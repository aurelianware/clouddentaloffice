using System.Data;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public interface IStripeDisputeWebhookProcessor
{
    Task ProcessAsync(StripeDisputeWebhookEvent webhook, CancellationToken cancellationToken = default);
}

/// <summary>
/// Card disputes (chargebacks) on a payment this app took. An open dispute is flagged for staff review
/// until Stripe closes it. A lost one takes the disputed amount back off the patient's payment, because
/// that money is gone, so the patient owes it again; a won or withdrawn one leaves the payment standing.
/// </summary>
public sealed class StripeDisputeWebhookProcessor(CloudDentalDbContext db, TimeProvider clock,
    StripePaymentMetrics metrics, ILogger<StripeDisputeWebhookProcessor> logger) : IStripeDisputeWebhookProcessor
{
    private const string Actor = "processor:Stripe";

    public async Task ProcessAsync(StripeDisputeWebhookEvent webhook, CancellationToken cancellationToken = default)
    {
        Validate(webhook);
        if (await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.ExternalEventId == webhook.ExternalEventId, cancellationToken)) return;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == webhook.TenantId &&
                x.Provider == PaymentProcessorProvider.Stripe, cancellationToken);
        if (configuration is null || configuration.ConnectedMerchantReference != webhook.ConnectedAccountId ||
            (configuration.Environment == PaymentProcessorEnvironment.Production) != webhook.LiveMode)
            throw new StripeWebhookPermanentException("Connected Stripe account mapping is invalid.");

        var now = clock.GetUtcNow().UtcDateTime;
        var payment = string.IsNullOrWhiteSpace(webhook.PaymentIntentId) ? null :
            await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
                x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.ExternalPaymentId == webhook.PaymentIntentId, cancellationToken);
        if (payment is not null && (payment.Status != PaymentStatus.Succeeded || !payment.LedgerEntryId.HasValue))
            throw new InvalidOperationException("The disputed payment has not been posted yet.");
        var processorEvent = new PaymentProcessorEvent
        {
            Id = Guid.NewGuid(), TenantId = webhook.TenantId, Processor = PaymentProcessorProvider.Stripe,
            ExternalEventId = webhook.ExternalEventId, ExternalPaymentId = webhook.PaymentIntentId,
            PaymentId = payment?.PaymentId, Status = PaymentProcessorEventStatus.Processed, CreatedAt = now, ProcessedAt = now
        };
        db.PaymentProcessorEvents.Add(processorEvent);

        if (payment is null)
        {
            // A dispute on a payment this app never took (for example one from a dashboard Payment Link).
            processorEvent.FailureCode = "not-an-app-payment";
            metrics.Ignored.Add(1);
        }
        else if (webhook.EventType == "charge.dispute.created")
        {
            await OpenIssueAsync(payment, webhook, "payment-disputed", now, cancellationToken);
            Audit(payment, "PaymentDisputed", "stripe-dispute-opened", now);
            metrics.Disputes.Add(1);
            logger.LogWarning("Stripe payment {PaymentId} was disputed.", payment.PaymentId);
        }
        else
        {
            var open = await db.PaymentReconciliationIssues.IgnoreQueryFilters().Where(x =>
                    x.TenantId == payment.TenantId && x.PaymentId == payment.PaymentId &&
                    x.IssueType == PaymentReconciliationIssueType.Dispute &&
                    x.Status == PaymentReconciliationIssueStatus.ReviewRequired && x.DiagnosticCode == "payment-disputed")
                .ToListAsync(cancellationToken);
            foreach (var issue in open)
            {
                issue.Status = PaymentReconciliationIssueStatus.Resolved;
                issue.ResolvedAt = now;
            }
            if (webhook.DisputeStatus == "lost")
                await PostLostDisputeAsync(payment, webhook, now, cancellationToken);
            else
                Audit(payment, "PaymentDisputeClosed", $"stripe-dispute-{webhook.DisputeStatus}", now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task PostLostDisputeAsync(PatientPayment payment, StripeDisputeWebhookEvent webhook, DateTime now,
        CancellationToken cancellationToken)
    {
        var sourceId = $"dispute-{webhook.ExternalDisputeId}";
        if (await db.PatientLedgerEntries.IgnoreQueryFilters().AnyAsync(x => x.TenantId == payment.TenantId &&
                x.SourceType == PatientLedgerSourceType.SystemReversal && x.SourceId == sourceId, cancellationToken))
            return;
        decimal? amount = null;
        try { amount = new Money(StripeCurrency.FromMinorUnits(webhook.AmountMinor, webhook.Currency), webhook.Currency).Amount; }
        catch (ArgumentException) { }
        if (amount is not { } lost || lost > payment.Amount ||
            !string.Equals(webhook.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase))
        {
            // Never post an amount we cannot reconcile with the payment; staff decide.
            await OpenIssueAsync(payment, webhook, "dispute-lost-amount-mismatch", now, cancellationToken);
            Audit(payment, "PaymentDisputeLost", "stripe-dispute-amount-mismatch", now);
            return;
        }

        var original = await db.PatientLedgerEntries.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == payment.TenantId && x.LedgerEntryId == payment.LedgerEntryId, cancellationToken);
        var reversal = new PatientLedgerEntry
        {
            LedgerEntryId = Guid.NewGuid(), TenantId = payment.TenantId, PatientAccountId = original.PatientAccountId,
            EntryType = PatientLedgerEntryType.PatientPayment, Amount = -lost, Currency = original.Currency,
            EffectiveDate = now, SourceType = PatientLedgerSourceType.SystemReversal, SourceId = sourceId,
            DescriptionCode = "dispute-lost", CreatedAt = now, CreatedBy = Actor
        };
        db.PatientLedgerEntries.Add(reversal);
        await PaymentAllocationUnwinder.UnapplyAsync(db, payment, lost, "dispute-lost", now, cancellationToken);
        if (lost == payment.Amount && !payment.ReversedAt.HasValue)
        {
            payment.ReversalLedgerEntryId = reversal.LedgerEntryId;
            payment.ReversedAt = now;
            payment.ReversedBy = Actor;
        }
        payment.UpdatedAt = now;
        // The patient's balance now shows the amount as owed again, on the ledger and in the audit trail.
        Audit(payment, "PaymentDisputeLost", "stripe-dispute-lost", now);
        logger.LogWarning("Stripe dispute on payment {PaymentId} was lost; the disputed amount was reversed.",
            payment.PaymentId);
    }

    private async Task OpenIssueAsync(PatientPayment payment, StripeDisputeWebhookEvent webhook, string code,
        DateTime now, CancellationToken cancellationToken)
    {
        if (await db.PaymentReconciliationIssues.IgnoreQueryFilters().AnyAsync(x => x.TenantId == payment.TenantId &&
                x.PaymentId == payment.PaymentId && x.IssueType == PaymentReconciliationIssueType.Dispute &&
                x.Status == PaymentReconciliationIssueStatus.ReviewRequired && x.DiagnosticCode == code, cancellationToken))
            return;
        db.PaymentReconciliationIssues.Add(new PaymentReconciliationIssue
        {
            Id = Guid.NewGuid(), TenantId = payment.TenantId, IssueType = PaymentReconciliationIssueType.Dispute,
            Status = PaymentReconciliationIssueStatus.ReviewRequired, PaymentId = payment.PaymentId,
            ExternalReference = StripePaymentReconciliationService.SafeReference(webhook.ExternalDisputeId),
            DiagnosticCode = code, DetectedAt = now
        });
    }

    private void Audit(PatientPayment payment, string action, string reasonCode, DateTime now) =>
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = payment.TenantId, Action = action, EntityType = nameof(PatientPayment),
            EntityId = payment.PaymentId.ToString("N"), Actor = Actor, ReasonCode = reasonCode, CreatedAt = now
        });

    private static void Validate(StripeDisputeWebhookEvent webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.TenantId) || string.IsNullOrWhiteSpace(webhook.ExternalEventId) ||
            string.IsNullOrWhiteSpace(webhook.ConnectedAccountId) || string.IsNullOrWhiteSpace(webhook.ExternalDisputeId) ||
            webhook.ExternalDisputeId.Length > 100 || webhook.AmountMinor <= 0 || webhook.Currency.Length != 3 ||
            string.IsNullOrWhiteSpace(webhook.DisputeStatus) || webhook.DisputeStatus.Length > 32 ||
            webhook.EventType is not ("charge.dispute.created" or "charge.dispute.closed"))
            throw new StripeWebhookPermanentException("Stripe dispute event is invalid.");
    }
}

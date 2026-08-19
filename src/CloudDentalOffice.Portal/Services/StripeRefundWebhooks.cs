using System.Data;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public interface IStripeRefundWebhookProcessor
{
    Task ProcessAsync(StripeRefundWebhookEvent webhook, CancellationToken cancellationToken = default);
}

public sealed class StripeRefundWebhookProcessor(CloudDentalDbContext db, TimeProvider clock,
    StripePaymentMetrics metrics, ILogger<StripeRefundWebhookProcessor> logger) : IStripeRefundWebhookProcessor
{
    public async Task ProcessAsync(StripeRefundWebhookEvent webhook, CancellationToken cancellationToken = default)
    {
        Validate(webhook);
        if (await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.ExternalEventId == webhook.ExternalEventId, cancellationToken)) return;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == webhook.TenantId &&
                x.Provider == PaymentProcessorProvider.Stripe && x.Enabled, cancellationToken);
        if (configuration is null || configuration.ConnectedMerchantReference != webhook.ConnectedAccountId ||
            (configuration.Environment == PaymentProcessorEnvironment.Production) != webhook.LiveMode)
            throw new StripeWebhookPermanentException("Connected Stripe account mapping is invalid or disabled.");

        var refund = await FindRefundAsync(webhook, cancellationToken)
            ?? throw new StripeWebhookPermanentException("Stripe refund reference is unknown.");
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == webhook.TenantId && x.PaymentId == refund.PaymentId, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var processorEvent = new PaymentProcessorEvent
        {
            Id = Guid.NewGuid(), TenantId = webhook.TenantId, Processor = PaymentProcessorProvider.Stripe,
            ExternalEventId = webhook.ExternalEventId, ExternalPaymentId = webhook.PaymentIntentId,
            PaymentId = payment.PaymentId, Status = PaymentProcessorEventStatus.Received, CreatedAt = now
        };
        db.PaymentProcessorEvents.Add(processorEvent);

        var conflict = ConflictCode(webhook, refund, payment);
        if (conflict is not null)
        {
            refund.Status = PatientRefundStatus.ReviewRequired;
            refund.FailureCode = conflict;
            processorEvent.Status = PaymentProcessorEventStatus.Conflict;
            processorEvent.FailureCode = conflict;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.Conflicts.Add(1);
            logger.LogWarning("Stripe refund event {ExternalEventId} requires review ({ConflictCode}).",
                webhook.ExternalEventId, conflict);
            return;
        }

        refund.ExternalRefundId ??= webhook.ExternalRefundId;
        if (webhook.EventType == "refund.failed" || webhook.RefundStatus is "failed" or "canceled")
        {
            refund.Status = PatientRefundStatus.Failed;
            refund.FailureCode = "stripe-refund-failed";
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.Failed.Add(1);
            return;
        }

        if (webhook.RefundStatus != "succeeded")
        {
            refund.Status = PatientRefundStatus.Pending;
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (!refund.LedgerEntryId.HasValue)
        {
            var ledger = new PatientLedgerEntry
            {
                LedgerEntryId = Guid.NewGuid(), TenantId = refund.TenantId,
                PatientAccountId = payment.PatientAccountId, EntryType = PatientLedgerEntryType.Refund,
                Amount = refund.Amount, Currency = refund.Currency, EffectiveDate = now,
                SourceType = PatientLedgerSourceType.Refund, SourceId = refund.RefundId.ToString("N"),
                DescriptionCode = "patient-refund", CreatedAt = now, CreatedBy = "processor:Stripe"
            };
            db.PatientLedgerEntries.Add(ledger);
            refund.LedgerEntryId = ledger.LedgerEntryId;
            await ReverseAllocationsAsync(payment, refund.Amount, now, cancellationToken);
            db.FinancialAuditEvents.Add(new FinancialAuditEvent
            {
                Id = Guid.NewGuid(), TenantId = refund.TenantId, Action = "RefundConfirmed",
                EntityType = nameof(PatientRefund), EntityId = refund.RefundId.ToString("N"),
                Actor = "processor:Stripe", ReasonCode = refund.Reason, CreatedAt = now
            });
        }
        refund.Status = PatientRefundStatus.Succeeded;
        refund.FailureCode = null;
        refund.CompletedAt = now;
        processorEvent.Status = PaymentProcessorEventStatus.Processed;
        processorEvent.ProcessedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Succeeded.Add(1);
    }

    private async Task<PatientRefund?> FindRefundAsync(StripeRefundWebhookEvent webhook,
        CancellationToken cancellationToken)
    {
        var query = db.PatientRefunds.IgnoreQueryFilters().Where(x => x.TenantId == webhook.TenantId &&
            x.Processor == PaymentProcessorProvider.Stripe);
        var byExternal = await query.SingleOrDefaultAsync(x => x.ExternalRefundId == webhook.ExternalRefundId,
            cancellationToken);
        if (byExternal is not null) return byExternal;
        return string.IsNullOrWhiteSpace(webhook.RefundReference) ? null : await query.SingleOrDefaultAsync(x =>
            x.InternalRefundReference == webhook.RefundReference, cancellationToken);
    }

    private async Task ReverseAllocationsAsync(PatientPayment payment, decimal refundAmount, DateTime now,
        CancellationToken cancellationToken)
    {
        var allocations = await db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x =>
                x.TenantId == payment.TenantId && x.PaymentId == payment.PaymentId && !x.UnappliedAt.HasValue)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.PaymentAllocationId)
            .ToListAsync(cancellationToken);
        var remaining = refundAmount;
        foreach (var allocation in allocations)
        {
            if (remaining <= 0) break;
            var removed = Math.Min(remaining, allocation.Amount);
            allocation.UnappliedAt = now;
            allocation.UnappliedBy = "processor:Stripe";
            allocation.UnapplyReasonCode = "refund";
            if (removed < allocation.Amount)
                db.PatientPaymentAllocations.Add(new PatientPaymentAllocation
                {
                    PaymentAllocationId = Guid.NewGuid(), TenantId = allocation.TenantId,
                    PaymentId = allocation.PaymentId, LedgerEntryId = allocation.LedgerEntryId,
                    Amount = allocation.Amount - removed, CreatedAt = now, CreatedBy = "processor:Stripe"
                });
            remaining -= removed;
        }
    }

    private static string? ConflictCode(StripeRefundWebhookEvent webhook, PatientRefund refund,
        PatientPayment payment)
    {
        if (!string.IsNullOrWhiteSpace(refund.ExternalRefundId) && refund.ExternalRefundId != webhook.ExternalRefundId)
            return "refund-id-mismatch";
        if (payment.ExternalPaymentId != webhook.PaymentIntentId) return "payment-intent-mismatch";
        if (!string.Equals(refund.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase)) return "currency-mismatch";
        return StripeCurrency.ToMinorUnits(new Money(refund.Amount, refund.Currency)) == webhook.AmountMinor
            ? null : "amount-mismatch";
    }

    private static void Validate(StripeRefundWebhookEvent webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.TenantId) || string.IsNullOrWhiteSpace(webhook.ExternalEventId) ||
            string.IsNullOrWhiteSpace(webhook.ConnectedAccountId) || string.IsNullOrWhiteSpace(webhook.ExternalRefundId) ||
            string.IsNullOrWhiteSpace(webhook.PaymentIntentId) || webhook.AmountMinor <= 0 || webhook.Currency.Length != 3 ||
            webhook.EventType is not ("refund.created" or "refund.updated" or "refund.failed"))
            throw new StripeWebhookPermanentException("Stripe refund event is invalid.");
    }
}

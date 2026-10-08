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
    private const string SucceededAfterFailure = "refund-succeeded-after-failure";
    private const string DashboardReason = "stripe-dashboard";

    public async Task ProcessAsync(StripeRefundWebhookEvent webhook, CancellationToken cancellationToken = default)
    {
        Validate(webhook);
        if (await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.ExternalEventId == webhook.ExternalEventId, cancellationToken)) return;

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        // Refunds already issued still settle after the practice disables online payments.
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == webhook.TenantId &&
                x.Provider == PaymentProcessorProvider.Stripe, cancellationToken);
        if (configuration is null || configuration.ConnectedMerchantReference != webhook.ConnectedAccountId ||
            (configuration.Environment == PaymentProcessorEnvironment.Production) != webhook.LiveMode)
            throw new StripeWebhookPermanentException("Connected Stripe account mapping is invalid.");

        var now = clock.GetUtcNow().UtcDateTime;
        var refund = await FindRefundAsync(webhook, cancellationToken);
        if (refund is null)
        {
            if (!string.IsNullOrWhiteSpace(webhook.RefundReference))
                throw new StripeWebhookPermanentException("Stripe refund reference is unknown.");
            // No reference of ours: the refund was made in the Stripe dashboard. If it refunds one of our
            // payments the patient's money still moved, so record it and post it like any other refund.
            var refunded = await FindPaymentAsync(webhook, cancellationToken);
            if (refunded is null)
            {
                await IgnoreAsync(webhook, now, transaction, cancellationToken);
                return;
            }
            refund = RecordDashboardRefund(webhook, refunded, now);
        }
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == webhook.TenantId && x.PaymentId == refund.PaymentId, cancellationToken);
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
            if (refund.LedgerEntryId is { } refundEntryId && refund.Status != PatientRefundStatus.Reversed)
            {
                // Stripe can fail a refund after it succeeded (for example, the card issuer rejects it).
                // The refund credit was already posted, so reverse it rather than leaving a refund that
                // never reached the patient on the ledger and outside the refund cap.
                await ReverseRefundEntryAsync(refund, refundEntryId, now, cancellationToken);
                refund.Status = PatientRefundStatus.Reversed;
                refund.FailureCode = "stripe-refund-failed-after-success";
            }
            else if (refund.Status != PatientRefundStatus.Reversed)
            {
                refund.Status = PatientRefundStatus.Failed;
                refund.FailureCode = "stripe-refund-failed";
            }
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.Failed.Add(1);
            return;
        }

        if (webhook.RefundStatus != "succeeded")
        {
            // Stripe delivers events out of order; a late pending event never moves a finished refund back.
            if (refund.Status is not (PatientRefundStatus.Succeeded or PatientRefundStatus.Failed or PatientRefundStatus.Reversed))
                refund.Status = PatientRefundStatus.Pending;
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (refund.Status is PatientRefundStatus.Failed or PatientRefundStatus.Reversed ||
            (refund.Status == PatientRefundStatus.ReviewRequired && refund.FailureCode == SucceededAfterFailure))
        {
            // A failed refund is final at Stripe, so a succeeded event after it is stale. Reversed refunds
            // stay reversed; a Failed one goes to review, and stays there for any later succeeded events,
            // rather than posting a credit on stale data.
            if (refund.Status == PatientRefundStatus.Failed)
            {
                refund.Status = PatientRefundStatus.ReviewRequired;
                refund.FailureCode = SucceededAfterFailure;
            }
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
            await PaymentAllocationUnwinder.UnapplyAsync(db, payment, refund.Amount, "refund", now, cancellationToken);
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

    private async Task<PatientPayment?> FindPaymentAsync(StripeRefundWebhookEvent webhook,
        CancellationToken cancellationToken)
    {
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
            x.ExternalPaymentId == webhook.PaymentIntentId, cancellationToken);
        // Stripe can only refund a captured charge, so a local payment not yet posted means its own
        // event is still on the way. Fail transiently so this event is retried after it.
        if (payment is not null && (payment.Status != PaymentStatus.Succeeded || !payment.LedgerEntryId.HasValue))
            throw new InvalidOperationException("The refunded payment has not been posted yet.");
        return payment;
    }

    private PatientRefund RecordDashboardRefund(StripeRefundWebhookEvent webhook, PatientPayment payment, DateTime now)
    {
        decimal amount;
        try { amount = new Money(StripeCurrency.FromMinorUnits(webhook.AmountMinor, webhook.Currency), webhook.Currency).Amount; }
        catch (ArgumentException) { throw new StripeWebhookPermanentException("Stripe refund amount is invalid."); }
        var refund = new PatientRefund
        {
            RefundId = Guid.NewGuid(), TenantId = payment.TenantId, PaymentId = payment.PaymentId,
            Amount = amount, Currency = webhook.Currency, Reason = DashboardReason,
            Processor = PaymentProcessorProvider.Stripe, InternalRefundReference = $"stripe-{webhook.ExternalRefundId}",
            ExternalRefundId = webhook.ExternalRefundId, Status = PatientRefundStatus.Requested,
            RequestedBy = "processor:Stripe", RequestedAt = now
        };
        db.PatientRefunds.Add(refund);
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = refund.TenantId, Action = "RefundRecordedFromStripe",
            EntityType = nameof(PatientRefund), EntityId = refund.RefundId.ToString("N"),
            Actor = "processor:Stripe", ReasonCode = DashboardReason, CreatedAt = now
        });
        logger.LogInformation("Recorded Stripe dashboard refund for payment {PaymentId}.", payment.PaymentId);
        return refund;
    }

    private async Task IgnoreAsync(StripeRefundWebhookEvent webhook, DateTime now,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        // A refund of a payment this app never took (for example one from a dashboard Payment Link).
        db.PaymentProcessorEvents.Add(new PaymentProcessorEvent
        {
            Id = Guid.NewGuid(), TenantId = webhook.TenantId, Processor = PaymentProcessorProvider.Stripe,
            ExternalEventId = webhook.ExternalEventId, ExternalPaymentId = webhook.PaymentIntentId,
            Status = PaymentProcessorEventStatus.Processed, FailureCode = "not-an-app-payment",
            CreatedAt = now, ProcessedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Ignored.Add(1);
    }

    private async Task ReverseRefundEntryAsync(PatientRefund refund, Guid refundEntryId, DateTime now,
        CancellationToken cancellationToken)
    {
        var original = await db.PatientLedgerEntries.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == refund.TenantId && x.LedgerEntryId == refundEntryId, cancellationToken);
        db.PatientLedgerEntries.Add(new PatientLedgerEntry
        {
            LedgerEntryId = Guid.NewGuid(), TenantId = original.TenantId, PatientAccountId = original.PatientAccountId,
            EntryType = original.EntryType, Amount = -original.Amount, Currency = original.Currency, EffectiveDate = now,
            SourceType = PatientLedgerSourceType.SystemReversal, SourceId = refund.RefundId.ToString("N"),
            DescriptionCode = "refund-reversal", CreatedAt = now, CreatedBy = "processor:Stripe",
            ReversalOfEntryId = original.LedgerEntryId
        });
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = refund.TenantId, Action = "RefundReversed",
            EntityType = nameof(PatientRefund), EntityId = refund.RefundId.ToString("N"),
            Actor = "processor:Stripe", ReasonCode = "stripe-refund-failed-after-success", CreatedAt = now
        });
    }

    private static string? ConflictCode(StripeRefundWebhookEvent webhook, PatientRefund refund,
        PatientPayment payment)
    {
        if (!string.IsNullOrWhiteSpace(refund.ExternalRefundId) && refund.ExternalRefundId != webhook.ExternalRefundId)
            return "refund-id-mismatch";
        if (payment.ExternalPaymentId != webhook.PaymentIntentId) return "payment-intent-mismatch";
        if (!string.Equals(refund.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(refund.Currency, payment.Currency, StringComparison.OrdinalIgnoreCase)) return "currency-mismatch";
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

/// <summary>Takes money that left the practice back off the charges a payment was applied to, newest first.</summary>
internal static class PaymentAllocationUnwinder
{
    public static async Task UnapplyAsync(CloudDentalDbContext db, PatientPayment payment, decimal amount,
        string reasonCode, DateTime now, CancellationToken cancellationToken)
    {
        var allocations = await db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x =>
                x.TenantId == payment.TenantId && x.PaymentId == payment.PaymentId && !x.UnappliedAt.HasValue)
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.PaymentAllocationId)
            .ToListAsync(cancellationToken);
        var remaining = amount;
        foreach (var allocation in allocations)
        {
            if (remaining <= 0) break;
            var removed = Math.Min(remaining, allocation.Amount);
            allocation.UnappliedAt = now;
            allocation.UnappliedBy = "processor:Stripe";
            allocation.UnapplyReasonCode = reasonCode;
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
}

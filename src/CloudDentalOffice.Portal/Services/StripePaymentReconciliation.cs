using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public sealed record StripeReconciliationDiagnostic(Guid Id, PaymentReconciliationIssueType Type,
    string Code, string? SafeExternalReference, DateTime DetectedAt);
public sealed record StripeReconciliationSummary(int PaymentsChecked, int RefundsChecked,
    int ReviewRequired, IReadOnlyList<StripeReconciliationDiagnostic> Diagnostics);

public interface IStripePaymentReconciliationService
{
    Task<StripeReconciliationSummary> ReconcileAsync(string tenantId, DateTime since,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StripeReconciliationDiagnostic>> ListAsync(string tenantId,
        CancellationToken cancellationToken = default);
}

public sealed class StripePaymentReconciliationService(CloudDentalDbContext db, IStripeApiClient stripe,
    ITenantProvider tenantProvider, TimeProvider clock) : IStripePaymentReconciliationService
{
    public async Task<StripeReconciliationSummary> ReconcileAsync(string tenantId, DateTime since,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        var now = clock.GetUtcNow().UtcDateTime;
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Provider == PaymentProcessorProvider.Stripe,
                cancellationToken);
        if (configuration is null || !configuration.Enabled ||
            string.IsNullOrWhiteSpace(configuration.ConnectedMerchantReference))
        {
            await ReplaceIssuesAsync(tenantId,
                [Candidate(PaymentReconciliationIssueType.DisconnectedAccount, null, null,
                    "stripe-account-disconnected")], now, cancellationToken);
            if (configuration is not null)
                await RecordRunAsync(tenantId, now, "review-required", cancellationToken);
            return new(0, 0, 1, await ListAsync(tenantId, cancellationToken));
        }

        var connectedAccount = configuration.ConnectedMerchantReference;
        var localPayments = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Processor == PaymentProcessorProvider.Stripe && x.CreatedAt >= since)
            .ToListAsync(cancellationToken);
        var localRefunds = await db.PatientRefunds.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Processor == PaymentProcessorProvider.Stripe && x.RequestedAt >= since)
            .ToListAsync(cancellationToken);
        var remotePayments = await stripe.ListPaymentsAsync(configuration, connectedAccount, since, cancellationToken);
        var remoteRefunds = await stripe.ListRefundsAsync(configuration, connectedAccount, since, cancellationToken);
        var candidates = new List<IssueCandidate>();

        foreach (var payment in localPayments)
        {
            if (payment.Status == PaymentStatus.Pending && now - payment.CreatedAt > TimeSpan.FromHours(24))
                candidates.Add(Candidate(PaymentReconciliationIssueType.PendingTooLong, payment.PaymentId, null,
                    "payment-pending-over-24-hours", payment.ExternalPaymentId));
            if (payment.Status != PaymentStatus.Succeeded || string.IsNullOrWhiteSpace(payment.ExternalPaymentId)) continue;
            var remote = remotePayments.SingleOrDefault(x => x.Id == payment.ExternalPaymentId);
            if (remote is null)
                candidates.Add(Candidate(PaymentReconciliationIssueType.MissingStripePayment, payment.PaymentId,
                    null, "stripe-payment-not-found", payment.ExternalPaymentId));
            else
            {
                if (remote.AmountReceived != StripeCurrency.ToMinorUnits(new Money(payment.Amount, payment.Currency)))
                    candidates.Add(Candidate(PaymentReconciliationIssueType.AmountMismatch, payment.PaymentId,
                        null, "payment-amount-mismatch", remote.Id));
                if (!remote.Currency.Equals(payment.Currency, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(Candidate(PaymentReconciliationIssueType.CurrencyMismatch, payment.PaymentId,
                        null, "payment-currency-mismatch", remote.Id));
            }
        }
        var localPaymentIds = localPayments.Where(x => x.ExternalPaymentId is not null)
            .Select(x => x.ExternalPaymentId!).ToHashSet(StringComparer.Ordinal);
        candidates.AddRange(remotePayments.Where(x => !localPaymentIds.Contains(x.Id)).Select(x =>
            Candidate(PaymentReconciliationIssueType.UnknownStripePayment, null, null,
                "unknown-stripe-payment", x.Id)));

        foreach (var refund in localRefunds.Where(x => x.Status is PatientRefundStatus.Pending or
                     PatientRefundStatus.Succeeded or PatientRefundStatus.ReviewRequired))
        {
            var remote = string.IsNullOrWhiteSpace(refund.ExternalRefundId) ? null :
                remoteRefunds.SingleOrDefault(x => x.Id == refund.ExternalRefundId);
            if (remote is null || remote.Amount != StripeCurrency.ToMinorUnits(new Money(refund.Amount, refund.Currency)) ||
                !remote.Currency.Equals(refund.Currency, StringComparison.OrdinalIgnoreCase) ||
                (refund.Status == PatientRefundStatus.Succeeded && remote.Status != "succeeded"))
                candidates.Add(Candidate(PaymentReconciliationIssueType.RefundMismatch, refund.PaymentId,
                    refund.RefundId, "refund-state-mismatch", refund.ExternalRefundId));
        }
        await ReplaceIssuesAsync(tenantId, candidates, now, cancellationToken);
        var diagnostics = await ListAsync(tenantId, cancellationToken);
        await RecordRunAsync(tenantId, now, diagnostics.Count == 0 ? "clean" : "review-required", cancellationToken);
        return new(localPayments.Count, localRefunds.Count, diagnostics.Count, diagnostics);
    }

    public async Task<IReadOnlyList<StripeReconciliationDiagnostic>> ListAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        return await db.PaymentReconciliationIssues.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Status == PaymentReconciliationIssueStatus.ReviewRequired)
            .OrderByDescending(x => x.DetectedAt).Select(x => new StripeReconciliationDiagnostic(x.Id,
                x.IssueType, x.DiagnosticCode, x.ExternalReference, x.DetectedAt)).ToListAsync(cancellationToken);
    }

    private async Task ReplaceIssuesAsync(string tenantId, IReadOnlyCollection<IssueCandidate> candidates,
        DateTime now, CancellationToken cancellationToken)
    {
        var existing = await db.PaymentReconciliationIssues.IgnoreQueryFilters().Where(x =>
            x.TenantId == tenantId && x.Status == PaymentReconciliationIssueStatus.ReviewRequired)
            .ToListAsync(cancellationToken);
        foreach (var issue in existing)
        {
            if (candidates.Any(x => Same(issue, x))) continue;
            issue.Status = PaymentReconciliationIssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
        foreach (var candidate in candidates.Where(x => !existing.Any(issue => Same(issue, x))))
            db.PaymentReconciliationIssues.Add(new PaymentReconciliationIssue
            {
                Id = Guid.NewGuid(), TenantId = tenantId, IssueType = candidate.Type,
                Status = PaymentReconciliationIssueStatus.ReviewRequired, PaymentId = candidate.PaymentId,
                RefundId = candidate.RefundId, ExternalReference = SafeReference(candidate.ExternalReference),
                DiagnosticCode = candidate.Code, DetectedAt = now
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool Same(PaymentReconciliationIssue issue, IssueCandidate candidate) =>
        issue.IssueType == candidate.Type && issue.PaymentId == candidate.PaymentId &&
        issue.RefundId == candidate.RefundId && issue.DiagnosticCode == candidate.Code;
    private static IssueCandidate Candidate(PaymentReconciliationIssueType type, Guid? paymentId, Guid? refundId,
        string code, string? external = null) => new(type, paymentId, refundId, code, external);
    private static string? SafeReference(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        value.Length <= 8 ? value : $"…{value[^8..]}";

    private async Task RecordRunAsync(string tenantId, DateTime now, string status,
        CancellationToken cancellationToken)
    {
        await db.PaymentProcessorConfigurations.IgnoreQueryFilters().Where(x =>
                x.TenantId == tenantId && x.Provider == PaymentProcessorProvider.Stripe)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LastReconciliationAt, now)
                .SetProperty(x => x.LastReconciliationStatusCode, status), cancellationToken);
    }
    private sealed record IssueCandidate(PaymentReconciliationIssueType Type, Guid? PaymentId, Guid? RefundId,
        string Code, string? ExternalReference);
}

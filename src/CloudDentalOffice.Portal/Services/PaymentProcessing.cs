using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CloudDentalOffice.Portal.Services;

public sealed record PaymentRequest(string TenantId, Guid PatientAccountId, Guid? StatementId,
    Money Amount, string InternalPaymentReference, PatientPaymentMethod Method,
    string? SuccessUrl = null, string? CancelUrl = null);

public sealed record PaymentSession(string InternalPaymentReference, string ExternalSessionId,
    string? ExternalPaymentId, Uri? CheckoutUrl, string? ClientToken, DateTime? ExpiresAt, PaymentStatus Status);

public sealed record PaymentRefundRequest(string TenantId, Guid PaymentId, Money Amount,
    string InternalRefundReference, string Reason = "requested_by_customer", string RequestedBy = "system");

public sealed record PaymentRefundResult(string InternalRefundReference, string? ExternalRefundId,
    PaymentStatus Status);

public sealed record ProcessorPaymentEvent(string TenantId, PaymentProcessorProvider Processor,
    string ExternalEventId, string ExternalPaymentId, string InternalPaymentReference,
    Money Amount, PaymentStatus Status, DateTime OccurredAt);

public sealed record PaymentReconciliationResult(Guid PaymentId, bool Duplicate, PaymentStatus Status,
    Guid? LedgerEntryId);

public sealed record PaymentAllocationResult(Guid PaymentId, decimal PaymentAmount, decimal AllocatedAmount,
    decimal UnappliedAmount, string Currency);

public interface IPaymentProcessor
{
    PaymentProcessorProvider Provider { get; }
    Task<PaymentSession> CreateSessionAsync(PaymentProcessorConfiguration configuration, PaymentRequest request,
        CancellationToken cancellationToken = default);
    Task<PaymentRefundResult> RefundAsync(PaymentProcessorConfiguration configuration, PaymentRefundRequest request,
        string externalPaymentId, CancellationToken cancellationToken = default);
}

public interface IPaymentProcessorResolver
{
    Task<(IPaymentProcessor Processor, PaymentProcessorConfiguration Configuration)> ResolveAsync(
        string tenantId, CancellationToken cancellationToken = default);
}

public interface IPaymentCheckoutService
{
    Task<PaymentSession> CreateAsync(PaymentRequest request, CancellationToken cancellationToken = default);
}

public interface IPaymentRefundService
{
    Task<PaymentRefundResult> RefundAsync(PaymentRefundRequest request, CancellationToken cancellationToken = default);
    Task<PaymentRefundResult> RetryAsync(string tenantId, Guid refundId, string requestedBy,
        CancellationToken cancellationToken = default);
}

public interface IPaymentReconciliationService
{
    Task<PaymentReconciliationResult> ReconcileAsync(ProcessorPaymentEvent paymentEvent,
        CancellationToken cancellationToken = default);
}

public interface IPaymentAllocationService
{
    Task<PaymentAllocationResult> AllocateAsync(string tenantId, Guid paymentId, Guid ledgerEntryId,
        Money amount, string createdBy, CancellationToken cancellationToken = default);
    Task<PaymentAllocationResult> GetAllocationAsync(string tenantId, Guid paymentId,
        CancellationToken cancellationToken = default);
}

public sealed class PaymentProcessorUnavailableException(string message) : InvalidOperationException(message);

public sealed class PaymentProcessorResolver(CloudDentalDbContext db, IEnumerable<IPaymentProcessor> processors,
    ITenantProvider tenantProvider) : IPaymentProcessorResolver
{
    private readonly IReadOnlyDictionary<PaymentProcessorProvider, IPaymentProcessor> _processors =
        BuildProcessorMap(processors);

    public async Task<(IPaymentProcessor Processor, PaymentProcessorConfiguration Configuration)> ResolveAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        var configurations = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Enabled).ToListAsync(cancellationToken);
        if (configurations.Count == 0)
            throw new PaymentProcessorUnavailableException("No payment processor is enabled for the tenant.");
        if (configurations.Count > 1)
            throw new PaymentProcessorUnavailableException("Multiple payment processors are enabled; select one before processing payments.");
        var configuration = configurations[0];
        if (string.IsNullOrWhiteSpace(configuration.CredentialReference))
            throw new PaymentProcessorUnavailableException("The enabled payment processor has no credential reference.");
        if (!_processors.TryGetValue(configuration.Provider, out var processor))
            throw new PaymentProcessorUnavailableException("The configured payment processor adapter is not installed.");
        return (processor, configuration);
    }

    private static IReadOnlyDictionary<PaymentProcessorProvider, IPaymentProcessor> BuildProcessorMap(
        IEnumerable<IPaymentProcessor> processors)
    {
        var groups = processors.GroupBy(x => x.Provider).ToList();
        var duplicate = groups.FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Multiple payment processor adapters are registered for {duplicate.Key}.");
        return groups.ToDictionary(x => x.Key, x => x.Single());
    }
}

public sealed class PaymentCheckoutService(CloudDentalDbContext db, IPaymentProcessorResolver resolver,
    ITenantProvider tenantProvider, TimeProvider clock) : IPaymentCheckoutService
{
    public async Task<PaymentSession> CreateAsync(PaymentRequest request, CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, request.TenantId);
        ValidateReference(request.InternalPaymentReference, nameof(request.InternalPaymentReference));
        if (request.Amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(request.Amount));
        var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId && x.Id == request.PatientAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient account was not found for the tenant.");
        if (request.StatementId.HasValue && !await db.PatientStatements.IgnoreQueryFilters().AnyAsync(x =>
                x.TenantId == request.TenantId && x.StatementId == request.StatementId &&
                x.PatientAccountId == account.Id && x.Currency == request.Amount.Currency, cancellationToken))
            throw new KeyNotFoundException("Statement was not found for the patient account and currency.");
        var accountCurrency = await db.PatientLedgerEntries.IgnoreQueryFilters().Where(x =>
            x.TenantId == request.TenantId && x.PatientAccountId == account.Id).Select(x => x.Currency)
            .FirstOrDefaultAsync(cancellationToken);
        if (accountCurrency is not null && !accountCurrency.Equals(request.Amount.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payment currency does not match the patient account.");

        var (processor, configuration) = await resolver.ResolveAsync(request.TenantId, cancellationToken);
        if (await db.PatientPayments.IgnoreQueryFilters().AnyAsync(x => x.TenantId == request.TenantId &&
                x.InternalPaymentReference == request.InternalPaymentReference, cancellationToken))
            throw new InvalidOperationException("The internal payment reference already exists.");
        var now = clock.GetUtcNow().UtcDateTime;
        var payment = new PatientPayment
        {
            PaymentId = Guid.NewGuid(), TenantId = request.TenantId, PatientAccountId = request.PatientAccountId,
            StatementId = request.StatementId, Amount = request.Amount.Amount, Currency = request.Amount.Currency,
            PaymentDate = now, Method = request.Method, Processor = processor.Provider,
            InternalPaymentReference = request.InternalPaymentReference.Trim(), Status = PaymentStatus.Pending,
            CreatedAt = now, UpdatedAt = now
        };
        db.PatientPayments.Add(payment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                    x.TenantId == request.TenantId &&
                    x.InternalPaymentReference == request.InternalPaymentReference, cancellationToken))
                throw new InvalidOperationException("The internal payment reference already exists.");
            throw;
        }
        try
        {
            var session = await processor.CreateSessionAsync(configuration, request, cancellationToken);
            if (!string.Equals(session.InternalPaymentReference, payment.InternalPaymentReference, StringComparison.Ordinal))
                throw new InvalidOperationException("Processor returned a mismatched internal payment reference.");
            payment.ExternalSessionId = BoundedExternal(session.ExternalSessionId, nameof(session.ExternalSessionId));
            payment.ExternalPaymentId = BoundedExternal(session.ExternalPaymentId, nameof(session.ExternalPaymentId));
            payment.Status = session.Status;
            payment.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            return session;
        }
        catch
        {
            payment.Status = PaymentStatus.Failed;
            payment.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    internal static void ValidateReference(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim() ||
            !value.All(x => char.IsLetterOrDigit(x) || x is '-' or '_' or '.'))
            throw new ArgumentException("Payment references must be 1-128 letters, digits, hyphens, underscores, or periods.", parameter);
    }

    internal static string? BoundedExternal(string? value, string parameter)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
            throw new ArgumentException("External payment identifiers must be 1-128 characters.", parameter);
        return value.Trim();
    }
}

public sealed class PaymentRefundService(CloudDentalDbContext db, IPaymentProcessorResolver resolver,
    ITenantProvider tenantProvider, TimeProvider clock) : IPaymentRefundService
{
    public PaymentRefundService(CloudDentalDbContext db, IPaymentProcessorResolver resolver,
        ITenantProvider tenantProvider) : this(db, resolver, tenantProvider, TimeProvider.System) { }
    public async Task<PaymentRefundResult> RefundAsync(PaymentRefundRequest request,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, request.TenantId);
        PaymentCheckoutService.ValidateReference(request.InternalRefundReference, nameof(request.InternalRefundReference));
        ValidateActorAndReason(request.RequestedBy, request.Reason);
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId && x.PaymentId == request.PaymentId, cancellationToken)
            ?? throw new KeyNotFoundException("Payment was not found for the tenant.");
        if (payment.Status != PaymentStatus.Succeeded || string.IsNullOrWhiteSpace(payment.ExternalPaymentId))
            throw new InvalidOperationException("Only a succeeded processor payment can be refunded.");
        if (request.Amount.Amount <= 0 || request.Amount.Amount > payment.Amount || request.Amount.Currency != payment.Currency)
            throw new ArgumentOutOfRangeException(nameof(request.Amount));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var reserved = await db.PatientRefunds.IgnoreQueryFilters().Where(x => x.TenantId == request.TenantId &&
                x.PaymentId == request.PaymentId && x.Status != PatientRefundStatus.Failed)
            .Select(x => x.Amount).ToListAsync(cancellationToken);
        if (reserved.Sum() + request.Amount.Amount > payment.Amount)
            throw new InvalidOperationException("Cumulative refunds cannot exceed the settled payment amount.");
        if (await db.PatientRefunds.IgnoreQueryFilters().AnyAsync(x => x.TenantId == request.TenantId &&
            x.InternalRefundReference == request.InternalRefundReference, cancellationToken))
            throw new InvalidOperationException("The refund reference already exists.");
        var (processor, configuration) = await resolver.ResolveAsync(request.TenantId, cancellationToken);
        if (processor.Provider != payment.Processor)
            throw new PaymentProcessorUnavailableException("The original payment processor is not the enabled processor.");
        var refund = new PatientRefund
        {
            RefundId = Guid.NewGuid(), TenantId = request.TenantId, PaymentId = payment.PaymentId,
            Amount = request.Amount.Amount, Currency = request.Amount.Currency, Reason = request.Reason.Trim(),
            Processor = payment.Processor, InternalRefundReference = request.InternalRefundReference,
            Status = PatientRefundStatus.Requested, RequestedBy = request.RequestedBy.Trim(),
            RequestedAt = clock.GetUtcNow().UtcDateTime
        };
        db.PatientRefunds.Add(refund);
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = request.TenantId, Action = "RefundRequested",
            EntityType = nameof(PatientRefund), EntityId = refund.RefundId.ToString("N"),
            Actor = request.RequestedBy.Trim(), ReasonCode = request.Reason.Trim(), CreatedAt = refund.RequestedAt
        });
        await db.SaveChangesAsync(cancellationToken); // Durable CDO intent precedes the remote call.
        await transaction.CommitAsync(cancellationToken);
        return await SubmitAsync(refund, payment, configuration, processor, request, cancellationToken);
    }

    public async Task<PaymentRefundResult> RetryAsync(string tenantId, Guid refundId, string requestedBy,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        ValidateActorAndReason(requestedBy, "retry");
        var refund = await db.PatientRefunds.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.RefundId == refundId, cancellationToken)
            ?? throw new KeyNotFoundException("Refund was not found for the tenant.");
        if (refund.Status != PatientRefundStatus.Failed || !string.IsNullOrWhiteSpace(refund.ExternalRefundId))
            throw new InvalidOperationException("Only a failed refund without a confirmed Stripe refund can be retried.");
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == tenantId && x.PaymentId == refund.PaymentId, cancellationToken);
        var (processor, configuration) = await resolver.ResolveAsync(tenantId, cancellationToken);
        if (processor.Provider != refund.Processor || string.IsNullOrWhiteSpace(payment.ExternalPaymentId))
            throw new PaymentProcessorUnavailableException("The original payment processor is unavailable.");
        refund.Status = PatientRefundStatus.Requested;
        refund.FailureCode = null;
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Action = "RefundRetried",
            EntityType = nameof(PatientRefund), EntityId = refund.RefundId.ToString("N"),
            Actor = requestedBy.Trim(), ReasonCode = "retry", CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        await db.SaveChangesAsync(cancellationToken);
        var request = new PaymentRefundRequest(tenantId, payment.PaymentId,
            new Money(refund.Amount, refund.Currency), refund.InternalRefundReference, refund.Reason, requestedBy);
        return await SubmitAsync(refund, payment, configuration, processor, request, cancellationToken);
    }

    private async Task<PaymentRefundResult> SubmitAsync(PatientRefund refund, PatientPayment payment,
        PaymentProcessorConfiguration configuration, IPaymentProcessor processor, PaymentRefundRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await processor.RefundAsync(configuration, request, payment.ExternalPaymentId!, cancellationToken);
            refund.ExternalRefundId = PaymentCheckoutService.BoundedExternal(result.ExternalRefundId, nameof(result.ExternalRefundId));
            refund.Status = result.Status == PaymentStatus.Failed ? PatientRefundStatus.Failed : PatientRefundStatus.Pending;
            refund.FailureCode = result.Status == PaymentStatus.Failed ? "stripe-refund-rejected" : null;
            await db.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is StripeConnectException or PaymentProcessorUnavailableException or HttpRequestException)
        {
            refund.Status = PatientRefundStatus.Failed;
            refund.FailureCode = "stripe-refund-request-failed";
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateActorAndReason(string actor, string reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 100)
            throw new ArgumentException("A bounded refund actor is required.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 64)
            throw new ArgumentException("A bounded refund reason is required.");
    }
}

public sealed class PaymentReconciliationService(CloudDentalDbContext db, IPatientAccountService accounts,
    ITenantProvider tenantProvider, TimeProvider clock) : IPaymentReconciliationService
{
    public async Task<PaymentReconciliationResult> ReconcileAsync(ProcessorPaymentEvent paymentEvent,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, paymentEvent.TenantId);
        PaymentCheckoutService.ValidateReference(paymentEvent.InternalPaymentReference, nameof(paymentEvent.InternalPaymentReference));
        PaymentCheckoutService.ValidateReference(paymentEvent.ExternalEventId, nameof(paymentEvent.ExternalEventId));
        PaymentCheckoutService.BoundedExternal(paymentEvent.ExternalPaymentId, nameof(paymentEvent.ExternalPaymentId));
        var prior = await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == paymentEvent.TenantId && x.Processor == paymentEvent.Processor &&
            x.ExternalEventId == paymentEvent.ExternalEventId, cancellationToken);
        if (prior is not null)
            return new(prior.PaymentId ?? Guid.Empty, true,
                prior.PaymentId.HasValue ? await Status(prior.PaymentId.Value, paymentEvent.TenantId, cancellationToken) : PaymentStatus.Failed,
                prior.PaymentId.HasValue ? await LedgerId(prior.PaymentId.Value, paymentEvent.TenantId, cancellationToken) : null);

        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == paymentEvent.TenantId && x.Processor == paymentEvent.Processor &&
            x.InternalPaymentReference == paymentEvent.InternalPaymentReference, cancellationToken)
            ?? throw new KeyNotFoundException("Payment was not found for the tenant and processor.");
        if (payment.Amount != paymentEvent.Amount.Amount || payment.Currency != paymentEvent.Amount.Currency)
            throw new InvalidOperationException("Processor event amount or currency does not match the CDO payment.");
        if (payment.ExternalPaymentId is not null && payment.ExternalPaymentId != paymentEvent.ExternalPaymentId)
            throw new InvalidOperationException("Processor event external payment ID does not match the CDO payment.");
        var now = clock.GetUtcNow().UtcDateTime;
        var inbox = new PaymentProcessorEvent
        {
            Id = Guid.NewGuid(), TenantId = paymentEvent.TenantId, Processor = paymentEvent.Processor,
            ExternalEventId = paymentEvent.ExternalEventId.Trim(), ExternalPaymentId = paymentEvent.ExternalPaymentId.Trim(),
            PaymentId = payment.PaymentId, Status = PaymentProcessorEventStatus.Received, CreatedAt = now
        };
        try
        {
            db.PaymentProcessorEvents.Add(inbox);
            payment.ExternalPaymentId = paymentEvent.ExternalPaymentId.Trim();
            payment.Status = paymentEvent.Status;
            payment.PaymentDate = NormalizeUtc(paymentEvent.OccurredAt);
            payment.UpdatedAt = now;
            if (paymentEvent.Status == PaymentStatus.Succeeded && !payment.LedgerEntryId.HasValue)
            {
                var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(x =>
                    x.TenantId == paymentEvent.TenantId && x.Id == payment.PatientAccountId, cancellationToken);
                var ledger = await accounts.PostAsync(new PostPatientLedgerEntry(paymentEvent.TenantId, account.PatientId,
                    PatientLedgerEntryType.PatientPayment, paymentEvent.Amount, payment.PaymentDate,
                    PatientLedgerSourceType.PatientPayment, payment.PaymentId.ToString("N"), "patient-payment",
                    $"processor:{paymentEvent.Processor}"), cancellationToken);
                payment.LedgerEntryId = ledger.LedgerEntryId;
            }
            inbox.Status = PaymentProcessorEventStatus.Processed;
            inbox.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new(payment.PaymentId, false, payment.Status, payment.LedgerEntryId);
        }
        catch (Exception ex) when (ex is DbUpdateException or DuplicateLedgerSourceException)
        {
            db.ChangeTracker.Clear();
            var concurrent = await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == paymentEvent.TenantId && x.Processor == paymentEvent.Processor &&
                x.ExternalEventId == paymentEvent.ExternalEventId, cancellationToken);
            if (concurrent is null) throw;
            return new(concurrent.PaymentId ?? Guid.Empty, true,
                concurrent.PaymentId.HasValue
                    ? await Status(concurrent.PaymentId.Value, paymentEvent.TenantId, cancellationToken)
                    : PaymentStatus.Failed,
                concurrent.PaymentId.HasValue
                    ? await LedgerId(concurrent.PaymentId.Value, paymentEvent.TenantId, cancellationToken)
                    : null);
        }
    }

    private async Task<PaymentStatus> Status(Guid id, string tenant, CancellationToken cancellationToken) =>
        await db.PatientPayments.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.PaymentId == id)
            .Select(x => x.Status).SingleAsync(cancellationToken);
    private async Task<Guid?> LedgerId(Guid id, string tenant, CancellationToken cancellationToken) =>
        await db.PatientPayments.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.PaymentId == id)
            .Select(x => x.LedgerEntryId).SingleAsync(cancellationToken);
    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value, DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed class PaymentAllocationService(CloudDentalDbContext db, ITenantProvider tenantProvider,
    TimeProvider clock) : IPaymentAllocationService
{
    public async Task<PaymentAllocationResult> AllocateAsync(string tenantId, Guid paymentId, Guid ledgerEntryId,
        Money amount, string createdBy, CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        if (amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Trim().Length > 100)
            throw new ArgumentException("A bounded allocation actor is required.", nameof(createdBy));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var payment = await db.PatientPayments.IgnoreQueryFilters().Include(x => x.Allocations).SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.PaymentId == paymentId, cancellationToken)
            ?? throw new KeyNotFoundException("Payment was not found for the tenant.");
        if (payment.Status != PaymentStatus.Succeeded) throw new InvalidOperationException("Only succeeded payments can be allocated.");
        if (payment.Currency != amount.Currency) throw new InvalidOperationException("Allocation currency does not match the payment.");
        var allocatableTypes = new[] { PatientLedgerEntryType.Charge, PatientLedgerEntryType.DebitAdjustment,
            PatientLedgerEntryType.Refund, PatientLedgerEntryType.Transfer };
        var ledgerEntry = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId &&
                x.LedgerEntryId == ledgerEntryId && x.PatientAccountId == payment.PatientAccountId &&
                allocatableTypes.Contains(x.EntryType), cancellationToken);
        if (ledgerEntry is null || ledgerEntry.Amount <= 0)
            throw new KeyNotFoundException("Allocatable ledger entry was not found for the patient account.");
        var allocated = payment.Allocations.Where(x => !x.UnappliedAt.HasValue).Sum(x => x.Amount);
        if (allocated + amount.Amount > payment.Amount) throw new InvalidOperationException("Allocations cannot exceed the payment amount.");
        if (payment.Allocations.Any(x => x.LedgerEntryId == ledgerEntryId && !x.UnappliedAt.HasValue))
            throw new InvalidOperationException("Use one active allocation per payment and ledger entry.");
        var targetAllocationAmounts = await db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x =>
            x.TenantId == tenantId && x.LedgerEntryId == ledgerEntryId && !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync(cancellationToken);
        if (targetAllocationAmounts.Sum() + amount.Amount > ledgerEntry.Amount)
            throw new InvalidOperationException("Allocations cannot exceed the target ledger amount.");
        db.PatientPaymentAllocations.Add(new PatientPaymentAllocation
        {
            PaymentAllocationId = Guid.NewGuid(), TenantId = tenantId, PaymentId = paymentId,
            LedgerEntryId = ledgerEntryId, Amount = amount.Amount, CreatedAt = clock.GetUtcNow().UtcDateTime,
            CreatedBy = createdBy.Trim()
        });
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Action = "PaymentAllocated", EntityType = "PatientPayment",
            EntityId = paymentId.ToString("N"), Actor = createdBy.Trim(), CreatedAt = clock.GetUtcNow().UtcDateTime
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("The payment was concurrently allocated; reload its allocations and try again.");
        }
        return new(payment.PaymentId, payment.Amount, allocated + amount.Amount,
            payment.Amount - allocated - amount.Amount, payment.Currency);
    }

    public async Task<PaymentAllocationResult> GetAllocationAsync(string tenantId, Guid paymentId,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, tenantId);
        var payment = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.PaymentId == paymentId, cancellationToken)
            ?? throw new KeyNotFoundException("Payment was not found for the tenant.");
        var allocationAmounts = await db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x =>
            x.TenantId == tenantId && x.PaymentId == paymentId && !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync(cancellationToken);
        var allocated = allocationAmounts.Sum();
        return new(payment.PaymentId, payment.Amount, allocated, payment.Amount - allocated, payment.Currency);
    }
}

internal static class PaymentTenantGuard
{
    public static void Ensure(ITenantProvider tenantProvider, string tenantId)
    {
        var trusted = tenantProvider.User is { } user
            ? PatientAccountApi.TrustedTenantId(user) ?? tenantProvider.TenantId
            : tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || !string.Equals(trusted, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Payment tenant context does not match the authenticated tenant.");
    }
}

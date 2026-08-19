using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public static class BillingPermissions
{
    public const string View = "Billing.View";
    public const string PostPayment = "Billing.PostPayment";
    public const string Adjust = "Billing.Adjust";
    public const string Refund = "Billing.Refund";
    public const string ConfigurePayments = "Billing.ConfigurePayments";
    public const string ClaimType = "permission";

    public static bool Has(ClaimsPrincipal user, string permission)
    {
        if (user.IsInRole("Admin") || user.IsInRole("BillingAdmin")) return true;
        if (user.Claims.Any(x => x.Type == ClaimType && x.Value.Equals(permission, StringComparison.OrdinalIgnoreCase))) return true;
        return user.IsInRole("BillingStaff") && permission is View or PostPayment;
    }
}

public sealed record StaffBillingDashboard(decimal TodayCollected, decimal OnlinePayments, decimal OfficePayments,
    decimal FailedOnlinePayments, decimal Refunds, string Currency);
public sealed record StaffBillingPayment(Guid PaymentId, decimal Amount, string Currency, DateTime PaymentDate,
    PatientPaymentMethod Method, PaymentProcessorProvider Processor, PaymentStatus Status, string SafeReference,
    decimal Allocated, decimal Unapplied, string RefundStatus, Guid? LedgerEntryId, bool CanReverse,
    IReadOnlyList<PatientPaymentAllocation> Allocations);
public sealed record StaffPatientBillingAccount(int PatientId, string PatientName, PatientAccountSummary Summary,
    decimal UnappliedCredit, PatientStatement? LastStatement, PaymentStatus? LastPaymentStatus,
    IReadOnlyList<PatientLedgerEntry> Ledger, IReadOnlyList<PatientStatement> Statements,
    IReadOnlyList<StaffBillingPayment> Payments, IReadOnlyList<PatientLedgerEntry> InsuranceActivity);
public sealed record RecordManualPayment(int PatientId, Money Amount, PatientPaymentMethod Method,
    string Reference, DateTime PaymentDate);
public sealed record PostStaffAdjustment(int PatientId, Money Amount, PatientLedgerEntryType EntryType,
    string ReasonCode, DateTime EffectiveDate);

public interface IStaffPatientBillingService
{
    Task<StaffBillingDashboard> GetDashboardAsync(ClaimsPrincipal user, DateTime date, CancellationToken cancellationToken = default);
    Task<StaffPatientBillingAccount> GetAccountAsync(ClaimsPrincipal user, int patientId, CancellationToken cancellationToken = default);
    Task<PatientPayment> RecordPaymentAsync(ClaimsPrincipal user, RecordManualPayment command, CancellationToken cancellationToken = default);
    Task<PatientLedgerEntry> PostAdjustmentAsync(ClaimsPrincipal user, PostStaffAdjustment command, CancellationToken cancellationToken = default);
    Task<PatientLedgerEntry> ReverseManualPaymentAsync(ClaimsPrincipal user, Guid paymentId, string reasonCode, CancellationToken cancellationToken = default);
    Task<PaymentAllocationResult> AllocateAsync(ClaimsPrincipal user, Guid paymentId, Guid ledgerEntryId, Money amount, CancellationToken cancellationToken = default);
    Task UnapplyAsync(ClaimsPrincipal user, Guid allocationId, string reasonCode, CancellationToken cancellationToken = default);
    Task<PatientStatement> GenerateStatementAsync(ClaimsPrincipal user, int patientId, DateTime dueDate, CancellationToken cancellationToken = default);
    Task<PatientStatement> SendStatementAsync(ClaimsPrincipal user, Guid statementId, CancellationToken cancellationToken = default);
    Task<PaymentRefundResult> RequestRefundAsync(ClaimsPrincipal user, Guid paymentId, Money amount,
        string reason, CancellationToken cancellationToken = default);
    Task<StripeReconciliationSummary> ReconcileStripeAsync(ClaimsPrincipal user, DateTime since,
        CancellationToken cancellationToken = default);
}

public sealed class StaffPatientBillingService(CloudDentalDbContext db, IPatientAccountService accounts,
    IPatientStatementService statements, IPaymentAllocationService allocations, ITenantProvider tenantProvider,
    TimeProvider clock, IPaymentRefundService? refunds = null,
    IStripePaymentReconciliationService? stripeReconciliation = null,
    IPatientBillingNotificationService? billingNotifications = null) : IStaffPatientBillingService
{
    public async Task<StaffBillingDashboard> GetDashboardAsync(ClaimsPrincipal user, DateTime date,
        CancellationToken cancellationToken = default)
    {
        var tenant = Context(user, BillingPermissions.View).Tenant;
        var start = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var payments = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == tenant && x.PaymentDate >= start && x.PaymentDate < end).ToListAsync(cancellationToken);
        var refundAmounts = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == tenant && x.EntryType == PatientLedgerEntryType.Refund &&
            x.EffectiveDate >= start && x.EffectiveDate < end).Select(x => x.Amount).ToListAsync(cancellationToken);
        var refunds = refundAmounts.Sum();
        var currencies = payments.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (currencies.Count > 1) throw new InvalidOperationException("The dashboard cannot aggregate multiple currencies.");
        decimal Sum(Func<PatientPayment, bool> predicate) => payments.Where(predicate).Sum(x => x.Amount);
        return new(Sum(x => x.Status == PaymentStatus.Succeeded),
            Sum(x => x.Processor == PaymentProcessorProvider.Stripe && x.Status == PaymentStatus.Succeeded),
            Sum(x => x.Processor != PaymentProcessorProvider.Stripe && x.Status == PaymentStatus.Succeeded),
            Sum(x => x.Processor == PaymentProcessorProvider.Stripe && x.Status == PaymentStatus.Failed),
            refunds, currencies.SingleOrDefault() ?? "USD");
    }

    public async Task<StaffPatientBillingAccount> GetAccountAsync(ClaimsPrincipal user, int patientId,
        CancellationToken cancellationToken = default)
    {
        var tenant = Context(user, BillingPermissions.View).Tenant;
        var patient = await db.Patients.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant && x.PatientId == patientId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient was not found for the tenant.");
        var summary = await accounts.GetSummaryAsync(tenant, patientId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient account was not found for the tenant.");
        var ledger = await accounts.GetLedgerAsync(tenant, patientId, cancellationToken);
        var patientStatements = await statements.ListAsync(tenant, patientId, cancellationToken);
        var accountPayments = db.PatientPayments.IgnoreQueryFilters().Where(x =>
            x.TenantId == tenant && x.PatientAccountId == summary.AccountId);
        var paymentRows = await accountPayments.AsNoTracking().Include(x => x.Allocations)
            .OrderByDescending(x => x.PaymentDate).ToListAsync(cancellationToken);
        var refundRows = await db.PatientRefunds.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == tenant && accountPayments.Select(p => p.PaymentId).Contains(x.PaymentId))
            .ToListAsync(cancellationToken);
        var paymentModels = paymentRows.Select(x =>
        {
            var active = x.Allocations.Where(a => !a.UnappliedAt.HasValue).ToList();
            var allocated = active.Sum(a => a.Amount);
            var safe = SafeReference(x.ExternalPaymentId ?? x.InternalPaymentReference);
            return new StaffBillingPayment(x.PaymentId, x.Amount, x.Currency, x.PaymentDate, x.Method, x.Processor,
                x.Status, safe, allocated, Math.Max(0, x.Amount - allocated),
                RefundLabel(refundRows.Where(r => r.PaymentId == x.PaymentId)), x.LedgerEntryId,
                x.Processor != PaymentProcessorProvider.Stripe && x.Status == PaymentStatus.Succeeded && !x.ReversedAt.HasValue,
                x.Allocations.OrderByDescending(a => a.CreatedAt).ToList());
        }).ToList();
        return new(patientId, patient.FullName, summary,
            paymentModels.Where(x => x.Status == PaymentStatus.Succeeded).Sum(x => x.Unapplied),
            patientStatements.FirstOrDefault(), paymentModels.FirstOrDefault()?.Status, ledger,
            patientStatements, paymentModels, ledger.Where(x => x.EntryType is PatientLedgerEntryType.InsurancePayment
                or PatientLedgerEntryType.ContractualAdjustment).ToList());
    }

    public async Task<PatientPayment> RecordPaymentAsync(ClaimsPrincipal user, RecordManualPayment command,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.PostPayment);
        if (command.Method is not (PatientPaymentMethod.Cash or PatientPaymentMethod.Check or PatientPaymentMethod.External))
            throw new ArgumentException("Staff payments must be cash, check, or external.");
        PaymentCheckoutService.ValidateReference(command.Reference, nameof(command.Reference));
        var reference = command.Reference;
        if (await db.PatientPayments.IgnoreQueryFilters().AnyAsync(x => x.TenantId == context.Tenant &&
            x.InternalPaymentReference == reference, cancellationToken))
            throw new InvalidOperationException("The payment reference already exists.");
        var summary = await accounts.GetSummaryAsync(context.Tenant, command.PatientId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient account was not found for the tenant.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var id = Guid.NewGuid();
        var now = clock.GetUtcNow().UtcDateTime;
        var entry = await accounts.PostAsync(new(context.Tenant, command.PatientId, PatientLedgerEntryType.PatientPayment,
            command.Amount, command.PaymentDate, PatientLedgerSourceType.PatientPayment, id.ToString("N"),
            "manual-patient-payment", context.Actor), cancellationToken);
        var payment = new PatientPayment
        {
            PaymentId = id, TenantId = context.Tenant, PatientAccountId = summary.AccountId,
            Amount = command.Amount.Amount, Currency = command.Amount.Currency, PaymentDate = command.PaymentDate,
            Method = command.Method, Processor = command.Method == PatientPaymentMethod.External
                ? PaymentProcessorProvider.External : PaymentProcessorProvider.Office,
            ExternalPaymentId = command.Method == PatientPaymentMethod.External ? reference : null,
            InternalPaymentReference = reference, Status = PaymentStatus.Succeeded,
            LedgerEntryId = entry.LedgerEntryId, CreatedAt = now, UpdatedAt = now, CreatedBy = context.Actor
        };
        db.PatientPayments.Add(payment);
        Audit(context, "PaymentRecorded", "PatientPayment", id.ToString("N"));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException("The payment reference was concurrently recorded; use a unique reference.", ex);
        }
        return payment;
    }

    public async Task<PatientLedgerEntry> PostAdjustmentAsync(ClaimsPrincipal user, PostStaffAdjustment command,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.Adjust);
        if (command.EntryType is not (PatientLedgerEntryType.ContractualAdjustment or PatientLedgerEntryType.WriteOff or
            PatientLedgerEntryType.Credit or PatientLedgerEntryType.DebitAdjustment))
            throw new ArgumentException("Unsupported staff adjustment type.");
        ValidateReason(command.ReasonCode);
        var source = Guid.NewGuid().ToString("N");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var entry = await accounts.PostAsync(new(context.Tenant, command.PatientId, command.EntryType, command.Amount,
            command.EffectiveDate, PatientLedgerSourceType.StaffAdjustment, source, command.ReasonCode.Trim(), context.Actor),
            cancellationToken);
        Audit(context, "AdjustmentPosted", "PatientLedgerEntry", entry.LedgerEntryId.ToString("N"), command.ReasonCode);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return entry;
    }

    public async Task<PatientLedgerEntry> ReverseManualPaymentAsync(ClaimsPrincipal user, Guid paymentId,
        string reasonCode, CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.Adjust);
        ValidateReason(reasonCode);
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == context.Tenant && x.PaymentId == paymentId, cancellationToken)
            ?? throw new KeyNotFoundException("Payment was not found for the tenant.");
        if (payment.Processor == PaymentProcessorProvider.Stripe)
            throw new InvalidOperationException("Online payments must use the processor refund workflow.");
        if (payment.Status != PaymentStatus.Succeeded || !payment.LedgerEntryId.HasValue || payment.ReversedAt.HasValue)
            throw new InvalidOperationException("Only an unreversed, successful manual payment can be reversed.");
        if (await db.PatientPaymentAllocations.IgnoreQueryFilters().AnyAsync(x => x.TenantId == context.Tenant &&
            x.PaymentId == paymentId && !x.UnappliedAt.HasValue, cancellationToken))
            throw new InvalidOperationException("Unapply active allocations before reversing this payment.");
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var reversal = await accounts.ReverseAsync(context.Tenant, payment.LedgerEntryId.Value,
            $"manual-reversal-{Guid.NewGuid():N}", context.Actor, now, cancellationToken);
        payment.Status = PaymentStatus.Cancelled;
        payment.ReversalLedgerEntryId = reversal.LedgerEntryId;
        payment.ReversedAt = now;
        payment.ReversedBy = context.Actor;
        payment.UpdatedAt = now;
        Audit(context, "ManualPaymentReversed", "PatientPayment", paymentId.ToString("N"), reasonCode);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return reversal;
    }

    public Task<PaymentAllocationResult> AllocateAsync(ClaimsPrincipal user, Guid paymentId, Guid ledgerEntryId,
        Money amount, CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.PostPayment);
        return AllocateAndAudit(context, paymentId, ledgerEntryId, amount, cancellationToken);
    }

    private async Task<PaymentAllocationResult> AllocateAndAudit(BillingContext context, Guid paymentId,
        Guid ledgerEntryId, Money amount, CancellationToken cancellationToken)
    {
        var result = await allocations.AllocateAsync(context.Tenant, paymentId, ledgerEntryId, amount, context.Actor, cancellationToken);
        return result;
    }

    public async Task UnapplyAsync(ClaimsPrincipal user, Guid allocationId, string reasonCode,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.PostPayment);
        ValidateReason(reasonCode);
        var allocation = await db.PatientPaymentAllocations.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == context.Tenant && x.PaymentAllocationId == allocationId, cancellationToken)
            ?? throw new KeyNotFoundException("Allocation was not found for the tenant.");
        if (allocation.UnappliedAt.HasValue) throw new InvalidOperationException("Allocation is already unapplied.");
        allocation.UnappliedAt = clock.GetUtcNow().UtcDateTime;
        allocation.UnappliedBy = context.Actor;
        allocation.UnapplyReasonCode = reasonCode.Trim();
        Audit(context, "PaymentUnapplied", "PatientPaymentAllocation", allocationId.ToString("N"), reasonCode);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PatientStatement> GenerateStatementAsync(ClaimsPrincipal user, int patientId, DateTime dueDate,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.Adjust);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var statement = await statements.CreateAsync(context.Tenant, patientId, now, dueDate, now, true,
            context.Actor, cancellationToken);
        Audit(context, "StatementGenerated", "PatientStatement", statement.StatementId.ToString("N"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return statement;
    }

    public async Task<PatientStatement> SendStatementAsync(ClaimsPrincipal user, Guid statementId,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.Adjust);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var statement = await statements.TransitionAsync(context.Tenant, statementId, PatientStatementStatus.Sent, cancellationToken);
        Audit(context, "StatementSent", "PatientStatement", statementId.ToString("N"));
        await db.SaveChangesAsync(cancellationToken);
        if (billingNotifications is not null)
            await billingNotifications.EnqueueAsync(context.Tenant, statement.PatientAccountId,
                PatientBillingNotificationType.NewStatement, "statement", statement.StatementId.ToString("N"),
                cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return statement;
    }

    public Task<PaymentRefundResult> RequestRefundAsync(ClaimsPrincipal user, Guid paymentId, Money amount,
        string reason, CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.Refund);
        ValidateReason(reason);
        return (refunds ?? throw new InvalidOperationException("Refund processing is unavailable.")).RefundAsync(
            new PaymentRefundRequest(context.Tenant, paymentId, amount,
            $"refund_{Guid.NewGuid():N}", reason.Trim(), context.Actor), cancellationToken);
    }

    public Task<StripeReconciliationSummary> ReconcileStripeAsync(ClaimsPrincipal user, DateTime since,
        CancellationToken cancellationToken = default)
    {
        var context = Context(user, BillingPermissions.ConfigurePayments);
        return (stripeReconciliation ?? throw new InvalidOperationException("Stripe reconciliation is unavailable."))
            .ReconcileAsync(context.Tenant, since, cancellationToken);
    }

    private BillingContext Context(ClaimsPrincipal user, string permission)
    {
        if (!BillingPermissions.Has(user, permission)) throw new UnauthorizedAccessException("Billing permission is required.");
        var tenant = PatientAccountApi.TrustedTenantId(user);
        var trusted = tenantProvider.User is { } providerUser
            ? PatientAccountApi.TrustedTenantId(providerUser) ?? tenantProvider.TenantId : tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenant) || !string.Equals(tenant, trusted, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Billing tenant context does not match the authenticated tenant.");
        var actor = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 100)
            throw new UnauthorizedAccessException("A bounded authenticated billing actor is required.");
        return new(tenant, actor.Trim());
    }

    private void Audit(BillingContext context, string action, string entityType, string entityId, string? reason = null) =>
        db.FinancialAuditEvents.Add(new FinancialAuditEvent { Id = Guid.NewGuid(), TenantId = context.Tenant,
            Action = action, EntityType = entityType, EntityId = entityId, Actor = context.Actor,
            ReasonCode = reason?.Trim(), CreatedAt = clock.GetUtcNow().UtcDateTime });
    private static string SafeReference(string value) => value.Length <= 8 ? value : value[^8..];
    private static string RefundLabel(IEnumerable<PatientRefund> values)
    {
        var rows = values.ToList();
        if (rows.Count == 0) return "None";
        return string.Join(", ", rows.OrderByDescending(x => x.RequestedAt).Select(x =>
            $"{x.Amount:N2} {x.Currency} {x.Status}"));
    }
    private static void ValidateReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64)
            throw new ArgumentException("A bounded reason code is required.");
    }
    private sealed record BillingContext(string Tenant, string Actor);
}

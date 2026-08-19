using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public enum PatientResponsibilityKind { Estimated, Finalized }

public sealed record PatientResponsibility(
    PatientResponsibilityKind Kind,
    decimal Charges,
    decimal InsurancePayments,
    decimal Adjustments,
    decimal PatientPayments,
    decimal PatientDue,
    string Currency,
    DateTime CalculatedAt);

public interface IPatientResponsibilityService
{
    PatientResponsibility CalculateEstimate(Money charges, Money estimatedInsurancePayment,
        Money estimatedAdjustment, DateTime calculatedAt);
    Task<PatientResponsibility?> GetFinalizedAsync(string tenantId, int patientId,
        CancellationToken cancellationToken = default);
}

public sealed class PatientResponsibilityService(IPatientAccountService accounts) : IPatientResponsibilityService
{
    public PatientResponsibility CalculateEstimate(Money charges, Money estimatedInsurancePayment,
        Money estimatedAdjustment, DateTime calculatedAt)
    {
        EnsureSameCurrency(charges, estimatedInsurancePayment, estimatedAdjustment);
        if (charges.Amount < 0 || estimatedInsurancePayment.Amount < 0 || estimatedAdjustment.Amount < 0)
            throw new ArgumentOutOfRangeException(nameof(charges), "Estimated responsibility inputs cannot be negative.");
        return new(PatientResponsibilityKind.Estimated, charges.Amount, estimatedInsurancePayment.Amount,
            estimatedAdjustment.Amount, 0m,
            charges.Amount - estimatedInsurancePayment.Amount - estimatedAdjustment.Amount,
            charges.Currency, NormalizeUtc(calculatedAt));
    }

    public async Task<PatientResponsibility?> GetFinalizedAsync(string tenantId, int patientId,
        CancellationToken cancellationToken = default)
    {
        var summary = await accounts.GetSummaryAsync(tenantId, patientId, cancellationToken);
        if (summary is null) return null;
        var balance = summary.Balance;
        return new(PatientResponsibilityKind.Finalized, balance.TotalCharges, balance.InsurancePayments,
            balance.Adjustments + balance.Credits, balance.PatientPayments,
            balance.AmountDue, balance.Currency, summary.UpdatedAt);
    }

    private static void EnsureSameCurrency(params Money[] values)
    {
        if (values.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
            throw new InvalidOperationException("Responsibility cannot mix currencies.");
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

public sealed record PatientStatementPreview(
    Guid PatientAccountId,
    int PatientId,
    DateTime StatementDate,
    DateTime DueDate,
    DateTime LedgerThroughDate,
    decimal BalanceForward,
    decimal NewCharges,
    decimal InsurancePayments,
    decimal Adjustments,
    decimal PatientPayments,
    decimal Credits,
    decimal Refunds,
    decimal DebitAdjustments,
    decimal AmountDue,
    string Currency,
    IReadOnlyList<PatientStatementLinePreview> Lines);

public sealed record PatientStatementLinePreview(Guid LedgerEntryId, DateTime ActivityDate,
    PatientLedgerEntryType EntryType, string PatientDescription, decimal Amount, string Currency);

public interface IPatientStatementService
{
    Task<PatientStatementPreview> PreviewAsync(string tenantId, int patientId, DateTime statementDate,
        DateTime dueDate, DateTime ledgerThroughDate, CancellationToken cancellationToken = default);
    Task<PatientStatement> CreateAsync(string tenantId, int patientId, DateTime statementDate, DateTime dueDate,
        DateTime ledgerThroughDate, bool finalize, string createdBy, CancellationToken cancellationToken = default);
    Task<PatientStatement> FinalizeAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default);
    Task<PatientStatement> TransitionAsync(string tenantId, Guid statementId, PatientStatementStatus status,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PatientStatement>> ListAsync(string tenantId, int? patientId = null,
        CancellationToken cancellationToken = default);
    Task<PatientStatement?> GetAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default);
    Task<PatientStatement> VoidAsync(string tenantId, Guid statementId, string reasonCode,
        CancellationToken cancellationToken = default);
    Task<PatientStatement> SupersedeAsync(string tenantId, Guid statementId, Guid replacementStatementId,
        CancellationToken cancellationToken = default);
}

public sealed class PatientStatementService(CloudDentalDbContext db, ITenantProvider tenantProvider,
    TimeProvider clock, ILogger<PatientStatementService> logger) : IPatientStatementService
{
    private static readonly PatientStatementStatus[] BalanceForwardStatuses =
        [PatientStatementStatus.Ready, PatientStatementStatus.Sent, PatientStatementStatus.PartiallyPaid, PatientStatementStatus.Paid];

    public async Task<PatientStatementPreview> PreviewAsync(string tenantId, int patientId, DateTime statementDate,
        DateTime dueDate, DateTime ledgerThroughDate, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        statementDate = NormalizeUtc(statementDate);
        dueDate = NormalizeUtc(dueDate);
        ledgerThroughDate = NormalizeUtc(ledgerThroughDate);
        if (dueDate.Date < statementDate.Date) throw new ArgumentException("Due date cannot precede the statement date.");
        if (ledgerThroughDate > clock.GetUtcNow().UtcDateTime.AddMinutes(1)) throw new ArgumentException("Ledger cutoff cannot be in the future.");

        var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.PatientId == patientId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient account was not found for the tenant.");
        var prior = await db.PatientStatements.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.PatientAccountId == account.Id &&
                BalanceForwardStatuses.Contains(x.Status) && x.LedgerThroughDate <= ledgerThroughDate)
            .OrderByDescending(x => x.LedgerThroughDate).ThenByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var fromExclusive = prior?.LedgerThroughDate;
        var entries = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.PatientAccountId == account.Id &&
                x.CreatedAt <= ledgerThroughDate && (!fromExclusive.HasValue || x.CreatedAt > fromExclusive.Value))
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.LedgerEntryId)
            .ToListAsync(cancellationToken);
        var currencies = entries.Select(x => x.Currency).Append(prior?.Currency).Where(x => x is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (currencies.Count > 1) throw new InvalidOperationException("A statement cannot mix currencies.");

        decimal Sum(params PatientLedgerEntryType[] types) => entries.Where(x => types.Contains(x.EntryType)).Sum(x => x.Amount);
        var charges = Sum(PatientLedgerEntryType.Charge);
        var insurance = Sum(PatientLedgerEntryType.InsurancePayment);
        var payments = Sum(PatientLedgerEntryType.PatientPayment);
        var adjustments = Sum(PatientLedgerEntryType.ContractualAdjustment, PatientLedgerEntryType.WriteOff);
        var credits = Sum(PatientLedgerEntryType.Credit);
        var refunds = Sum(PatientLedgerEntryType.Refund);
        var debits = Sum(PatientLedgerEntryType.DebitAdjustment, PatientLedgerEntryType.Transfer);
        var balanceForward = prior?.AmountDue ?? 0m;
        var amountDue = balanceForward + charges + refunds + debits - insurance - adjustments - payments - credits;
        var lines = entries.Select(ToPreviewLine).ToList();
        return new(account.Id, patientId, statementDate, dueDate, ledgerThroughDate, balanceForward, charges,
            insurance, adjustments, payments, credits, refunds, debits, amountDue,
            currencies.SingleOrDefault() ?? "USD", lines);
    }

    public async Task<PatientStatement> CreateAsync(string tenantId, int patientId, DateTime statementDate,
        DateTime dueDate, DateTime ledgerThroughDate, bool finalize, string createdBy,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        if (string.IsNullOrWhiteSpace(createdBy) || createdBy.Trim().Length > 100)
            throw new ArgumentException("A bounded statement creator is required.");
        var preview = await PreviewAsync(tenantId, patientId, statementDate, dueDate, ledgerThroughDate, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var statement = new PatientStatement
        {
            StatementId = Guid.NewGuid(), TenantId = tenantId, PatientAccountId = preview.PatientAccountId,
            StatementDate = preview.StatementDate, DueDate = preview.DueDate,
            Status = finalize ? PatientStatementStatus.Ready : PatientStatementStatus.Draft,
            BalanceForward = preview.BalanceForward, NewCharges = preview.NewCharges,
            InsurancePayments = preview.InsurancePayments, Adjustments = preview.Adjustments,
            PatientPayments = preview.PatientPayments, Credits = preview.Credits, Refunds = preview.Refunds,
            DebitAdjustments = preview.DebitAdjustments, AmountDue = preview.AmountDue, Currency = preview.Currency,
            LedgerThroughDate = preview.LedgerThroughDate, CreatedAt = now, CreatedBy = createdBy.Trim(), StatusUpdatedAt = now,
            Lines = preview.Lines.Select(x => new PatientStatementLine
            {
                StatementLineId = Guid.NewGuid(), TenantId = tenantId, LedgerEntryId = x.LedgerEntryId,
                ActivityDate = x.ActivityDate, EntryType = x.EntryType, PatientDescription = x.PatientDescription,
                Amount = x.Amount, Currency = x.Currency
            }).ToList()
        };
        db.PatientStatements.Add(statement);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Patient statement {StatementId} created with status {Status}.",
            statement.StatementId, statement.Status);
        return statement;
    }

    public async Task<PatientStatement> FinalizeAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default)
    {
        var statement = await Required(tenantId, statementId, false, cancellationToken);
        if (statement.Status != PatientStatementStatus.Draft) throw new InvalidOperationException("Only draft statements can be finalized.");
        statement.Status = PatientStatementStatus.Ready;
        statement.StatusUpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    public async Task<PatientStatement> TransitionAsync(string tenantId, Guid statementId, PatientStatementStatus status,
        CancellationToken cancellationToken = default)
    {
        var statement = await Required(tenantId, statementId, false, cancellationToken);
        var allowed = (statement.Status, status) switch
        {
            (PatientStatementStatus.Ready, PatientStatementStatus.Sent) => true,
            (PatientStatementStatus.Sent, PatientStatementStatus.PartiallyPaid or PatientStatementStatus.Paid) => true,
            (PatientStatementStatus.PartiallyPaid, PatientStatementStatus.Paid) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("The requested statement status transition is not allowed.");
        if (status is PatientStatementStatus.PartiallyPaid or PatientStatementStatus.Paid)
        {
            var laterEntries = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.PatientAccountId == statement.PatientAccountId &&
                x.CreatedAt > statement.LedgerThroughDate).ToListAsync(cancellationToken);
            var remaining = statement.AmountDue + laterEntries.Sum(Impact);
            if (status == PatientStatementStatus.PartiallyPaid &&
                (statement.AmountDue <= 0 || remaining <= 0 || remaining >= statement.AmountDue))
                throw new InvalidOperationException("A partial payment status requires posted ledger activity that reduces the statement balance.");
            if (status == PatientStatementStatus.Paid && remaining > 0)
                throw new InvalidOperationException("A paid status requires posted ledger activity that satisfies the statement balance.");
        }
        statement.Status = status;
        statement.StatusUpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    public async Task<IReadOnlyList<PatientStatement>> ListAsync(string tenantId, int? patientId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        var query = db.PatientStatements.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId);
        if (patientId.HasValue)
        {
            var accountId = await db.PatientAccounts.IgnoreQueryFilters().Where(x =>
                x.TenantId == tenantId && x.PatientId == patientId.Value).Select(x => (Guid?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (!accountId.HasValue) return [];
            query = query.Where(x => x.PatientAccountId == accountId.Value);
        }
        return await query.OrderByDescending(x => x.StatementDate).ThenByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public Task<PatientStatement?> GetAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default) =>
        Optional(tenantId, statementId, true, cancellationToken);

    public async Task<PatientStatement> VoidAsync(string tenantId, Guid statementId, string reasonCode,
        CancellationToken cancellationToken = default)
    {
        var statement = await Required(tenantId, statementId, false, cancellationToken);
        if (statement.Status is PatientStatementStatus.Paid or PatientStatementStatus.Superseded or PatientStatementStatus.Voided)
            throw new InvalidOperationException("The statement cannot be voided from its current status.");
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Trim().Length > 64)
            throw new ArgumentException("A bounded void reason code is required.");
        var now = clock.GetUtcNow().UtcDateTime;
        statement.Status = PatientStatementStatus.Voided;
        statement.StatusUpdatedAt = now;
        statement.VoidedAt = now;
        statement.VoidReasonCode = reasonCode.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return statement;
    }

    public async Task<PatientStatement> SupersedeAsync(string tenantId, Guid statementId, Guid replacementStatementId,
        CancellationToken cancellationToken = default)
    {
        if (statementId == replacementStatementId) throw new ArgumentException("A statement cannot supersede itself.");
        var original = await Required(tenantId, statementId, false, cancellationToken);
        var replacement = await Required(tenantId, replacementStatementId, false, cancellationToken);
        if (original.PatientAccountId != replacement.PatientAccountId || replacement.CreatedAt <= original.CreatedAt)
            throw new InvalidOperationException("Replacement must be a newer statement for the same account.");
        if (original.Status is PatientStatementStatus.Paid or PatientStatementStatus.Superseded or PatientStatementStatus.Voided ||
            replacement.Status is PatientStatementStatus.Voided or PatientStatementStatus.Superseded)
            throw new InvalidOperationException("The statement cannot be superseded from its current status.");
        var now = clock.GetUtcNow().UtcDateTime;
        original.Status = PatientStatementStatus.Superseded;
        original.StatusUpdatedAt = now;
        original.SupersededByStatementId = replacement.StatementId;
        replacement.SupersedesStatementId = original.StatementId;
        replacement.StatusUpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return original;
    }

    private async Task<PatientStatement> Required(string tenantId, Guid id, bool lines, CancellationToken cancellationToken) =>
        await Optional(tenantId, id, lines, cancellationToken) ?? throw new KeyNotFoundException("Statement was not found for the tenant.");

    private async Task<PatientStatement?> Optional(string tenantId, Guid id, bool lines, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var query = db.PatientStatements.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.StatementId == id);
        if (lines) query = query.Include(x => x.Lines);
        return await query.SingleOrDefaultAsync(cancellationToken);
    }

    private static PatientStatementLinePreview ToPreviewLine(PatientLedgerEntry entry) => new(
        entry.LedgerEntryId, entry.EffectiveDate, entry.EntryType,
        entry.ReversalOfEntryId.HasValue ? "Reversal of prior account activity" : entry.EntryType switch
        {
            PatientLedgerEntryType.Charge => "Dental services",
            PatientLedgerEntryType.InsurancePayment => "Insurance payment",
            PatientLedgerEntryType.PatientPayment => "Payment received",
            PatientLedgerEntryType.ContractualAdjustment => "Insurance adjustment",
            PatientLedgerEntryType.WriteOff => "Account adjustment",
            PatientLedgerEntryType.Refund => "Refund",
            PatientLedgerEntryType.Credit => "Account credit",
            PatientLedgerEntryType.DebitAdjustment => "Account adjustment",
            PatientLedgerEntryType.Transfer => "Balance transfer",
            _ => "Account activity"
        }, Impact(entry), entry.Currency);

    private static decimal Impact(PatientLedgerEntry entry) => entry.EntryType switch
    {
        PatientLedgerEntryType.Charge or PatientLedgerEntryType.Refund or
        PatientLedgerEntryType.DebitAdjustment or PatientLedgerEntryType.Transfer => entry.Amount,
        _ => -entry.Amount
    };

    private void EnsureTenant(string tenantId)
    {
        var trustedTenant = tenantProvider.User is { } user
            ? PatientAccountApi.TrustedTenantId(user) ?? tenantProvider.TenantId
            : tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId) || !string.Equals(trustedTenant, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Statement tenant context does not match the authenticated tenant.");
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}

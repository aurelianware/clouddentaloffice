using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public sealed record PostPatientLedgerEntry(
    string TenantId,
    int PatientId,
    PatientLedgerEntryType EntryType,
    Money Amount,
    DateTime EffectiveDate,
    PatientLedgerSourceType SourceType,
    string SourceId,
    string DescriptionCode,
    string CreatedBy);

public sealed record PatientAccountBalance(
    decimal TotalCharges,
    decimal InsurancePayments,
    decimal Adjustments,
    decimal PatientPayments,
    decimal Refunds,
    decimal Credits,
    decimal AmountDue,
    string Currency);

public sealed record PatientAccountSummary(Guid AccountId, int PatientId, PatientAccountStatus Status,
    PatientAccountBalance Balance, DateTime CreatedAt, DateTime UpdatedAt);

public interface IPatientAccountService
{
    Task<PatientLedgerEntry> PostAsync(PostPatientLedgerEntry command, CancellationToken cancellationToken = default);
    Task<PatientLedgerEntry> ReverseAsync(string tenantId, Guid entryId, string sourceId, string createdBy,
        DateTime effectiveDate, CancellationToken cancellationToken = default);
    Task<PatientAccountSummary?> GetSummaryAsync(string tenantId, int patientId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PatientLedgerEntry>> GetLedgerAsync(string tenantId, int patientId, CancellationToken cancellationToken = default);
}

public sealed class DuplicateLedgerSourceException(string message) : InvalidOperationException(message);

public sealed class PatientAccountService(CloudDentalDbContext db, TimeProvider clock,
    ITenantProvider tenantProvider, ILogger<PatientAccountService> logger) : IPatientAccountService
{
    public async Task<PatientLedgerEntry> PostAsync(PostPatientLedgerEntry command, CancellationToken cancellationToken = default)
    {
        EnsureTenant(command.TenantId);
        Validate(command);
        if (!await db.Patients.IgnoreQueryFilters().AnyAsync(x => x.TenantId == command.TenantId && x.PatientId == command.PatientId, cancellationToken))
            throw new KeyNotFoundException("Patient was not found for the tenant.");

        var duplicate = await db.PatientLedgerEntries.IgnoreQueryFilters().AnyAsync(x =>
            x.TenantId == command.TenantId && x.SourceType == command.SourceType && x.SourceId == command.SourceId &&
            x.EntryType == command.EntryType, cancellationToken);
        if (duplicate) throw new DuplicateLedgerSourceException("The financial source was already posted.");

        var now = clock.GetUtcNow().UtcDateTime;
        var account = await db.PatientAccounts.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == command.TenantId && x.PatientId == command.PatientId, cancellationToken);
        if (account is null)
        {
            account = new PatientAccount { Id = Guid.NewGuid(), TenantId = command.TenantId, PatientId = command.PatientId,
                Status = PatientAccountStatus.Active, CreatedAt = now, UpdatedAt = now };
            db.PatientAccounts.Add(account);
        }
        else
        {
            var accountCurrency = await db.PatientLedgerEntries.IgnoreQueryFilters().Where(x =>
                x.TenantId == command.TenantId && x.PatientAccountId == account.Id).Select(x => x.Currency)
                .FirstOrDefaultAsync(cancellationToken);
            if (accountCurrency is not null && !accountCurrency.Equals(command.Amount.Currency, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A patient account cannot contain multiple currencies.");
        }

        var entry = new PatientLedgerEntry
        {
            LedgerEntryId = Guid.NewGuid(), TenantId = command.TenantId, PatientAccountId = account.Id,
            EntryType = command.EntryType, Amount = command.Amount.Amount, Currency = command.Amount.Currency,
            EffectiveDate = NormalizeUtc(command.EffectiveDate), SourceType = command.SourceType,
            SourceId = command.SourceId.Trim(), DescriptionCode = command.DescriptionCode.Trim(),
            CreatedAt = now, CreatedBy = command.CreatedBy.Trim()
        };
        account.UpdatedAt = now;
        db.PatientLedgerEntries.Add(entry);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DuplicateLedgerSourceException("The account or financial source was concurrently posted.");
        }
        logger.LogInformation("Patient ledger entry {LedgerEntryId} posted for tenant {TenantId}, source {SourceType}, type {EntryType}.",
            entry.LedgerEntryId, command.TenantId, command.SourceType, command.EntryType);
        return entry;
    }

    public async Task<PatientLedgerEntry> ReverseAsync(string tenantId, Guid entryId, string sourceId, string createdBy,
        DateTime effectiveDate, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        var original = await db.PatientLedgerEntries.IgnoreQueryFilters().Include(x => x.PatientAccount).SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.LedgerEntryId == entryId, cancellationToken)
            ?? throw new KeyNotFoundException("Ledger entry was not found for the tenant.");
        if (original.ReversalOfEntryId.HasValue) throw new InvalidOperationException("A reversal entry cannot be reversed through this operation.");
        if (await db.PatientLedgerEntries.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.ReversalOfEntryId == entryId, cancellationToken))
            throw new DuplicateLedgerSourceException("The ledger entry was already reversed.");
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Trim().Length > 128 || string.IsNullOrWhiteSpace(createdBy) || createdBy.Trim().Length > 100)
            throw new ArgumentException("A bounded source ID and actor are required.");

        var now = clock.GetUtcNow().UtcDateTime;
        var reversal = new PatientLedgerEntry
        {
            LedgerEntryId = Guid.NewGuid(), TenantId = tenantId, PatientAccountId = original.PatientAccountId,
            EntryType = original.EntryType, Amount = -original.Amount, Currency = original.Currency,
            EffectiveDate = NormalizeUtc(effectiveDate), SourceType = PatientLedgerSourceType.SystemReversal,
            SourceId = sourceId.Trim(), DescriptionCode = "reversal", CreatedAt = now,
            CreatedBy = createdBy.Trim(), ReversalOfEntryId = original.LedgerEntryId
        };
        original.PatientAccount.UpdatedAt = now;
        db.PatientLedgerEntries.Add(reversal);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Patient ledger entry {LedgerEntryId} reversed for tenant {TenantId}.", entryId, tenantId);
        return reversal;
    }

    public async Task<PatientAccountSummary?> GetSummaryAsync(string tenantId, int patientId, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.PatientId == patientId, cancellationToken);
        if (account is null) return null;
        var entries = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == tenantId && x.PatientAccountId == account.Id).ToListAsync(cancellationToken);
        return new(account.Id, account.PatientId, account.Status, Calculate(entries), account.CreatedAt, account.UpdatedAt);
    }

    public async Task<IReadOnlyList<PatientLedgerEntry>> GetLedgerAsync(string tenantId, int patientId, CancellationToken cancellationToken = default)
    {
        EnsureTenant(tenantId);
        var accountId = await db.PatientAccounts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.PatientId == patientId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (!accountId.HasValue) return [];
        return await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == tenantId && x.PatientAccountId == accountId.Value)
            .OrderBy(x => x.EffectiveDate).ThenBy(x => x.CreatedAt).ThenBy(x => x.LedgerEntryId).ToListAsync(cancellationToken);
    }

    public static PatientAccountBalance Calculate(IEnumerable<PatientLedgerEntry> entries)
    {
        var rows = entries.ToList();
        var currencies = rows.Select(x => x.Currency).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (currencies.Count > 1) throw new InvalidOperationException("A patient account cannot aggregate multiple currencies.");
        decimal Sum(params PatientLedgerEntryType[] types) => rows.Where(x => types.Contains(x.EntryType)).Sum(x => x.Amount);
        var charges = Sum(PatientLedgerEntryType.Charge);
        var insurance = Sum(PatientLedgerEntryType.InsurancePayment);
        var patient = Sum(PatientLedgerEntryType.PatientPayment);
        var adjustments = Sum(PatientLedgerEntryType.ContractualAdjustment, PatientLedgerEntryType.WriteOff);
        var credits = Sum(PatientLedgerEntryType.Credit);
        var refunds = Sum(PatientLedgerEntryType.Refund);
        var debitAdjustments = Sum(PatientLedgerEntryType.DebitAdjustment);
        var transfers = Sum(PatientLedgerEntryType.Transfer);
        var due = charges + refunds + debitAdjustments + transfers - insurance - patient - adjustments - credits;
        return new(charges, insurance, adjustments, patient, refunds, credits, due, currencies.SingleOrDefault() ?? "USD");
    }

    private static void Validate(PostPatientLedgerEntry command)
    {
        if (string.IsNullOrWhiteSpace(command.TenantId) || command.TenantId.Length > 64) throw new ArgumentException("Tenant is required.");
        if (command.PatientId <= 0) throw new ArgumentOutOfRangeException(nameof(command.PatientId));
        if (command.Amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(command.Amount), "Posted amounts must be positive; use a reversal for corrections.");
        if (string.IsNullOrWhiteSpace(command.SourceId) || command.SourceId.Trim().Length > 128) throw new ArgumentException("Source ID is required.");
        if (string.IsNullOrWhiteSpace(command.DescriptionCode) || command.DescriptionCode.Trim().Length > 64) throw new ArgumentException("Description code is required.");
        if (string.IsNullOrWhiteSpace(command.CreatedBy) || command.CreatedBy.Trim().Length > 100) throw new ArgumentException("Posting actor is required.");
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true ||
        exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;

    private void EnsureTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || !string.Equals(tenantProvider.TenantId, tenantId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Patient account tenant context does not match the authenticated tenant.");
    }
}

using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientAccountServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly PatientAccountService _service;
    private readonly DateTime _effective = new(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);

    public PatientAccountServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        var tenant = new FixedTenantProvider("tenant-a");
        _db = new CloudDentalDbContext(options, tenant);
        _db.Database.EnsureCreated();
        _db.Patients.Add(new Patient { TenantId = "tenant-a", PatientId = 101, FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.SaveChanges();
        _service = new PatientAccountService(_db, TimeProvider.System, tenant, NullLogger<PatientAccountService>.Instance);
    }

    [Fact]
    public async Task First_transaction_lazily_creates_one_account_and_posts_charge()
    {
        var entry = await Post(PatientLedgerEntryType.Charge, 250m, "procedure-1", PatientLedgerSourceType.Procedure);
        var account = Assert.Single(_db.PatientAccounts);
        Assert.Equal(101, account.PatientId);
        Assert.Equal(account.Id, entry.PatientAccountId);
        Assert.Equal(250m, (await _service.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
    }

    [Fact]
    public async Task Payments_adjustments_and_refunds_derive_deterministic_balance()
    {
        await Post(PatientLedgerEntryType.Charge, 500m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Post(PatientLedgerEntryType.InsurancePayment, 180m, "era-1", PatientLedgerSourceType.Era);
        await Post(PatientLedgerEntryType.ContractualAdjustment, 70m, "era-1", PatientLedgerSourceType.Era);
        await Post(PatientLedgerEntryType.PatientPayment, 200m, "payment-1", PatientLedgerSourceType.PatientPayment);
        await Post(PatientLedgerEntryType.Refund, 25m, "refund-1", PatientLedgerSourceType.Refund);
        var balance = (await _service.GetSummaryAsync("tenant-a", 101))!.Balance;
        Assert.Equal(500m, balance.TotalCharges);
        Assert.Equal(180m, balance.InsurancePayments);
        Assert.Equal(70m, balance.Adjustments);
        Assert.Equal(200m, balance.PatientPayments);
        Assert.Equal(25m, balance.Refunds);
        Assert.Equal(75m, balance.AmountDue);
    }

    [Fact]
    public async Task Overpayment_produces_credit_balance()
    {
        await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Post(PatientLedgerEntryType.PatientPayment, 130m, "payment-1", PatientLedgerSourceType.PatientPayment);
        Assert.Equal(-30m, (await _service.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
    }

    [Theory]
    [InlineData(PatientLedgerEntryType.Charge, 40)]
    [InlineData(PatientLedgerEntryType.Refund, 40)]
    [InlineData(PatientLedgerEntryType.DebitAdjustment, 40)]
    [InlineData(PatientLedgerEntryType.Transfer, 40)]
    [InlineData(PatientLedgerEntryType.InsurancePayment, -40)]
    [InlineData(PatientLedgerEntryType.PatientPayment, -40)]
    [InlineData(PatientLedgerEntryType.ContractualAdjustment, -40)]
    [InlineData(PatientLedgerEntryType.WriteOff, -40)]
    [InlineData(PatientLedgerEntryType.Credit, -40)]
    public void Every_ledger_type_has_an_explicit_balance_direction(PatientLedgerEntryType type, int expected)
    {
        var entry = new PatientLedgerEntry { EntryType = type, Amount = 40m, Currency = "USD" };
        Assert.Equal(expected, PatientAccountService.Calculate([entry]).AmountDue);
    }

    [Fact]
    public async Task Reversal_negates_original_without_mutating_it()
    {
        var charge = await Post(PatientLedgerEntryType.Charge, 125m, "procedure-1", PatientLedgerSourceType.Procedure);
        var reversal = await _service.ReverseAsync("tenant-a", charge.LedgerEntryId, "correction-1", "staff:42", _effective);
        Assert.Equal(-125m, reversal.Amount);
        Assert.Equal(charge.LedgerEntryId, reversal.ReversalOfEntryId);
        Assert.Equal(0m, (await _service.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
        Assert.Equal(2, (await _service.GetLedgerAsync("tenant-a", 101)).Count);
    }

    [Fact]
    public async Task Duplicate_source_and_duplicate_reversal_are_rejected()
    {
        var charge = await Post(PatientLedgerEntryType.Charge, 125m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Assert.ThrowsAsync<DuplicateLedgerSourceException>(() => Post(PatientLedgerEntryType.Charge, 125m, "procedure-1", PatientLedgerSourceType.Procedure));
        await _service.ReverseAsync("tenant-a", charge.LedgerEntryId, "correction-1", "staff:42", _effective);
        await Assert.ThrowsAsync<DuplicateLedgerSourceException>(() =>
            _service.ReverseAsync("tenant-a", charge.LedgerEntryId, "correction-2", "staff:42", _effective));
    }

    [Fact]
    public async Task Different_entry_types_from_one_era_source_are_allowed()
    {
        await Post(PatientLedgerEntryType.InsurancePayment, 100m, "era-1", PatientLedgerSourceType.Era);
        await Post(PatientLedgerEntryType.ContractualAdjustment, 25m, "era-1", PatientLedgerSourceType.Era);
        Assert.Equal(2, _db.PatientLedgerEntries.Count());
    }

    [Fact]
    public async Task Tenant_context_and_patient_lookup_prevent_cross_tenant_access()
    {
        await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetSummaryAsync("tenant-b", 101));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetLedgerAsync("tenant-b", 101));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.PostAsync(Command(PatientLedgerEntryType.Charge,
            100m, "tenant-b-source", PatientLedgerSourceType.Procedure) with { TenantId = "tenant-b" }));
    }

    [Fact]
    public async Task Authenticated_tenant_claim_allows_access_when_provider_falls_back_to_default_tenant()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var tenant = new FixedTenantProvider("default-tenant", AuthenticatedUser(new System.Security.Claims.Claim("TenantId", "tenant-a")));
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        using var db = new CloudDentalDbContext(options, tenant);
        db.Database.EnsureCreated();
        db.Patients.Add(new Patient { TenantId = "tenant-a", PatientId = 101, FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        db.SaveChanges();

        var service = new PatientAccountService(db, TimeProvider.System, tenant, NullLogger<PatientAccountService>.Instance);
        var entry = await service.PostAsync(Command(PatientLedgerEntryType.Charge, 100m, "procedure-claim", PatientLedgerSourceType.Procedure));

        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal(100m, (await service.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
    }

    [Fact]
    public async Task Concurrent_reversal_unique_violations_are_translated()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var tenant = new FixedTenantProvider("tenant-a");
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        using var db = new ThrowingCloudDentalDbContext(options, tenant,
            new SqliteException("SQLite Error 19: 'UNIQUE constraint failed'.", 19, 2067));
        db.Database.EnsureCreated();
        db.Patients.Add(new Patient { TenantId = "tenant-a", PatientId = 101, FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        db.SaveChanges();

        var service = new PatientAccountService(db, TimeProvider.System, tenant, NullLogger<PatientAccountService>.Instance);
        var charge = await service.PostAsync(Command(PatientLedgerEntryType.Charge, 125m, "procedure-race", PatientLedgerSourceType.Procedure));

        db.ThrowUniqueViolationOnSave = true;
        var exception = await Assert.ThrowsAsync<DuplicateLedgerSourceException>(() =>
            service.ReverseAsync("tenant-a", charge.LedgerEntryId, "correction-race", "staff:42", _effective));
        Assert.Equal("The ledger entry was concurrently reversed.", exception.Message);
    }

    [Fact]
    public async Task Posted_history_cannot_be_updated_or_deleted()
    {
        var entry = await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        entry.Amount = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        _db.Entry(entry).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Invalid_amount_precision_and_mixed_currency_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(1.001m));
        await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PostAsync(Command(PatientLedgerEntryType.Charge,
            25m, "procedure-2", PatientLedgerSourceType.Procedure) with { Amount = new Money(25m, "EUR") }));
    }

    [Fact]
    public void Staff_api_tenant_is_only_resolved_from_trusted_claims()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim("TenantId", "tenant-a")], "test"));
        Assert.Equal("tenant-a", PatientAccountApi.TrustedTenantId(user));
        var blank = new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim("TenantId", "   ")], "test"));
        Assert.Null(PatientAccountApi.TrustedTenantId(blank));
        Assert.Null(PatientAccountApi.TrustedTenantId(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    private Task<PatientLedgerEntry> Post(PatientLedgerEntryType type, decimal amount, string sourceId, PatientLedgerSourceType sourceType) =>
        _service.PostAsync(Command(type, amount, sourceId, sourceType));

    private PostPatientLedgerEntry Command(PatientLedgerEntryType type, decimal amount, string sourceId,
        PatientLedgerSourceType sourceType) => new("tenant-a", 101, type, new Money(amount), _effective,
            sourceType, sourceId, "test-category", "staff:42");

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private static ClaimsPrincipal AuthenticatedUser(params System.Security.Claims.Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user = null) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User { get; } = user;
    }

    private sealed class ThrowingCloudDentalDbContext(
        DbContextOptions<CloudDentalDbContext> options,
        ITenantProvider tenantProvider,
        Exception saveException) : CloudDentalDbContext(options, tenantProvider)
    {
        public bool ThrowUniqueViolationOnSave { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            ThrowUniqueViolationOnSave
                ? throw new DbUpdateException("Simulated unique violation.", saveException)
                : base.SaveChangesAsync(cancellationToken);
    }
}

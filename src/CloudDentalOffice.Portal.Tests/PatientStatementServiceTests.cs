using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientStatementServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 8, 19, 15, 0, 0, TimeSpan.Zero));
    private readonly CloudDentalDbContext _db;
    private readonly PatientAccountService _accounts;
    private readonly PatientStatementService _statements;
    private readonly PatientResponsibilityService _responsibility;

    public PatientStatementServiceTests()
    {
        _connection.Open();
        var tenant = new FixedTenantProvider("tenant-a");
        _db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, tenant);
        _db.Database.EnsureCreated();
        _db.Patients.Add(new Patient { PatientId = 101, TenantId = "tenant-a", FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.SaveChanges();
        _accounts = new PatientAccountService(_db, _clock, tenant, NullLogger<PatientAccountService>.Instance);
        _statements = new PatientStatementService(_db, tenant, _clock, NullLogger<PatientStatementService>.Instance);
        _responsibility = new PatientResponsibilityService(_accounts);
    }

    [Fact]
    public async Task Estimated_and_finalized_responsibility_are_explicitly_distinct()
    {
        var estimate = _responsibility.CalculateEstimate(new Money(850m), new Money(422.60m), new Money(100m), Now);
        Assert.Equal(PatientResponsibilityKind.Estimated, estimate.Kind);
        Assert.Equal(327.40m, estimate.PatientDue);
        Assert.Empty(_db.PatientLedgerEntries);

        await Post(PatientLedgerEntryType.Charge, 850m, "procedure-1", PatientLedgerSourceType.Procedure);
        await Post(PatientLedgerEntryType.InsurancePayment, 422.60m, "era-1", PatientLedgerSourceType.Era);
        await Post(PatientLedgerEntryType.ContractualAdjustment, 100m, "era-1", PatientLedgerSourceType.Era);
        var finalized = await _responsibility.GetFinalizedAsync("tenant-a", 101);
        Assert.Equal(PatientResponsibilityKind.Finalized, finalized!.Kind);
        Assert.Equal(327.40m, finalized.PatientDue);
    }

    [Theory]
    [InlineData("charges")]
    [InlineData("estimatedInsurancePayment")]
    [InlineData("estimatedAdjustment")]
    public void Negative_estimate_reports_the_actual_invalid_parameter(string parameter)
    {
        var charges = new Money(parameter == "charges" ? -1m : 100m);
        var insurance = new Money(parameter == "estimatedInsurancePayment" ? -1m : 50m);
        var adjustment = new Money(parameter == "estimatedAdjustment" ? -1m : 10m);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            _responsibility.CalculateEstimate(charges, insurance, adjustment, Now));
        Assert.Equal(parameter, exception.ParamName);
    }

    [Fact]
    public async Task Statement_generation_snapshots_ledger_totals_and_safe_lines()
    {
        await Post(PatientLedgerEntryType.Charge, 850m, "procedure-secret", PatientLedgerSourceType.Procedure, "implant-detail");
        await Post(PatientLedgerEntryType.InsurancePayment, 422.60m, "era-1", PatientLedgerSourceType.Era);
        await Post(PatientLedgerEntryType.ContractualAdjustment, 100m, "era-1", PatientLedgerSourceType.Era);
        var statement = await Create(finalize: true);
        Assert.Equal(PatientStatementStatus.Ready, statement.Status);
        Assert.Equal(850m, statement.NewCharges);
        Assert.Equal(422.60m, statement.InsurancePayments);
        Assert.Equal(100m, statement.Adjustments);
        Assert.Equal(327.40m, statement.AmountDue);
        Assert.Equal(3, statement.Lines.Count);
        Assert.DoesNotContain(statement.Lines, x => x.PatientDescription.Contains("implant", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(statement.Lines, x => x.PatientDescription.Contains("procedure-secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Later_statement_uses_balance_forward_and_new_partial_payment()
    {
        await Post(PatientLedgerEntryType.Charge, 500m, "procedure-1", PatientLedgerSourceType.Procedure);
        var first = await Create(finalize: true);
        _clock.Advance(TimeSpan.FromDays(1));
        await Post(PatientLedgerEntryType.PatientPayment, 125m, "payment-1", PatientLedgerSourceType.PatientPayment);
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Create(finalize: true);
        Assert.Equal(first.AmountDue, second.BalanceForward);
        Assert.Equal(125m, second.PatientPayments);
        Assert.Equal(375m, second.AmountDue);
        Assert.Single(second.Lines);
    }

    [Fact]
    public async Task Sent_statement_supports_partial_then_paid_status()
    {
        await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        var statement = await Create(finalize: true);
        await _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.Sent);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.PartiallyPaid));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await Post(PatientLedgerEntryType.PatientPayment, 40m, "payment-1", PatientLedgerSourceType.PatientPayment);
        await _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.PartiallyPaid);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.Paid));
        await Post(PatientLedgerEntryType.Charge, 75m, "procedure-2", PatientLedgerSourceType.Procedure);
        await Post(PatientLedgerEntryType.PatientPayment, 60m, "payment-2", PatientLedgerSourceType.PatientPayment);
        var paid = await _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.Paid);
        Assert.Equal(PatientStatementStatus.Paid, paid.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _statements.TransitionAsync("tenant-a", statement.StatementId, PatientStatementStatus.Sent));
    }

    [Fact]
    public async Task Finalized_statement_is_not_rewritten_by_later_ledger_activity()
    {
        await Post(PatientLedgerEntryType.Charge, 500m, "procedure-1", PatientLedgerSourceType.Procedure);
        var statement = await Create(finalize: true);
        _clock.Advance(TimeSpan.FromDays(1));
        await Post(PatientLedgerEntryType.PatientPayment, 200m, "payment-1", PatientLedgerSourceType.PatientPayment);
        _db.ChangeTracker.Clear();
        var historical = await _statements.GetAsync("tenant-a", statement.StatementId);
        Assert.Equal(500m, historical!.AmountDue);
        Assert.Single(historical.Lines);
        historical.AmountDue = 300m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Draft_can_be_finalized_and_voided_statement_is_not_a_balance_forward()
    {
        await Post(PatientLedgerEntryType.Charge, 200m, "procedure-1", PatientLedgerSourceType.Procedure);
        var draft = await Create(finalize: false);
        Assert.Equal(PatientStatementStatus.Draft, draft.Status);
        Assert.Equal(PatientStatementStatus.Ready,
            (await _statements.FinalizeAsync("tenant-a", draft.StatementId)).Status);
        await _statements.VoidAsync("tenant-a", draft.StatementId, "incorrect-recipient");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var preview = await Preview();
        Assert.Equal(0m, preview.BalanceForward);
        Assert.Equal(200m, preview.NewCharges);
    }

    [Fact]
    public async Task Newer_same_account_statement_can_supersede_original_without_rewriting_snapshot()
    {
        await Post(PatientLedgerEntryType.Charge, 200m, "procedure-1", PatientLedgerSourceType.Procedure);
        var original = await Create(finalize: true);
        var replacement = await Create(finalize: true);
        Assert.Equal(original.CreatedAt, replacement.CreatedAt);
        var result = await _statements.SupersedeAsync("tenant-a", original.StatementId, replacement.StatementId);
        Assert.Equal(PatientStatementStatus.Superseded, result.Status);
        Assert.Equal(replacement.StatementId, result.SupersededByStatementId);
        Assert.Equal(original.StatementId, replacement.SupersedesStatementId);
        Assert.Equal(200m, original.AmountDue);
    }

    [Fact]
    public async Task Tenant_isolation_applies_to_preview_list_detail_and_mutations()
    {
        await Post(PatientLedgerEntryType.Charge, 200m, "procedure-1", PatientLedgerSourceType.Procedure);
        var statement = await Create(finalize: true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _statements.PreviewAsync("tenant-b", 101, Now, Now.AddDays(30), Now));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _statements.ListAsync("tenant-b"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _statements.GetAsync("tenant-b", statement.StatementId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _statements.VoidAsync("tenant-b", statement.StatementId, "test"));
    }

    [Fact]
    public async Task Statement_lines_and_statements_cannot_be_deleted()
    {
        await Post(PatientLedgerEntryType.Charge, 100m, "procedure-1", PatientLedgerSourceType.Procedure);
        var statement = await Create(finalize: true);
        _db.Entry(statement.Lines.Single()).State = EntityState.Deleted;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
        _db.ChangeTracker.Clear();
        var loaded = await _db.PatientStatements.SingleAsync(x => x.StatementId == statement.StatementId);
        _db.PatientStatements.Remove(loaded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private Task<PatientStatementPreview> Preview() => _statements.PreviewAsync("tenant-a", 101, Now, Now.AddDays(30), Now);
    private Task<PatientStatement> Create(bool finalize) =>
        _statements.CreateAsync("tenant-a", 101, Now, Now.AddDays(30), Now, finalize, "staff:42");

    private async Task Post(PatientLedgerEntryType type, decimal amount, string sourceId,
        PatientLedgerSourceType sourceType, string description = "account-activity")
    {
        await _accounts.PostAsync(new PostPatientLedgerEntry("tenant-a", 101, type, new Money(amount), Now,
            sourceType, sourceId, description, "staff:42"));
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}

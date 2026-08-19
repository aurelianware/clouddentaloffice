using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StaffPatientBillingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTenant _tenant = new("tenant-a");
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 8, 19, 18, 0, 0, TimeSpan.Zero));
    private readonly CloudDentalDbContext _db;
    private readonly PatientAccountService _accounts;
    private readonly StaffPatientBillingService _service;
    private PatientLedgerEntry _charge = null!;

    public StaffPatientBillingTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        _db.Patients.AddRange(Patient(101, "tenant-a", "Alex"), Patient(202, "tenant-b", "Other"));
        _db.SaveChanges();
        _accounts = new(_db, _clock, _tenant, NullLogger<PatientAccountService>.Instance);
        var statements = new PatientStatementService(_db, _tenant, _clock, NullLogger<PatientStatementService>.Instance);
        var allocations = new PaymentAllocationService(_db, _tenant, _clock);
        _service = new(_db, _accounts, statements, allocations, _tenant, _clock);
        _charge = _accounts.PostAsync(new("tenant-a", 101, PatientLedgerEntryType.Charge, new Money(500m),
            _clock.GetUtcNow().UtcDateTime, PatientLedgerSourceType.Procedure, "procedure-1", "dental-services", "system"))
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Clinical_staff_has_no_billing_access_by_default()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetAccountAsync(User("Staff"), 101));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RecordPaymentAsync(User("Staff"),
            Payment(PatientPaymentMethod.Cash, "cash-1")));
    }

    [Fact]
    public async Task Billing_staff_can_view_and_post_but_cannot_adjust()
    {
        Assert.Equal(500m, (await _service.GetAccountAsync(User("BillingStaff"), 101)).Summary.Balance.AmountDue);
        await _service.RecordPaymentAsync(User("BillingStaff"), Payment(PatientPaymentMethod.Cash, "cash-1"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.PostAdjustmentAsync(User("BillingStaff"),
            new(101, new Money(10m), PatientLedgerEntryType.Credit, "courtesy", _clock.GetUtcNow().UtcDateTime)));
    }

    [Theory]
    [InlineData(PatientPaymentMethod.Cash, PaymentProcessorProvider.Office, "cash-1")]
    [InlineData(PatientPaymentMethod.Check, PaymentProcessorProvider.Office, "check-100")]
    [InlineData(PatientPaymentMethod.External, PaymentProcessorProvider.External, "external-100")]
    public async Task Manual_payment_posts_payment_and_immutable_ledger(PatientPaymentMethod method,
        PaymentProcessorProvider processor, string reference)
    {
        var payment = await _service.RecordPaymentAsync(User("BillingStaff"), Payment(method, reference));
        Assert.Equal(processor, payment.Processor);
        Assert.Equal("staff@example.com", payment.CreatedBy);
        Assert.Equal(400m, (await _accounts.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
        Assert.Contains(_db.FinancialAuditEvents, x => x.Action == "PaymentRecorded" && x.Actor == "staff@example.com");
        var ledger = await _db.PatientLedgerEntries.SingleAsync(x => x.LedgerEntryId == payment.LedgerEntryId);
        ledger.Amount = 1m;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(" receipt with spaces ")]
    [InlineData("receipt/100")]
    [InlineData("receipt#100")]
    public async Task Manual_payment_rejects_noncanonical_internal_reference(string reference)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.RecordPaymentAsync(User("BillingStaff"),
            Payment(PatientPaymentMethod.Cash, reference)));
    }

    [Fact]
    public async Task Duplicate_manual_payment_reference_returns_a_domain_conflict()
    {
        await _service.RecordPaymentAsync(User("BillingStaff"), Payment(PatientPaymentMethod.Check, "check-duplicate"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordPaymentAsync(
            User("BillingStaff"), Payment(PatientPaymentMethod.Check, "check-duplicate")));
        Assert.Contains("already exists", error.Message);
        Assert.Single(_db.PatientPayments.Where(x => x.InternalPaymentReference == "check-duplicate"));
    }

    [Fact]
    public async Task Adjustment_and_reversal_preserve_history_and_actor()
    {
        await _service.PostAdjustmentAsync(User("BillingAdmin"), new(101, new Money(25m),
            PatientLedgerEntryType.Credit, "courtesy", _clock.GetUtcNow().UtcDateTime));
        var payment = await _service.RecordPaymentAsync(User("BillingAdmin"), Payment(PatientPaymentMethod.Check, "check-1"));
        var reversal = await _service.ReverseManualPaymentAsync(User("BillingAdmin"), payment.PaymentId, "entered-twice");
        Assert.Equal(payment.LedgerEntryId, reversal.ReversalOfEntryId);
        Assert.Equal(-100m, reversal.Amount);
        Assert.Equal(PaymentStatus.Cancelled, payment.Status);
        Assert.Equal("staff@example.com", payment.ReversedBy);
        Assert.Equal(475m, (await _accounts.GetSummaryAsync("tenant-a", 101))!.Balance.AmountDue);
        Assert.Equal(4, await _db.PatientLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task Allocation_enforces_payment_and_target_limits_and_unapply_is_audited()
    {
        var payment = await _service.RecordPaymentAsync(User("BillingStaff"), Payment(PatientPaymentMethod.Cash, "cash-1"));
        var result = await _service.AllocateAsync(User("BillingStaff"), payment.PaymentId, _charge.LedgerEntryId, new Money(80m));
        Assert.Equal(20m, result.UnappliedAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.AllocateAsync(User("BillingStaff"),
            payment.PaymentId, _charge.LedgerEntryId, new Money(30m)));
        var allocation = await _db.PatientPaymentAllocations.SingleAsync();
        await _service.UnapplyAsync(User("BillingStaff"), allocation.PaymentAllocationId, "move-to-other-charge");
        Assert.NotNull(allocation.UnappliedAt);
        Assert.Equal(100m, (await new PaymentAllocationService(_db, _tenant, _clock)
            .GetAllocationAsync("tenant-a", payment.PaymentId)).UnappliedAmount);
        Assert.Contains(_db.FinancialAuditEvents, x => x.Action == "PaymentUnapplied");
        _db.PatientPaymentAllocations.Remove(allocation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Cross_tenant_patient_and_payment_are_rejected()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetAccountAsync(User("BillingAdmin"), 202));
        var otherUser = User("BillingAdmin", "tenant-b");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetDashboardAsync(otherUser, _clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task Dashboard_totals_only_authenticated_tenant_and_separates_channels()
    {
        await _service.RecordPaymentAsync(User("BillingStaff"), Payment(PatientPaymentMethod.Cash, "cash-1"));
        var account = await _db.PatientAccounts.SingleAsync(x => x.TenantId == "tenant-a");
        _db.PatientPayments.AddRange(
            new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = account.Id,
                Amount = 75m, Currency = "USD", PaymentDate = _clock.GetUtcNow().UtcDateTime, Method = PatientPaymentMethod.Card,
                Processor = PaymentProcessorProvider.Stripe, InternalPaymentReference = "stripe-ok", Status = PaymentStatus.Succeeded,
                CreatedAt = _clock.GetUtcNow().UtcDateTime, UpdatedAt = _clock.GetUtcNow().UtcDateTime },
            new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = account.Id,
                Amount = 20m, Currency = "USD", PaymentDate = _clock.GetUtcNow().UtcDateTime, Method = PatientPaymentMethod.Card,
                Processor = PaymentProcessorProvider.Stripe, InternalPaymentReference = "stripe-fail", Status = PaymentStatus.Failed,
                CreatedAt = _clock.GetUtcNow().UtcDateTime, UpdatedAt = _clock.GetUtcNow().UtcDateTime });
        await _db.SaveChangesAsync();
        var dashboard = await _service.GetDashboardAsync(User("BillingAdmin"), _clock.GetUtcNow().UtcDateTime);
        Assert.Equal(175m, dashboard.TodayCollected);
        Assert.Equal(75m, dashboard.OnlinePayments);
        Assert.Equal(100m, dashboard.OfficePayments);
        Assert.Equal(20m, dashboard.FailedOnlinePayments);
    }

    [Fact]
    public async Task Refund_loading_uses_a_server_side_payment_subquery_for_large_accounts()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var payments = Enumerable.Range(0, 1_050).Select(index => new PatientPayment
        {
            PaymentId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = _charge.PatientAccountId,
            Amount = 1m, Currency = "USD", PaymentDate = now, Method = PatientPaymentMethod.Card,
            Processor = PaymentProcessorProvider.Stripe, InternalPaymentReference = $"bulk-{index}",
            Status = PaymentStatus.Succeeded, CreatedAt = now, UpdatedAt = now
        }).ToList();
        _db.PatientPayments.AddRange(payments);
        _db.PatientRefunds.Add(new PatientRefund
        {
            RefundId = Guid.NewGuid(), TenantId = "tenant-a", PaymentId = payments[^1].PaymentId,
            Amount = 1m, Currency = "USD", Reason = "requested_by_customer",
            Processor = PaymentProcessorProvider.Stripe, InternalRefundReference = "bulk-refund",
            Status = PatientRefundStatus.Pending, RequestedBy = "test", RequestedAt = now
        });
        await _db.SaveChangesAsync();

        var account = await _service.GetAccountAsync(User("BillingAdmin"), 101);
        Assert.Equal(1_050, account.Payments.Count);
        Assert.Contains("Pending", account.Payments.Single(x => x.PaymentId == payments[^1].PaymentId).RefundStatus);
    }

    private RecordManualPayment Payment(PatientPaymentMethod method, string reference) =>
        new(101, new Money(100m), method, reference, _clock.GetUtcNow().UtcDateTime);
    private static Patient Patient(int id, string tenant, string first) => new() { PatientId = id, TenantId = tenant,
        FirstName = first, LastName = "Patient", DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" };
    private static ClaimsPrincipal User(string role, string tenant = "tenant-a") => new(new ClaimsIdentity(new[]
    {
        new System.Security.Claims.Claim(ClaimTypes.Role, role),
        new System.Security.Claims.Claim(ClaimTypes.Email, "staff@example.com"),
        new System.Security.Claims.Claim("tenant_id", tenant)
    }, "test"));
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
    private sealed class FixedTenant(string tenant) : ITenantProvider { public string TenantId => tenant; public ClaimsPrincipal? User => null; }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}

using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripeRefundWebhookTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly StripePaymentMetrics _metrics = new();
    private readonly Guid _paymentId = Guid.NewGuid();
    private readonly Guid _refundId = Guid.NewGuid();
    private readonly Guid _chargeId = Guid.NewGuid();

    public StripeRefundWebhookTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new DefaultTenantProvider());
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        _db.Patients.Add(new Patient { PatientId = 1, TenantId = "tenant-a", FirstName = "Test",
            LastName = "Patient", DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PatientAccounts.Add(new PatientAccount { Id = accountId, TenantId = "tenant-a", PatientId = 1,
            CreatedAt = now, UpdatedAt = now });
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(),
            TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe, Enabled = true,
            Environment = PaymentProcessorEnvironment.Sandbox, ConnectedMerchantReference = "acct_practice",
            CredentialReference = "secret", CreatedAt = now, UpdatedAt = now });
        _db.PatientLedgerEntries.AddRange(
            new PatientLedgerEntry { LedgerEntryId = _chargeId, TenantId = "tenant-a", PatientAccountId = accountId,
                EntryType = PatientLedgerEntryType.Charge, Amount = 100m, Currency = "USD", EffectiveDate = now,
                SourceType = PatientLedgerSourceType.Procedure, SourceId = "charge", DescriptionCode = "charge",
                CreatedAt = now, CreatedBy = "test" },
            new PatientLedgerEntry { LedgerEntryId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = accountId,
                EntryType = PatientLedgerEntryType.PatientPayment, Amount = 100m, Currency = "USD", EffectiveDate = now,
                SourceType = PatientLedgerSourceType.PatientPayment, SourceId = _paymentId.ToString("N"),
                DescriptionCode = "payment", CreatedAt = now, CreatedBy = "processor:Stripe" });
        _db.PatientPayments.Add(new PatientPayment { PaymentId = _paymentId, TenantId = "tenant-a",
            PatientAccountId = accountId, Amount = 100m, Currency = "USD", PaymentDate = now,
            Method = PatientPaymentMethod.Card, Processor = PaymentProcessorProvider.Stripe,
            ExternalPaymentId = "pi_test", InternalPaymentReference = "pay_test", Status = PaymentStatus.Succeeded,
            CreatedAt = now, UpdatedAt = now });
        _db.PatientPaymentAllocations.Add(new PatientPaymentAllocation { PaymentAllocationId = Guid.NewGuid(),
            TenantId = "tenant-a", PaymentId = _paymentId, LedgerEntryId = _chargeId, Amount = 100m,
            CreatedAt = now, CreatedBy = "processor:Stripe" });
        _db.PatientRefunds.Add(new PatientRefund { RefundId = _refundId, TenantId = "tenant-a",
            PaymentId = _paymentId, Amount = 40m, Currency = "USD", Reason = "requested_by_customer",
            Processor = PaymentProcessorProvider.Stripe, InternalRefundReference = "refund_test",
            ExternalRefundId = "re_test", Status = PatientRefundStatus.Pending, RequestedBy = "staff",
            RequestedAt = now });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Confirmed_partial_refund_posts_once_and_reduces_allocation()
    {
        await Service().ProcessAsync(Event());
        await Service().ProcessAsync(Event());
        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientRefundStatus.Succeeded, refund.Status);
        Assert.NotNull(refund.LedgerEntryId);
        Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync());
        Assert.Equal(60m, (await _db.PatientPaymentAllocations.IgnoreQueryFilters()
            .Where(x => !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync()).Sum());
        Assert.Equal(40m, PatientAccountService.Calculate(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .ToListAsync()).AmountDue);
    }

    [Fact]
    public async Task Confirmed_full_refund_unapplies_the_entire_payment()
    {
        await _db.PatientRefunds.IgnoreQueryFilters().ExecuteUpdateAsync(x => x.SetProperty(r => r.Amount, 100m));
        _db.ChangeTracker.Clear();
        await Service().ProcessAsync(Event() with { AmountMinor = 10000 });
        Assert.Empty(await _db.PatientPaymentAllocations.IgnoreQueryFilters()
            .Where(x => !x.UnappliedAt.HasValue).ToListAsync());
        Assert.Equal(100m, PatientAccountService.Calculate(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .ToListAsync()).AmountDue);
    }

    [Fact]
    public async Task Failed_refund_does_not_change_ledger_or_allocations()
    {
        await Service().ProcessAsync(Event() with { EventType = "refund.failed", RefundStatus = "failed" });
        Assert.Equal(PatientRefundStatus.Failed,
            (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync());
        Assert.Equal(100m, (await _db.PatientPaymentAllocations.IgnoreQueryFilters()
            .Where(x => !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync()).Sum());
    }

    [Theory]
    [InlineData(4100, "USD", "amount-mismatch")]
    [InlineData(4000, "EUR", "currency-mismatch")]
    public async Task Mismatch_requires_review_without_posting(long amount, string currency, string code)
    {
        await Service().ProcessAsync(Event() with { AmountMinor = amount, Currency = currency });
        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientRefundStatus.ReviewRequired, refund.Status);
        Assert.Equal(code, refund.FailureCode);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync());
    }

    [Fact]
    public async Task Connected_account_and_tenant_mapping_is_enforced()
    {
        await Assert.ThrowsAsync<StripeWebhookPermanentException>(() =>
            Service().ProcessAsync(Event() with { ConnectedAccountId = "acct_other" }));
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync());
    }

    private StripeRefundWebhookProcessor Service() => new(_db, TimeProvider.System, _metrics,
        NullLogger<StripeRefundWebhookProcessor>.Instance);
    private static StripeRefundWebhookEvent Event() => new("tenant-a", "evt_refund", "refund.updated",
        "acct_practice", "re_test", "pi_test", "refund_test", 4000, "USD", "succeeded", false);
    public void Dispose() { _metrics.Dispose(); _db.Dispose(); _connection.Dispose(); }
}

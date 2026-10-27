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
    private readonly Guid _paymentEntryId = Guid.NewGuid();

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
            new PatientLedgerEntry { LedgerEntryId = _paymentEntryId, TenantId = "tenant-a", PatientAccountId = accountId,
                EntryType = PatientLedgerEntryType.PatientPayment, Amount = 100m, Currency = "USD", EffectiveDate = now,
                SourceType = PatientLedgerSourceType.PatientPayment, SourceId = _paymentId.ToString("N"),
                DescriptionCode = "payment", CreatedAt = now, CreatedBy = "processor:Stripe" });
        _db.PatientPayments.Add(new PatientPayment { PaymentId = _paymentId, TenantId = "tenant-a",
            PatientAccountId = accountId, Amount = 100m, Currency = "USD", PaymentDate = now,
            Method = PatientPaymentMethod.Card, Processor = PaymentProcessorProvider.Stripe,
            ExternalPaymentId = "pi_test", InternalPaymentReference = "pay_test", Status = PaymentStatus.Succeeded,
            LedgerEntryId = _paymentEntryId, CreatedAt = now, UpdatedAt = now });
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

    [Fact]
    public async Task Refund_that_fails_after_succeeding_reverses_its_ledger_credit_once()
    {
        await Service().ProcessAsync(Event());
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_failed", EventType = "refund.failed", RefundStatus = "failed" });
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_failed_again", EventType = "refund.updated", RefundStatus = "failed" });

        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientRefundStatus.Reversed, refund.Status);
        var refundEntries = await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync();
        Assert.Equal(2, refundEntries.Count);
        Assert.Equal(0m, refundEntries.Sum(x => x.Amount));
        Assert.Contains(refundEntries, x => x.ReversalOfEntryId == refund.LedgerEntryId);
        Assert.Equal(0m, PatientAccountService.Calculate(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .ToListAsync()).AmountDue);
        Assert.Single(await _db.FinancialAuditEvents.IgnoreQueryFilters()
            .Where(x => x.Action == "RefundReversed").ToListAsync());

        // A stale succeeded event does not post the credit again.
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_stale_succeeded" });
        Assert.Equal(PatientRefundStatus.Reversed, (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(2, await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .CountAsync(x => x.EntryType == PatientLedgerEntryType.Refund));
    }

    [Fact]
    public async Task Refund_still_settles_after_the_practice_disables_online_payments()
    {
        await _db.PaymentProcessorConfigurations.IgnoreQueryFilters().ExecuteUpdateAsync(x => x.SetProperty(c => c.Enabled, false));
        _db.ChangeTracker.Clear();

        await Service().ProcessAsync(Event());

        Assert.Equal(PatientRefundStatus.Succeeded, (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Late_pending_event_does_not_move_a_succeeded_refund_back()
    {
        await Service().ProcessAsync(Event());
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_created", EventType = "refund.created", RefundStatus = "pending" });
        Assert.Equal(PatientRefundStatus.Succeeded, (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Succeeded_event_after_a_failure_goes_to_review_without_posting()
    {
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_failed", EventType = "refund.failed", RefundStatus = "failed" });
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_late_succeeded" });
        await Service().ProcessAsync(Event() with { ExternalEventId = "evt_late_succeeded_again" });
        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientRefundStatus.ReviewRequired, refund.Status);
        Assert.Equal("refund-succeeded-after-failure", refund.FailureCode);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync());
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
    public async Task Refund_made_in_the_Stripe_dashboard_is_recorded_and_posted_once()
    {
        var dashboard = Event() with { ExternalEventId = "evt_dashboard", ExternalRefundId = "re_dashboard",
            RefundReference = null, AmountMinor = 2500 };
        await Service().ProcessAsync(dashboard);
        await Service().ProcessAsync(dashboard with { ExternalEventId = "evt_dashboard_again" });

        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync(x => x.ExternalRefundId == "re_dashboard");
        Assert.Equal(PatientRefundStatus.Succeeded, refund.Status);
        Assert.Equal(25m, refund.Amount);
        Assert.Equal("stripe-dashboard", refund.Reason);
        Assert.Equal(_paymentId, refund.PaymentId);
        Assert.Equal(25m, Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.EntryType == PatientLedgerEntryType.Refund).ToListAsync()).Amount);
        Assert.Equal(75m, (await _db.PatientPaymentAllocations.IgnoreQueryFilters()
            .Where(x => !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync()).Sum());
        Assert.Contains(await _db.FinancialAuditEvents.IgnoreQueryFilters().ToListAsync(),
            x => x.Action == "RefundRecordedFromStripe" && x.EntityId == refund.RefundId.ToString("N"));
    }

    [Fact]
    public async Task Refund_of_a_payment_the_app_never_took_is_acknowledged_and_ignored()
    {
        await Service().ProcessAsync(Event() with { ExternalRefundId = "re_elsewhere", RefundReference = null,
            PaymentIntentId = "pi_elsewhere" });

        Assert.Single(await _db.PatientRefunds.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("not-an-app-payment", (await _db.PaymentProcessorEvents.IgnoreQueryFilters().SingleAsync()).FailureCode);
    }

    [Fact]
    public async Task Dashboard_refund_that_arrives_before_its_payment_posts_is_retried()
    {
        await _db.PatientPayments.IgnoreQueryFilters().ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, PaymentStatus.Pending));
        _db.ChangeTracker.Clear();

        // Exactly InvalidOperationException, not the permanent subclass: the broker retries it.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ProcessAsync(Event() with
            { ExternalRefundId = "re_dashboard", RefundReference = null }));
        Assert.Single(await _db.PatientRefunds.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Refund_with_an_unknown_reference_of_ours_is_still_rejected()
    {
        await Assert.ThrowsAsync<StripeWebhookPermanentException>(() => Service().ProcessAsync(Event() with
            { ExternalRefundId = "re_unknown", RefundReference = "refund_missing" }));
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

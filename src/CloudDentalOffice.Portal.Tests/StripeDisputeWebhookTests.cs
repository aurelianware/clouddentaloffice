using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripeDisputeWebhookTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly StripePaymentMetrics _metrics = new();
    private readonly Guid _paymentId = Guid.NewGuid();
    private readonly Guid _paymentEntryId = Guid.NewGuid();

    public StripeDisputeWebhookTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new DefaultTenantProvider());
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        var chargeId = Guid.NewGuid();
        _db.Patients.Add(new Patient { PatientId = 1, TenantId = "tenant-a", FirstName = "Test",
            LastName = "Patient", DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PatientAccounts.Add(new PatientAccount { Id = accountId, TenantId = "tenant-a", PatientId = 1,
            CreatedAt = now, UpdatedAt = now });
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(),
            TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe, Enabled = true,
            Environment = PaymentProcessorEnvironment.Sandbox, ConnectedMerchantReference = "acct_practice",
            CredentialReference = "secret", CreatedAt = now, UpdatedAt = now });
        _db.PatientLedgerEntries.AddRange(
            new PatientLedgerEntry { LedgerEntryId = chargeId, TenantId = "tenant-a", PatientAccountId = accountId,
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
            TenantId = "tenant-a", PaymentId = _paymentId, LedgerEntryId = chargeId, Amount = 100m,
            CreatedAt = now, CreatedBy = "processor:Stripe" });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Opened_dispute_is_flagged_for_review_without_touching_the_ledger()
    {
        await Service().ProcessAsync(Opened());
        await Service().ProcessAsync(Opened());
        await Service().ProcessAsync(Opened() with { ExternalEventId = "evt_opened_again" });

        var issue = Assert.Single(await OpenIssues());
        Assert.Equal("payment-disputed", issue.DiagnosticCode);
        Assert.Equal(_paymentId, issue.PaymentId);
        Assert.Equal(0m, await AmountDue());
        Assert.Contains(await _db.FinancialAuditEvents.IgnoreQueryFilters().ToListAsync(), x => x.Action == "PaymentDisputed");
    }

    [Fact]
    public async Task Lost_dispute_reverses_the_payment_once_so_the_patient_owes_it_again()
    {
        await Service().ProcessAsync(Opened());
        await Service().ProcessAsync(Closed("lost"));
        await Service().ProcessAsync(Closed("lost") with { ExternalEventId = "evt_closed_again" });

        Assert.Equal(100m, await AmountDue());
        var reversal = Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters()
            .Where(x => x.SourceType == PatientLedgerSourceType.SystemReversal).ToListAsync());
        Assert.Equal(-100m, reversal.Amount);
        Assert.Equal("dispute-lost", reversal.DescriptionCode);
        Assert.Empty(await _db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x => !x.UnappliedAt.HasValue).ToListAsync());
        var payment = await _db.PatientPayments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(reversal.LedgerEntryId, payment.ReversalLedgerEntryId);
        Assert.Empty(await OpenIssues());
        Assert.Contains(await _db.FinancialAuditEvents.IgnoreQueryFilters().ToListAsync(), x => x.Action == "PaymentDisputeLost");
    }

    [Fact]
    public async Task Partly_lost_dispute_reverses_only_the_disputed_amount()
    {
        await Service().ProcessAsync(Closed("lost") with { AmountMinor = 3000 });

        Assert.Equal(30m, await AmountDue());
        Assert.Equal(70m, (await _db.PatientPaymentAllocations.IgnoreQueryFilters()
            .Where(x => !x.UnappliedAt.HasValue).Select(x => x.Amount).ToListAsync()).Sum());
        Assert.Null((await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).ReversedAt);
    }

    [Fact]
    public async Task Lost_dispute_larger_than_the_payment_goes_to_review_without_posting()
    {
        await Service().ProcessAsync(Closed("lost") with { AmountMinor = 15000 });

        Assert.Equal(0m, await AmountDue());
        Assert.Equal("dispute-lost-amount-mismatch", Assert.Single(await OpenIssues()).DiagnosticCode);
    }

    [Theory]
    [InlineData("won")]
    [InlineData("warning_closed")]
    public async Task Won_or_withdrawn_dispute_closes_the_review_and_keeps_the_payment(string status)
    {
        await Service().ProcessAsync(Opened());
        await Service().ProcessAsync(Closed(status));

        Assert.Empty(await OpenIssues());
        Assert.Equal(0m, await AmountDue());
        Assert.Contains(await _db.FinancialAuditEvents.IgnoreQueryFilters().ToListAsync(),
            x => x.Action == "PaymentDisputeClosed" && x.ReasonCode == $"stripe-dispute-{status}");
    }

    [Fact]
    public async Task Dispute_on_a_payment_the_app_never_took_is_acknowledged_and_ignored()
    {
        await Service().ProcessAsync(Closed("lost") with { PaymentIntentId = "pi_elsewhere" });

        Assert.Empty(await OpenIssues());
        Assert.Equal(0m, await AmountDue());
        Assert.Equal("not-an-app-payment", (await _db.PaymentProcessorEvents.IgnoreQueryFilters().SingleAsync()).FailureCode);
    }

    [Fact]
    public async Task Dispute_for_another_connected_account_is_rejected()
    {
        await Assert.ThrowsAsync<StripeWebhookPermanentException>(() =>
            Service().ProcessAsync(Opened() with { ConnectedAccountId = "acct_other" }));
        Assert.Empty(await OpenIssues());
    }

    private Task<List<PaymentReconciliationIssue>> OpenIssues() => _db.PaymentReconciliationIssues.IgnoreQueryFilters()
        .Where(x => x.Status == PaymentReconciliationIssueStatus.ReviewRequired).ToListAsync();
    private async Task<decimal> AmountDue() =>
        PatientAccountService.Calculate(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync()).AmountDue;
    private StripeDisputeWebhookProcessor Service() => new(_db, TimeProvider.System, _metrics,
        NullLogger<StripeDisputeWebhookProcessor>.Instance);
    private static StripeDisputeWebhookEvent Opened() => new("tenant-a", "evt_opened", "charge.dispute.created",
        "acct_practice", "dp_test", "pi_test", 10000, "USD", "needs_response", false);
    private static StripeDisputeWebhookEvent Closed(string status) => Opened() with
        { ExternalEventId = "evt_closed", EventType = "charge.dispute.closed", DisputeStatus = status };
    public void Dispose() { _metrics.Dispose(); _db.Dispose(); _connection.Dispose(); }
}

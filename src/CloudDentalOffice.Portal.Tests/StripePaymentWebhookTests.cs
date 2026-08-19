using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripePaymentWebhookTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly StripePaymentMetrics _metrics = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _paymentId = Guid.NewGuid();
    private readonly Guid _attemptId = Guid.NewGuid();

    public StripePaymentWebhookTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new DefaultTenantProvider());
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        _db.Patients.Add(new Patient { PatientId = 101, TenantId = "tenant-a", FirstName = "Test", LastName = "Patient",
            Email = "patient@example.test",
            DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PatientAccounts.Add(new PatientAccount { Id = _accountId, TenantId = "tenant-a", PatientId = 101,
            Status = PatientAccountStatus.Active, CreatedAt = now, UpdatedAt = now });
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(), TenantId = "tenant-a",
            Provider = PaymentProcessorProvider.Stripe, Enabled = true, Environment = PaymentProcessorEnvironment.Sandbox,
            ConnectedMerchantReference = "acct_practice", CredentialReference = "secret-ref",
            OnboardingStatus = PaymentProcessorOnboardingStatus.Enabled, ChargesEnabled = true, PayoutsEnabled = true,
            CreatedAt = now, UpdatedAt = now });
        _db.PatientPayments.Add(new PatientPayment { PaymentId = _paymentId, TenantId = "tenant-a",
            PatientAccountId = _accountId, Amount = 50m, Currency = "USD", PaymentDate = now,
            Method = PatientPaymentMethod.Card, Processor = PaymentProcessorProvider.Stripe,
            ExternalSessionId = "cs_test", InternalPaymentReference = "pay_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Status = PaymentStatus.Pending, CreatedAt = now, UpdatedAt = now });
        _db.PatientPaymentAttempts.Add(new PatientPaymentAttempt { Id = _attemptId, TenantId = "tenant-a",
            PatientAccountId = _accountId, PaymentId = _paymentId, Selection = PatientPaymentSelection.FullBalance,
            Amount = 50m, Currency = "USD", PaymentReference = "pay_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Status = PatientPaymentAttemptStatus.SessionCreated, StripeCheckoutSessionId = "cs_test",
            ConnectedAccountId = "acct_practice", CreatedAt = now, UpdatedAt = now });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Paid_checkout_posts_once_and_duplicate_is_idempotent()
    {
        var webhook = Event();
        await Service().ProcessAsync(webhook);
        await Service().ProcessAsync(webhook);

        var payment = await _db.PatientPayments.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        Assert.NotNull(payment.LedgerEntryId);
        Assert.Equal("pi_test", payment.ExternalPaymentId);
        Assert.Equal(PatientPaymentAttemptStatus.Completed,
            (await _db.PatientPaymentAttempts.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Single(await _db.PaymentProcessorEvents.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(5100, "USD", "amount-mismatch")]
    [InlineData(5000, "EUR", "currency-mismatch")]
    public async Task Amount_or_currency_mismatch_requires_review(long amount, string currency, string code)
    {
        await Service().ProcessAsync(Event() with { AmountMinor = amount, Currency = currency });
        Assert.Equal(PaymentStatus.Pending, (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
        var attempt = await _db.PatientPaymentAttempts.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientPaymentAttemptStatus.ReviewRequired, attempt.Status);
        Assert.Equal(code, attempt.FailureCode);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(PaymentProcessorEventStatus.Conflict,
            (await _db.PaymentProcessorEvents.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("USD", 5000)]
    [InlineData("JPY", 50)]
    [InlineData("KWD", 50000)]
    public async Task Stripe_minor_units_use_the_currency_exponent(string currency, long amountMinor)
    {
        await _db.PatientPayments.IgnoreQueryFilters().Where(x => x.PaymentId == _paymentId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Currency, currency));
        await _db.PatientPaymentAttempts.IgnoreQueryFilters().Where(x => x.Id == _attemptId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Currency, currency));
        _db.ChangeTracker.Clear();

        await Service().ProcessAsync(Event() with { Currency = currency, AmountMinor = amountMinor });

        Assert.Equal(PaymentStatus.Succeeded,
            (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Async_failure_marks_payment_failed_without_posting()
    {
        await Service().ProcessAsync(Event() with
        {
            EventType = "checkout.session.async_payment_failed", PaymentStatus = "unpaid"
        });
        Assert.Equal(PaymentStatus.Failed, (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(PatientPaymentAttemptStatus.Failed,
            (await _db.PatientPaymentAttempts.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Completed_but_unpaid_waits_for_async_success()
    {
        await Service().ProcessAsync(Event() with { PaymentStatus = "unpaid" });
        Assert.Equal(PaymentStatus.Pending, (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());

        await Service().ProcessAsync(Event("evt_async") with { EventType = "checkout.session.async_payment_succeeded" });
        Assert.Single(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Statement_payment_is_allocated_and_marks_statement_paid()
    {
        var now = DateTime.UtcNow;
        var statementId = Guid.NewGuid();
        var chargeId = Guid.NewGuid();
        _db.PatientLedgerEntries.Add(new PatientLedgerEntry { LedgerEntryId = chargeId, TenantId = "tenant-a",
            PatientAccountId = _accountId, EntryType = PatientLedgerEntryType.Charge, Amount = 50m, Currency = "USD",
            EffectiveDate = now, SourceType = PatientLedgerSourceType.Procedure, SourceId = "procedure-charge",
            DescriptionCode = "charge", CreatedAt = now, CreatedBy = "test" });
        _db.PatientStatements.Add(new PatientStatement { StatementId = statementId, TenantId = "tenant-a",
            PatientAccountId = _accountId, StatementDate = now, DueDate = now.AddDays(30),
            Status = PatientStatementStatus.Sent, AmountDue = 50m, Currency = "USD", LedgerThroughDate = now,
            CreatedAt = now, CreatedBy = "test", StatusUpdatedAt = now });
        _db.PatientStatementLines.Add(new PatientStatementLine { StatementLineId = Guid.NewGuid(),
            TenantId = "tenant-a", StatementId = statementId, LedgerEntryId = chargeId, ActivityDate = now,
            EntryType = PatientLedgerEntryType.Charge, PatientDescription = "Account charge", Amount = 50m, Currency = "USD" });
        await _db.SaveChangesAsync();
        await _db.PatientPayments.IgnoreQueryFilters().Where(x => x.PaymentId == _paymentId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.StatementId, statementId));
        await _db.PatientPaymentAttempts.IgnoreQueryFilters().Where(x => x.Id == _attemptId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.StatementId, statementId));
        _db.ChangeTracker.Clear();

        await Service().ProcessAsync(Event());

        Assert.Equal(50m, (await _db.PatientPaymentAllocations.IgnoreQueryFilters().SingleAsync()).Amount);
        Assert.Equal(PatientStatementStatus.Paid,
            (await _db.PatientStatements.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Theory]
    [InlineData("tenant-b", "acct_practice")]
    [InlineData("tenant-a", "acct_unknown")]
    public async Task Tenant_or_connected_account_cannot_cross_mapping(string tenant, string account)
    {
        await Assert.ThrowsAsync<StripeWebhookPermanentException>(() =>
            Service().ProcessAsync(Event() with { TenantId = tenant, ConnectedAccountId = account }));
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Unknown_payment_reference_is_rejected_without_financial_changes()
    {
        await Assert.ThrowsAsync<StripeWebhookPermanentException>(() => Service().ProcessAsync(Event() with
            { PaymentReference = "pay_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }));
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Sandbox_event_cannot_post_without_explicit_test_tenant_allowlist()
    {
        var service = new StripePaymentWebhookProcessor(_db, TimeProvider.System, _metrics,
            Options.Create(new StripePaymentPostingOptions()),
            NullLogger<StripePaymentWebhookProcessor>.Instance);
        var error = await Assert.ThrowsAsync<StripeWebhookPermanentException>(() =>
            service.ProcessAsync(Event()));
        Assert.Contains("Sandbox ledger posting is not enabled", error.Message);
        Assert.Empty(await _db.PatientLedgerEntries.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData("checkout.session.completed", "paid", PatientBillingNotificationType.PaymentReceived)]
    [InlineData("checkout.session.async_payment_failed", "unpaid", PatientBillingNotificationType.PaymentFailed)]
    public async Task Payment_outcome_queues_privacy_safe_notification(string eventType, string paymentStatus,
        PatientBillingNotificationType expected)
    {
        var notifications = new CaptureNotifications();
        var service = new StripePaymentWebhookProcessor(_db, TimeProvider.System, _metrics,
            Options.Create(new StripePaymentPostingOptions { AllowedSandboxTenantIds = ["tenant-a"] }),
            NullLogger<StripePaymentWebhookProcessor>.Instance, notifications);
        await service.ProcessAsync(Event() with { EventType = eventType, PaymentStatus = paymentStatus });
        Assert.Equal(expected, Assert.Single(notifications.Types));
    }

    [Theory]
    [InlineData("checkout.session.completed", "paid", PaymentStatus.Succeeded, PatientPaymentAttemptStatus.Completed)]
    [InlineData("checkout.session.async_payment_failed", "unpaid", PaymentStatus.Failed, PatientPaymentAttemptStatus.Failed)]
    public async Task Notification_enqueue_failure_does_not_abort_payment_posting(string eventType, string paymentStatus,
        PaymentStatus expectedPaymentStatus, PatientPaymentAttemptStatus expectedAttemptStatus)
    {
        var service = new StripePaymentWebhookProcessor(_db, TimeProvider.System, _metrics,
            Options.Create(new StripePaymentPostingOptions { AllowedSandboxTenantIds = ["tenant-a"] }),
            NullLogger<StripePaymentWebhookProcessor>.Instance, new ThrowingNotifications());

        await service.ProcessAsync(Event() with { EventType = eventType, PaymentStatus = paymentStatus });

        Assert.Equal(expectedPaymentStatus,
            (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(expectedAttemptStatus,
            (await _db.PatientPaymentAttempts.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.Equal(PaymentProcessorEventStatus.Processed,
            (await _db.PaymentProcessorEvents.IgnoreQueryFilters().SingleAsync()).Status);
    }

    private StripePaymentWebhookProcessor Service() => new(_db, TimeProvider.System, _metrics,
        Options.Create(new StripePaymentPostingOptions { AllowedSandboxTenantIds = ["tenant-a"] }),
        NullLogger<StripePaymentWebhookProcessor>.Instance);
    private static StripePaymentWebhookEvent Event(string id = "evt_paid") => new("tenant-a", id,
        "checkout.session.completed", "acct_practice", "cs_test", "pi_test",
        "pay_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 5000, "USD", "paid", false)
        { OccurredAt = DateTime.UtcNow };

    private sealed class CaptureNotifications : IPatientBillingNotificationService
    {
        public List<PatientBillingNotificationType> Types { get; } = [];
        public Task<bool> EnqueueAsync(string tenantId, Guid patientAccountId,
            PatientBillingNotificationType type, string sourceType, string sourceId,
            CancellationToken cancellationToken = default)
        { Types.Add(type); return Task.FromResult(true); }
    }

    private sealed class ThrowingNotifications : IPatientBillingNotificationService
    {
        public Task<bool> EnqueueAsync(string tenantId, Guid patientAccountId,
            PatientBillingNotificationType type, string sourceType, string sourceId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("notification storage unavailable");
    }

    public void Dispose() { _metrics.Dispose(); _db.Dispose(); _connection.Dispose(); }
}

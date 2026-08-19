using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PaymentProcessingTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 8, 19, 20, 0, 0, TimeSpan.Zero));
    private readonly FixedTenantProvider _tenant = new("tenant-a");
    private readonly CloudDentalDbContext _db;
    private readonly FakeProcessor _processor = new();
    private readonly PatientAccountService _accounts;
    private readonly PaymentProcessorResolver _resolver;
    private PatientAccount _account = null!;
    private PatientLedgerEntry _charge = null!;

    public PaymentProcessingTests()
    {
        _connection.Open();
        _db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        _db.Patients.Add(new Patient { PatientId = 101, TenantId = "tenant-a", FirstName = "Test", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PaymentProcessorConfigurations.Add(Configuration(enabled: true));
        _db.SaveChanges();
        _accounts = new PatientAccountService(_db, _clock, _tenant, NullLogger<PatientAccountService>.Instance);
        _resolver = new PaymentProcessorResolver(_db, [_processor], _tenant);
    }

    [Fact]
    public async Task Resolver_returns_enabled_tenant_processor_without_exposing_credentials()
    {
        var (processor, configuration) = await _resolver.ResolveAsync("tenant-a");
        Assert.Same(_processor, processor);
        Assert.Equal("secret-reference", configuration.CredentialReference);
        Assert.DoesNotContain("Secret", typeof(PaymentProcessorConfiguration).GetProperties().Select(x => x.Name));
    }

    [Fact]
    public async Task Resolver_fails_closed_for_disabled_missing_and_duplicate_processors()
    {
        _db.PaymentProcessorConfigurations.Single().Enabled = false;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<PaymentProcessorUnavailableException>(() => _resolver.ResolveAsync("tenant-a"));
        Assert.Throws<InvalidOperationException>(() => new PaymentProcessorResolver(_db,
            [_processor, new FakeProcessor()], _tenant));
    }

    [Fact]
    public async Task Checkout_persists_canonical_payment_and_calls_only_neutral_adapter()
    {
        await SeedAccount();
        var checkout = Checkout();
        var session = await checkout.CreateAsync(Request(125m, "payment-1"));
        Assert.Equal("session-payment-1", session.ExternalSessionId);
        var payment = Assert.Single(_db.PatientPayments);
        Assert.Equal(_account.Id, payment.PatientAccountId);
        Assert.Equal(125m, payment.Amount);
        Assert.Equal("USD", payment.Currency);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
        Assert.Equal(PaymentProcessorProvider.Stripe, payment.Processor);
        Assert.Null(payment.ExternalPaymentId);
        Assert.Equal(1, _processor.SessionCalls);
    }

    [Fact]
    public async Task Checkout_rejects_duplicate_and_noncanonical_internal_references()
    {
        await SeedAccount();
        var checkout = Checkout();
        await checkout.CreateAsync(Request(125m, "payment-1"));

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            checkout.CreateAsync(Request(125m, "payment-1")));
        Assert.Equal("The internal payment reference already exists.", duplicate.Message);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            checkout.CreateAsync(Request(125m, " payment-2 ")));
        Assert.Single(_db.PatientPayments);
        Assert.Equal(1, _processor.SessionCalls);
    }

    [Fact]
    public async Task Successful_event_posts_exactly_one_patient_payment_ledger_entry()
    {
        await SeedAccount();
        await Checkout().CreateAsync(Request(125m, "payment-1"));
        var service = Reconciliation();
        var first = await service.ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 125m));
        var duplicate = await service.ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 125m));
        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.PaymentId, duplicate.PaymentId);
        Assert.Equal(first.LedgerEntryId, duplicate.LedgerEntryId);
        Assert.Single(_db.PaymentProcessorEvents);
        var ledger = _db.PatientLedgerEntries.Where(x => x.EntryType == PatientLedgerEntryType.PatientPayment).ToList();
        Assert.Single(ledger);
        Assert.Equal(125m, ledger[0].Amount);
    }

    [Fact]
    public async Task New_event_for_same_external_payment_remains_ledger_idempotent()
    {
        await SeedAccount();
        await Checkout().CreateAsync(Request(125m, "payment-1"));
        var service = Reconciliation();
        await service.ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 125m));
        var second = await service.ReconcileAsync(Event("event-2", "external-payment-1", "payment-1", 125m));
        Assert.False(second.Duplicate);
        Assert.Equal(2, _db.PaymentProcessorEvents.Count());
        Assert.Single(_db.PatientLedgerEntries.Where(x => x.EntryType == PatientLedgerEntryType.PatientPayment));
    }

    [Fact]
    public async Task Partial_allocation_preserves_unapplied_cash_and_overallocation_is_rejected()
    {
        await SeedAccount(charge: 100m);
        await Checkout().CreateAsync(Request(150m, "payment-1"));
        var result = await Reconciliation().ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 150m));
        var allocations = new PaymentAllocationService(_db, _tenant, _clock);
        var partial = await allocations.AllocateAsync("tenant-a", result.PaymentId, _charge.LedgerEntryId,
            new Money(60m), "staff:42");
        Assert.Equal(60m, partial.AllocatedAmount);
        Assert.Equal(90m, partial.UnappliedAmount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => allocations.AllocateAsync("tenant-a", result.PaymentId,
            _charge.LedgerEntryId, new Money(100m), "staff:42"));
        Assert.Equal(90m, (await allocations.GetAllocationAsync("tenant-a", result.PaymentId)).UnappliedAmount);
    }

    [Fact]
    public async Task Full_allocation_and_overpayment_are_both_supported()
    {
        await SeedAccount(charge: 100m);
        await Checkout().CreateAsync(Request(150m, "payment-1"));
        var result = await Reconciliation().ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 150m));
        var allocations = new PaymentAllocationService(_db, _tenant, _clock);
        var applied = await allocations.AllocateAsync("tenant-a", result.PaymentId, _charge.LedgerEntryId,
            new Money(100m), "staff:42");
        Assert.Equal(100m, applied.AllocatedAmount);
        Assert.Equal(50m, applied.UnappliedAmount);
    }

    [Fact]
    public async Task Failed_or_cancelled_event_does_not_post_financial_ledger_entry()
    {
        await SeedAccount();
        await Checkout().CreateAsync(Request(50m, "payment-1"));
        var failed = Event("event-1", "external-payment-1", "payment-1", 50m) with { Status = PaymentStatus.Failed };
        var result = await Reconciliation().ReconcileAsync(failed);
        Assert.Equal(PaymentStatus.Failed, result.Status);
        Assert.Null(result.LedgerEntryId);
        Assert.DoesNotContain(_db.PatientLedgerEntries, x => x.EntryType == PatientLedgerEntryType.PatientPayment);
    }

    [Fact]
    public async Task Refund_orchestration_uses_the_same_neutral_processor_boundary()
    {
        await SeedAccount();
        await Checkout().CreateAsync(Request(50m, "payment-1"));
        var payment = await Reconciliation().ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 50m));
        var refunds = new PaymentRefundService(_db, _resolver, _tenant);
        var result = await refunds.RefundAsync(new PaymentRefundRequest("tenant-a", payment.PaymentId,
            new Money(20m), "refund-1"));
        Assert.Equal(PaymentStatus.Pending, result.Status);
        Assert.Equal("refund-1", result.ExternalRefundId);
        Assert.Equal(1, _processor.RefundCalls);
        Assert.Equal(PatientRefundStatus.Pending,
            (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
        Assert.DoesNotContain(_db.PatientLedgerEntries, x => x.EntryType == PatientLedgerEntryType.Refund);
        await Assert.ThrowsAsync<InvalidOperationException>(() => refunds.RefundAsync(new PaymentRefundRequest(
            "tenant-a", payment.PaymentId, new Money(31m), "refund-2")));
    }

    [Fact]
    public async Task Transient_refund_transport_failure_is_durable_and_retryable()
    {
        await SeedAccount();
        await Checkout().CreateAsync(Request(50m, "payment-1"));
        var payment = await Reconciliation().ReconcileAsync(Event("event-1", "external-payment-1", "payment-1", 50m));
        var refunds = new PaymentRefundService(_db, _resolver, _tenant, _clock);
        _processor.RefundFailure = new HttpRequestException("temporary transport failure");
        await Assert.ThrowsAsync<HttpRequestException>(() => refunds.RefundAsync(new PaymentRefundRequest(
            "tenant-a", payment.PaymentId, new Money(20m), "refund-retry", RequestedBy: "staff")));
        var refund = await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PatientRefundStatus.Failed, refund.Status);
        Assert.Null(refund.ExternalRefundId);

        _processor.RefundFailure = null;
        var retried = await refunds.RetryAsync("tenant-a", refund.RefundId, "staff");
        Assert.Equal(PaymentStatus.Pending, retried.Status);
        Assert.Equal(PatientRefundStatus.Pending,
            (await _db.PatientRefunds.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Tenant_isolation_applies_to_resolution_checkout_reconciliation_and_allocation()
    {
        await SeedAccount();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _resolver.ResolveAsync("tenant-b"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Checkout().CreateAsync(Request(50m, "payment-1") with
            { TenantId = "tenant-b" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Reconciliation().ReconcileAsync(
            Event("event-1", "external-payment-1", "payment-1", 50m) with { TenantId = "tenant-b" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new PaymentAllocationService(_db, _tenant, _clock)
            .GetAllocationAsync("tenant-b", Guid.NewGuid()));
    }

    [Fact]
    public void Payment_entities_store_no_card_or_bank_account_details()
    {
        var names = typeof(PatientPayment).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("CardNumber", names);
        Assert.DoesNotContain("BankAccountNumber", names);
        Assert.DoesNotContain("Cvc", names);
        Assert.DoesNotContain("RoutingNumber", names);
    }

    private PaymentCheckoutService Checkout() => new(_db, _resolver, _tenant, _clock);
    private PaymentReconciliationService Reconciliation() => new(_db, _accounts, _tenant, _clock);
    private PaymentRequest Request(decimal amount, string reference) =>
        new("tenant-a", _account.Id, null, new Money(amount), reference, PatientPaymentMethod.Card);
    private ProcessorPaymentEvent Event(string eventId, string externalPaymentId, string reference, decimal amount) =>
        new("tenant-a", PaymentProcessorProvider.Stripe, eventId, externalPaymentId, reference,
            new Money(amount), PaymentStatus.Succeeded, _clock.GetUtcNow().UtcDateTime);

    private async Task SeedAccount(decimal charge = 200m)
    {
        _charge = await _accounts.PostAsync(new PostPatientLedgerEntry("tenant-a", 101, PatientLedgerEntryType.Charge,
            new Money(charge), _clock.GetUtcNow().UtcDateTime, PatientLedgerSourceType.Procedure,
            $"procedure-{Guid.NewGuid():N}", "dental-services", "system:test"));
        _account = await _db.PatientAccounts.SingleAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
    }

    private PaymentProcessorConfiguration Configuration(bool enabled) => new()
    {
        Id = Guid.NewGuid(), TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe,
        Enabled = enabled, Environment = PaymentProcessorEnvironment.Sandbox,
        CredentialReference = "secret-reference", ConnectedMerchantReference = "merchant-reference",
        CreatedAt = _clock.GetUtcNow().UtcDateTime, UpdatedAt = _clock.GetUtcNow().UtcDateTime
    };

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

    private sealed class FakeProcessor : IPaymentProcessor
    {
        public PaymentProcessorProvider Provider => PaymentProcessorProvider.Stripe;
        public int SessionCalls { get; private set; }
        public int RefundCalls { get; private set; }
        public Exception? RefundFailure { get; set; }
        public Task<PaymentSession> CreateSessionAsync(PaymentProcessorConfiguration configuration, PaymentRequest request,
            CancellationToken cancellationToken = default)
        {
            SessionCalls++;
            return Task.FromResult(new PaymentSession(request.InternalPaymentReference,
                $"session-{request.InternalPaymentReference}", null, new Uri("https://checkout.example.test/session"),
                null, DateTime.UtcNow.AddMinutes(30), PaymentStatus.Pending));
        }
        public Task<PaymentRefundResult> RefundAsync(PaymentProcessorConfiguration configuration,
            PaymentRefundRequest request, string externalPaymentId, CancellationToken cancellationToken = default)
        {
            RefundCalls++;
            if (RefundFailure is not null) throw RefundFailure;
            return Task.FromResult(new PaymentRefundResult(request.InternalRefundReference, "refund-1", PaymentStatus.Pending));
        }
    }
}

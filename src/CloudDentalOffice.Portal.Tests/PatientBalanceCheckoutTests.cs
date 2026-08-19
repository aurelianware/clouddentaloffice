using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientBalanceCheckoutTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTenantProvider _tenant = new("tenant-a");
    private readonly CloudDentalDbContext _db;
    private readonly FakeCheckout _checkout;
    private readonly PatientAccountService _accounts;
    private Guid _accountId;

    public PatientBalanceCheckoutTests()
    {
        _connection.Open();
        _db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        _checkout = new FakeCheckout(_db);
        _db.Patients.Add(new Patient { PatientId = 101, TenantId = "tenant-a", FirstName = "Private", LastName = "Patient",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PaymentProcessorConfigurations.Add(Configuration()); _db.SaveChanges();
        _accounts = new PatientAccountService(_db, TimeProvider.System, _tenant, NullLogger<PatientAccountService>.Instance);
    }

    [Fact]
    public async Task Full_balance_is_derived_server_side()
    {
        await SeedBalance(327.40m);
        var result = await Service().CreateAsync(Request(PatientPaymentSelection.FullBalance));
        Assert.Equal(327.40m, result.Amount.Amount); Assert.Equal(327.40m, _checkout.Last!.Amount.Amount);
        Assert.StartsWith("pay_", result.PaymentReference); Assert.Equal(PatientPaymentAttemptStatus.SessionCreated,
            (await _db.PatientPaymentAttempts.SingleAsync()).Status);
    }

    [Fact]
    public async Task Statement_balance_is_derived_from_snapshot_and_succeeded_statement_payments()
    {
        await SeedBalance(400m);
        var statementId = Guid.NewGuid();
        _db.PatientStatements.Add(new PatientStatement { StatementId = statementId, TenantId = "tenant-a",
            PatientAccountId = _accountId, StatementDate = DateTime.UtcNow, DueDate = DateTime.UtcNow.AddDays(30),
            Status = PatientStatementStatus.Sent, AmountDue = 225m, Currency = "USD", LedgerThroughDate = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow, CreatedBy = "test", StatusUpdatedAt = DateTime.UtcNow });
        _db.PatientPayments.Add(new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = "tenant-a",
            PatientAccountId = _accountId, StatementId = statementId, Amount = 25m, Currency = "USD",
            PaymentDate = DateTime.UtcNow, Method = PatientPaymentMethod.Card, Processor = PaymentProcessorProvider.Stripe,
            InternalPaymentReference = "prior-payment", Status = PaymentStatus.Succeeded, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        var result = await Service().CreateAsync(Request(PatientPaymentSelection.StatementBalance, statementId));
        Assert.Equal(200m, result.Amount.Amount);
    }

    [Fact]
    public async Task Partial_payment_is_validated_against_current_balance_and_maximum()
    {
        await SeedBalance(300m);
        var result = await Service(maximum: 250m).CreateAsync(Request(PatientPaymentSelection.Partial,
            custom: new Money(125m)));
        Assert.Equal(125m, result.Amount.Amount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(maximum: 100m).CreateAsync(
            Request(PatientPaymentSelection.Partial, custom: new Money(125m))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().CreateAsync(
            Request(PatientPaymentSelection.Partial, custom: new Money(301m))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Zero_and_negative_payments_are_rejected(decimal value)
    {
        await SeedBalance(100m);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Service().CreateAsync(
            Request(PatientPaymentSelection.Partial, custom: new Money(value))));
        Assert.Empty(_db.PatientPaymentAttempts);
    }

    [Theory]
    [InlineData(PatientPaymentSelection.FullBalance, true, false)]
    [InlineData(PatientPaymentSelection.FullBalance, false, true)]
    [InlineData(PatientPaymentSelection.StatementBalance, false, true)]
    [InlineData(PatientPaymentSelection.Partial, true, true)]
    public async Task Selection_rejects_fields_that_do_not_apply(PatientPaymentSelection selection,
        bool includeStatement, bool includeCustomAmount)
    {
        await SeedBalance(100m);
        var request = Request(selection, includeStatement ? Guid.NewGuid() : null,
            includeCustomAmount ? new Money(25m) : null);

        await Assert.ThrowsAsync<ArgumentException>(() => Service().CreateAsync(request));

        Assert.Empty(_db.PatientPaymentAttempts);
        Assert.Equal(0, _checkout.Calls);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Disabled_or_not_ready_Stripe_account_is_rejected(bool enabled, bool charges, bool payouts)
    {
        await SeedBalance(100m); var config = await _db.PaymentProcessorConfigurations.SingleAsync();
        config.Enabled = enabled; config.ChargesEnabled = charges; config.PayoutsEnabled = payouts;
        config.OnboardingStatus = charges && payouts ? PaymentProcessorOnboardingStatus.Enabled : PaymentProcessorOnboardingStatus.Pending;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<PaymentProcessorUnavailableException>(() => Service().CreateAsync(
            Request(PatientPaymentSelection.FullBalance)));
        Assert.Equal(0, _checkout.Calls);
    }

    [Fact]
    public async Task Multiple_sessions_have_distinct_opaque_references()
    {
        await SeedBalance(100m); var service = Service();
        var first = await service.CreateAsync(Request(PatientPaymentSelection.FullBalance));
        var second = await service.CreateAsync(Request(PatientPaymentSelection.FullBalance));
        Assert.NotEqual(first.PaymentReference, second.PaymentReference);
        Assert.Equal(2, await _db.PatientPaymentAttempts.CountAsync());
    }

    [Fact]
    public async Task Stripe_failure_leaves_a_sanitized_failed_attempt()
    {
        await SeedBalance(100m); _checkout.Failure = new StripeConnectException("remote failed");
        await Assert.ThrowsAsync<StripeConnectException>(() => Service().CreateAsync(Request(PatientPaymentSelection.FullBalance)));
        var attempt = await _db.PatientPaymentAttempts.SingleAsync();
        Assert.Equal(PatientPaymentAttemptStatus.Failed, attempt.Status);
        Assert.Equal("checkout-session-failed", attempt.FailureCode);
    }

    [Fact]
    public async Task Tenant_isolation_is_enforced_before_checkout()
    {
        await SeedBalance(100m);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().CreateAsync(
            Request(PatientPaymentSelection.FullBalance) with { TenantId = "tenant-b" }));
        Assert.Equal(0, _checkout.Calls);
    }

    [Fact]
    public async Task Stripe_request_contains_only_generic_presentation_and_opaque_reference()
    {
        var handler = new RecordingHandler("""
            {"id":"cs_test_opaque","payment_intent":"pi_test_opaque","url":"https://checkout.stripe.test/session","expires_at":1787171400}
            """);
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var api = new StripeApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com") },
            new ConfigurationStripeCredentialProvider(configuration), configuration);
        var opaqueReference = $"pay_{new string('a', 32)}";
        await api.CreateCheckoutSessionAsync(Configuration(), "acct_practice",
            new PaymentRequest("tenant-a", Guid.NewGuid(), null, new Money(50m), opaqueReference, PatientPaymentMethod.Card,
                "https://portal.example.test/payments/success?session_id={CHECKOUT_SESSION_ID}",
                "https://portal.example.test/payments/cancel"));
        Assert.Equal("acct_practice", handler.StripeAccount); Assert.Equal(opaqueReference, handler.IdempotencyKey);
        Assert.Contains("Account+payment", handler.Body); Assert.Contains("payment_reference", handler.Body);
        foreach (var prohibited in new[] { "Private", "Patient", "DOB", "diagnosis", "procedure", "tooth", "insurance", "claim", "medical" })
            Assert.DoesNotContain(prohibited, handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    private PatientBalanceCheckoutService Service(decimal maximum = 50_000m) => new(_db, _checkout, _tenant,
        Options.Create(new PatientCheckoutOptions { MaximumAmount = maximum, PublicBaseUrl = "https://portal.example.test" }),
        TimeProvider.System);
    private PatientBalanceCheckoutRequest Request(PatientPaymentSelection selection, Guid? statement = null, Money? custom = null) =>
        new("tenant-a", _accountId, selection, statement, custom);
    private async Task SeedBalance(decimal amount)
    {
        await _accounts.PostAsync(new PostPatientLedgerEntry("tenant-a", 101, PatientLedgerEntryType.Charge,
            new Money(amount), DateTime.UtcNow, PatientLedgerSourceType.Procedure, $"procedure-{Guid.NewGuid():N}",
            "account-charge", "test"));
        _accountId = (await _db.PatientAccounts.SingleAsync()).Id;
    }
    private static PaymentProcessorConfiguration Configuration() => new()
    {
        Id = Guid.NewGuid(), TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe, Enabled = true,
        Environment = PaymentProcessorEnvironment.Sandbox, CredentialReference = "Secrets:StripeTest",
        ConnectedMerchantReference = "acct_practice", OnboardingStatus = PaymentProcessorOnboardingStatus.Enabled,
        ChargesEnabled = true, PayoutsEnabled = true, DetailsSubmitted = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    { public string TenantId => tenantId; public ClaimsPrincipal? User => null; }
    private sealed class FakeCheckout(CloudDentalDbContext db) : IPaymentCheckoutService
    {
        public int Calls; public PaymentRequest? Last; public Exception? Failure;
        public Task<PaymentSession> CreateAsync(PaymentRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request; if (Failure is not null) throw Failure;
            db.PatientPayments.Add(new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = request.TenantId,
                PatientAccountId = request.PatientAccountId, StatementId = request.StatementId, Amount = request.Amount.Amount,
                Currency = request.Amount.Currency, PaymentDate = DateTime.UtcNow, Method = request.Method,
                Processor = PaymentProcessorProvider.Stripe, InternalPaymentReference = request.InternalPaymentReference,
                Status = PaymentStatus.Pending, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.SaveChanges();
            return Task.FromResult(new PaymentSession(request.InternalPaymentReference, $"cs_{Calls}", null,
                new Uri($"https://checkout.stripe.test/{Calls}"), null, DateTime.UtcNow.AddMinutes(30), PaymentStatus.Pending));
        }
    }
    private sealed class RecordingHandler(string response) : HttpMessageHandler
    {
        public string Body = string.Empty; public string? StripeAccount; public string? IdempotencyKey;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            StripeAccount = request.Headers.GetValues("Stripe-Account").Single();
            IdempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}

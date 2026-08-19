using System.Security.Claims;
using System.Net;
using System.Text;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripeConnectTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTenantProvider _tenant = new("tenant-a");
    private readonly CloudDentalDbContext _db;
    private readonly FakeStripeApiClient _api = new();
    private readonly StripeConnectService _service;

    public StripeConnectTests()
    {
        _connection.Open();
        _db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        _db.PaymentProcessorConfigurations.Add(Configuration());
        _db.SaveChanges();
        _service = new StripeConnectService(_db, _api, _tenant, TimeProvider.System);
    }

    [Fact]
    public async Task Creates_account_and_Stripe_hosted_onboarding_link()
    {
        var link = await _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return);
        Assert.Equal("https://connect.stripe.test/onboard", link.Url.AbsoluteUri);
        Assert.Equal(1, _api.CreateAccountCalls); Assert.Equal(1, _api.CreateLinkCalls);
        var config = await _db.PaymentProcessorConfigurations.SingleAsync();
        Assert.Equal("acct_test_practice", config.ConnectedMerchantReference);
        Assert.Equal(PaymentProcessorOnboardingStatus.Pending, config.OnboardingStatus);
    }

    [Fact]
    public async Task Already_connected_tenant_reuses_account_and_refreshes_link()
    {
        var config = await _db.PaymentProcessorConfigurations.SingleAsync();
        config.ConnectedMerchantReference = "acct_existing"; await _db.SaveChangesAsync();
        await _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return);
        await _service.RefreshOnboardingLinkAsync("tenant-a", Refresh, Return);
        Assert.Equal(0, _api.CreateAccountCalls); Assert.Equal(2, _api.CreateLinkCalls);
        Assert.All(_api.LinkedAccounts, x => Assert.Equal("acct_existing", x));
    }

    [Fact]
    public async Task Incomplete_and_enabled_statuses_are_persisted()
    {
        await _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return);
        _api.Status = new("acct_test_practice", false, false, true, "past_due");
        var incomplete = await _service.RefreshStatusAsync("tenant-a");
        Assert.Equal(PaymentProcessorOnboardingStatus.Restricted, incomplete.Status);
        _api.Status = new("acct_test_practice", true, true, true, "satisfied");
        var enabled = await _service.RefreshStatusAsync("tenant-a");
        Assert.Equal(PaymentProcessorOnboardingStatus.Enabled, enabled.Status);
        Assert.True(enabled.ChargesEnabled); Assert.True(enabled.PayoutsEnabled);
    }

    [Fact]
    public async Task Stripe_failure_is_sanitized_and_does_not_store_a_partial_account()
    {
        _api.Failure = new StripeConnectException("Stripe Connect request failed with HTTP 503.");
        var error = await Assert.ThrowsAsync<StripeConnectException>(() =>
            _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return));
        Assert.DoesNotContain("sk_", error.Message);
        Assert.Null((await _db.PaymentProcessorConfigurations.SingleAsync()).ConnectedMerchantReference);
    }

    [Fact]
    public async Task Disabled_configuration_fails_closed()
    {
        var config = await _db.PaymentProcessorConfigurations.SingleAsync();
        config.Enabled = false; config.OnboardingStatus = PaymentProcessorOnboardingStatus.Disabled;
        config.ConnectedMerchantReference = "acct_existing"; await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<PaymentProcessorUnavailableException>(() =>
            _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return));
        var status = await _service.RefreshStatusAsync("tenant-a");
        Assert.False(status.Enabled); Assert.Equal(PaymentProcessorOnboardingStatus.Disabled, status.Status);
        Assert.Equal("acct_existing", status.ConnectedAccountId);
        Assert.Equal(0, _api.CreateAccountCalls);
    }

    [Fact]
    public async Task Cross_tenant_access_is_rejected_before_Stripe_is_called()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.CreateOnboardingLinkAsync("tenant-b", "admin@example.test", Refresh, Return));
        Assert.Equal(0, _api.CreateAccountCalls);
    }

    [Fact]
    public void Credential_provider_resolves_only_opaque_reference_and_checks_environment()
    {
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var provider = new ConfigurationStripeCredentialProvider(new ConfigurationBuilder()
            .AddInMemoryCollection(values).Build());
        var config = Configuration();
        Assert.Equal(values["Secrets:StripeTest"], provider.GetSecret(config));
        config.Environment = PaymentProcessorEnvironment.Production;
        var error = Assert.Throws<PaymentProcessorUnavailableException>(() => provider.GetSecret(config));
        Assert.DoesNotContain(values["Secrets:StripeTest"]!, error.Message);
    }

    [Fact]
    public void Credential_provider_accepts_environment_matched_restricted_keys()
    {
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "rk_test_not-a-real-secret" };
        var provider = new ConfigurationStripeCredentialProvider(new ConfigurationBuilder()
            .AddInMemoryCollection(values).Build());
        Assert.Equal(values["Secrets:StripeTest"], provider.GetSecret(Configuration()));
    }

    [Fact]
    public async Task Api_client_uses_Accounts_v2_without_exposing_secret_in_payload()
    {
        var handler = new RecordingHandler("""
            {"id":"acct_test_practice","configuration":{"merchant":{"capabilities":{"card_payments":{"status":"active"},"stripe_balance":{"payouts":{"status":"active"}}}}},"requirements":{"entries":[]}}
            """);
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new StripeApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com") },
            new ConfigurationStripeCredentialProvider(configuration), configuration);
        var account = await client.CreateConnectedAccountAsync(Configuration(), "admin@example.test");
        Assert.True(account.ChargesEnabled); Assert.True(account.PayoutsEnabled);
        Assert.Equal("/v2/core/accounts", handler.RequestUri!.AbsolutePath);
        Assert.Equal("2026-07-29.preview", handler.ApiVersion);
        Assert.Contains("\"fees_collector\":\"stripe\"", handler.Body);
        Assert.DoesNotContain("sk_test_", handler.Body);
    }

    [Theory]
    [InlineData("\"2026-08-19T20:30:00.000Z\"")]
    [InlineData("1787171400")]
    public async Task Api_client_accepts_v2_and_legacy_account_link_expiration(string expiresAt)
    {
        var handler = new RecordingHandler($$"""
            {"url":"https://connect.stripe.test/onboard","expires_at":{{expiresAt}}}
            """);
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new StripeApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com") },
            new ConfigurationStripeCredentialProvider(configuration), configuration);
        var link = await client.CreateAccountLinkAsync(Configuration(), "acct_test_practice", Refresh, Return);
        Assert.Equal(DateTimeKind.Utc, link.ExpiresAt.Kind);
        Assert.Equal(new DateTime(2026, 8, 19, 20, 30, 0, DateTimeKind.Utc), link.ExpiresAt);
    }

    [Fact]
    public async Task Refund_uses_connected_account_idempotency_and_only_opaque_metadata()
    {
        var handler = new RecordingHandler("""
            {"id":"re_test","payment_intent":"pi_test","amount":2500,"currency":"usd","status":"pending","metadata":{"refund_reference":"refund_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """);
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new StripeApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com") },
            new ConfigurationStripeCredentialProvider(configuration), configuration);
        var request = new PaymentRefundRequest("tenant-a", Guid.NewGuid(), new Money(25m),
            "refund_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "requested_by_customer", "admin@example.test");
        var result = await client.CreateRefundAsync(Configuration(), "acct_test_practice", request, "pi_test");
        Assert.Equal("re_test", result.Id);
        Assert.Equal("acct_test_practice", handler.ConnectedAccount);
        Assert.Equal(request.InternalRefundReference, handler.IdempotencyKey);
        Assert.Contains("metadata%5Brefund_reference%5D=refund_", handler.Body);
        Assert.DoesNotContain(request.TenantId, handler.Body);
        Assert.DoesNotContain(request.RequestedBy, handler.Body);
    }

    [Fact]
    public async Task Reconciliation_lists_follow_all_Stripe_pages()
    {
        var handler = new PagingHandler(
            """{"data":[{"id":"pi_first","amount_received":100,"currency":"usd","status":"succeeded"}],"has_more":true}""",
            """{"data":[{"id":"pi_second","amount_received":200,"currency":"usd","status":"succeeded"}],"has_more":false}""",
            """{"data":[{"id":"re_first","payment_intent":"pi_first","amount":50,"currency":"usd","status":"succeeded","metadata":{}}],"has_more":true}""",
            """{"data":[{"id":"re_second","payment_intent":"pi_second","amount":75,"currency":"usd","status":"pending","metadata":{}}],"has_more":false}"""
        );
        var values = new Dictionary<string, string?> { ["Secrets:StripeTest"] = "sk_test_not-a-real-secret" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var client = new StripeApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.stripe.com") },
            new ConfigurationStripeCredentialProvider(configuration), configuration);
        var payments = await client.ListPaymentsAsync(Configuration(), "acct_test_practice", DateTime.UtcNow.AddDays(-1));
        var refunds = await client.ListRefundsAsync(Configuration(), "acct_test_practice", DateTime.UtcNow.AddDays(-1));
        Assert.Equal(["pi_first", "pi_second"], payments.Select(x => x.Id));
        Assert.Equal(["re_first", "re_second"], refunds.Select(x => x.Id));
        Assert.Contains("starting_after=pi_first", handler.Requests[1].Query);
        Assert.Contains("starting_after=re_first", handler.Requests[3].Query);
        Assert.All(handler.ConnectedAccounts, x => Assert.Equal("acct_test_practice", x));
    }

    [Fact]
    public async Task Disable_is_local_and_never_deletes_the_connected_account()
    {
        await _service.CreateOnboardingLinkAsync("tenant-a", "admin@example.test", Refresh, Return);
        await _service.DisableAsync("tenant-a");
        var config = await _db.PaymentProcessorConfigurations.SingleAsync();
        Assert.False(config.Enabled); Assert.Equal("acct_test_practice", config.ConnectedMerchantReference);
        Assert.Equal(1, _api.CreateAccountCalls); Assert.Equal(1, _api.CreateLinkCalls);
    }

    private static readonly Uri Refresh = new("https://portal.example.test/settings/payments/stripe?flow=refresh");
    private static readonly Uri Return = new("https://portal.example.test/settings/payments/stripe?flow=return");
    private static PaymentProcessorConfiguration Configuration() => new()
    {
        Id = Guid.NewGuid(), TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe,
        Enabled = true, Environment = PaymentProcessorEnvironment.Sandbox,
        CredentialReference = "Secrets:StripeTest", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId; public ClaimsPrincipal? User => null;
    }
    private sealed class FakeStripeApiClient : IStripeApiClient
    {
        public int CreateAccountCalls, CreateLinkCalls;
        public List<string> LinkedAccounts { get; } = [];
        public Exception? Failure { get; set; }
        public StripeAccountSnapshot Status { get; set; } = new("acct_test_practice", false, false, false, "pending");
        public Task<StripeAccountSnapshot> CreateConnectedAccountAsync(PaymentProcessorConfiguration configuration,
            string contactEmail, CancellationToken cancellationToken = default)
        { CreateAccountCalls++; if (Failure is not null) throw Failure; return Task.FromResult(Status); }
        public Task<StripeAccountSnapshot> GetConnectedAccountAsync(PaymentProcessorConfiguration configuration,
            string accountId, CancellationToken cancellationToken = default)
        { if (Failure is not null) throw Failure; return Task.FromResult(Status); }
        public Task<StripeOnboardingLink> CreateAccountLinkAsync(PaymentProcessorConfiguration configuration,
            string accountId, Uri refreshUrl, Uri returnUrl, CancellationToken cancellationToken = default)
        {
            CreateLinkCalls++; LinkedAccounts.Add(accountId); if (Failure is not null) throw Failure;
            return Task.FromResult(new StripeOnboardingLink(new Uri("https://connect.stripe.test/onboard"), DateTime.UtcNow.AddMinutes(5)));
        }
        public Task<StripeCheckoutSessionSnapshot> CreateCheckoutSessionAsync(PaymentProcessorConfiguration configuration,
            string connectedAccountId, PaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StripeRefundSnapshot> CreateRefundAsync(PaymentProcessorConfiguration configuration,
            string connectedAccountId, PaymentRefundRequest request, string externalPaymentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StripePaymentSnapshot?> GetPaymentAsync(PaymentProcessorConfiguration configuration,
            string connectedAccountId, string paymentIntentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StripeRefundSnapshot?> GetRefundAsync(PaymentProcessorConfiguration configuration,
            string connectedAccountId, string refundId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<StripePaymentSnapshot>> ListPaymentsAsync(
            PaymentProcessorConfiguration configuration, string connectedAccountId, DateTime createdAfter,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StripePaymentSnapshot>>([]);
        public Task<IReadOnlyList<StripeRefundSnapshot>> ListRefundsAsync(
            PaymentProcessorConfiguration configuration, string connectedAccountId, DateTime createdAfter,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StripeRefundSnapshot>>([]);
    }
    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? ApiVersion { get; private set; }
        public string? ConnectedAccount { get; private set; }
        public string? IdempotencyKey { get; private set; }
        public string Body { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            ApiVersion = request.Headers.GetValues("Stripe-Version").Single();
            ConnectedAccount = request.Headers.TryGetValues("Stripe-Account", out var accounts) ? accounts.Single() : null;
            IdempotencyKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class PagingHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);
        public List<Uri> Requests { get; } = [];
        public List<string?> ConnectedAccounts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            ConnectedAccounts.Add(request.Headers.TryGetValues("Stripe-Account", out var accounts)
                ? accounts.Single() : null);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") });
        }
    }
}

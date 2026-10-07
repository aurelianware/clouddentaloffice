using System.Security.Cryptography;
using System.Text;
using CloudDentalOffice.Contracts.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class StripeWebhookEndpointTests : IDisposable
{
    private const string Secret = "whsec_test_only";
    private readonly StripeWebhookMetrics _metrics = new();
    private readonly RecordingInbox _inbox = new();
    private readonly IConfiguration _configuration = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["StripeWebhooks:EndpointSecret"] = Secret,
            ["StripeWebhooks:Accounts:0:TenantId"] = "tenant-a",
            ["StripeWebhooks:Accounts:0:ConnectedAccountId"] = "acct_practice",
            ["StripeWebhooks:Accounts:0:LiveMode"] = "false",
            ["StripeWebhooks:Accounts:0:Enabled"] = "true"
        }).Build();

    [Fact]
    public async Task Checkout_session_the_app_did_not_create_is_acknowledged_without_persisting()
    {
        var status = await Send(Session(metadata: "{}"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Empty(_inbox.Persisted);
    }

    [Fact]
    public async Task Checkout_session_with_our_reference_is_persisted()
    {
        var status = await Send(Session(metadata: """{"payment_reference":"pay_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""));

        Assert.Equal(StatusCodes.Status202Accepted, status);
        Assert.Single(_inbox.Persisted);
    }

    private async Task<int> Send(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider()
        };
        http.Request.Body = new MemoryStream(bytes);
        http.Request.Headers["Stripe-Signature"] = $"t={timestamp},v1={signature}";
        var result = await StripeWebhookEndpoint.HandleAsync(http, _configuration, _inbox, TimeProvider.System, _metrics);
        return Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;
    }

    private static string Session(string metadata) =>
        "{\"id\":\"evt_1\",\"type\":\"checkout.session.completed\",\"account\":\"acct_practice\"," +
        "\"livemode\":false,\"created\":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "," +
        "\"data\":{\"object\":{\"id\":\"cs_1\",\"payment_intent\":\"pi_1\",\"amount_total\":5000," +
        "\"currency\":\"usd\",\"payment_status\":\"paid\",\"metadata\":" + metadata + "}}}";

    public void Dispose() => _metrics.Dispose();

    private sealed class RecordingInbox : IIntegrationInbox
    {
        public List<IntegrationEvent> Persisted { get; } = [];
        public Task<IntegrationInboxPersistResult> PersistAsync(string tenantId, string channel, string externalEventId,
            string eventType, IntegrationEvent payload, CancellationToken cancellationToken = default)
        {
            Persisted.Add(payload);
            return Task.FromResult(new IntegrationInboxPersistResult(Guid.NewGuid(), true));
        }
        public Task<IntegrationInboxTenantStatus> GetStatusAsync(string tenantId, string? channel = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RequeueAsync(string tenantId, Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

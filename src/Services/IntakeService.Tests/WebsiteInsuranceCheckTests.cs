using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The website's "Check My Insurance" form is relayed to the portal for staff. The
/// site's key picks the practice; nothing is stored here. Synthetic data only.
/// </summary>
public sealed class WebsiteInsuranceCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validation_needs_a_name_a_carrier_and_a_way_to_reach_them()
    {
        var errors = WebsiteInsuranceCheckEndpoint.Validate(new PublicInsuranceCheckRequest { RequestingAppointment = "Maybe" });
        Assert.All(new[] { "name", "carrier", "contact", "requestingAppointment" }, key => Assert.Contains(key, errors.Keys));

        Assert.Empty(WebsiteInsuranceCheckEndpoint.Validate(Valid()));
        Assert.Empty(WebsiteInsuranceCheckEndpoint.Validate(Valid() with { Phone = null }));
        Assert.Contains("email", WebsiteInsuranceCheckEndpoint.Validate(Valid() with { Email = "not-an-email" }).Keys);
        Assert.Contains("phone", WebsiteInsuranceCheckEndpoint.Validate(Valid() with { Phone = "12" }).Keys);
    }

    [Fact]
    public async Task A_valid_check_is_relayed_for_the_sites_practice()
    {
        var publisher = new RecordingPublisher();
        var http = Http("site-key", idempotencyKey: "website-request-0001");

        var result = await Handle(Valid() with { RequestId = "website-request-0001" }, publisher, http);

        Assert.Equal(202, result);
        var evt = Assert.IsType<WebsiteInsuranceCheckEvent>(Assert.Single(publisher.Published));
        Assert.Equal("practice-a", evt.TenantId);
        Assert.Equal("Rowan Vale", evt.Name);
        Assert.Equal("Delta Dental", evt.Carrier);
        Assert.Equal("PPO", evt.Plan);
        Assert.Equal("Yes", evt.RequestingAppointment);
        Assert.Equal("attr-1", evt.AttributionId);
        Assert.Equal(Now.UtcDateTime, evt.SubmittedAtUtc);

        // The same form sent twice relays the same event, which the broker and the portal drop.
        var again = new RecordingPublisher();
        await Handle(Valid() with { RequestId = "website-request-0001" }, again, Http("site-key", idempotencyKey: "website-request-0001"));
        Assert.Equal(evt.EventId, Assert.Single(again.Published).EventId);
    }

    [Fact]
    public async Task Unknown_keys_bad_input_and_a_missing_broker_publish_nothing()
    {
        var publisher = new RecordingPublisher();

        Assert.Equal(401, await Handle(Valid(), publisher, Http("wrong-key")));
        Assert.Equal(400, await Handle(Valid() with { Carrier = null }, publisher, Http("site-key")));
        Assert.Equal(503, await Handle(Valid(), publisher, Http("site-key"), new ServiceBusOptions()));
        Assert.Equal(503, await Handle(Valid(), new RecordingPublisher { Fail = true }, Http("site-key")));
        Assert.Empty(publisher.Published);
    }

    private static PublicInsuranceCheckRequest Valid() => new()
    {
        Name = "Rowan Vale", Phone = "480-555-0100", Email = "rowan@example.test", Carrier = "Delta Dental", Plan = "PPO",
        RequestingAppointment = "Yes", CreatedAt = Now.UtcDateTime,
        Attribution = new Dictionary<string, string> { ["attribution_id"] = "attr-1", ["member_id"] = "dropped" }
    };

    private static async Task<int> Handle(PublicInsuranceCheckRequest request, IEventPublisher publisher, HttpContext http,
        ServiceBusOptions? bus = null)
    {
        var result = await WebsiteInsuranceCheckEndpoint.Handle(request, Config(), publisher,
            bus ?? new ServiceBusOptions { ConnectionString = "Endpoint=sb://test/;SharedAccessKeyName=k;SharedAccessKey=v" },
            new Clock(Now), NullLoggerFactory.Instance, http);
        await result.ExecuteAsync(http);
        return http.Response.StatusCode;
    }

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["PublicBooking:Enabled"] = "true",
        ["PublicBooking:Clients:0:ApiKey"] = "site-key",
        ["PublicBooking:Clients:0:TenantId"] = "practice-a"
    }).Build();

    private static DefaultHttpContext Http(string key, string? idempotencyKey = null)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        http.Request.Headers.Authorization = $"Bearer {key}";
        if (idempotencyKey is not null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;
        return http;
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool Fail { get; init; }
        public List<IntegrationEvent> Published { get; } = [];

        public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Broker down.");
            Published.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

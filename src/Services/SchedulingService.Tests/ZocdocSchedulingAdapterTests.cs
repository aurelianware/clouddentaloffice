using System.Net;
using System.Text;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocSchedulingAdapterTests
{
    [Fact]
    public void EnvironmentsUseOfficialSandboxAndProductionEndpoints()
    {
        var sandbox = ZocdocEndpoints.For(ZocdocEnvironment.Sandbox);
        var production = ZocdocEndpoints.For(ZocdocEnvironment.Production);

        Assert.Equal("https://api-developer-sandbox.zocdoc.com/", sandbox.ApiBaseUri.ToString());
        Assert.Equal("https://auth-api-developer-sandbox.zocdoc.com/oauth/token", sandbox.TokenUri.ToString());
        Assert.Equal("https://api-developer-sandbox.zocdoc.com/", sandbox.Audience);
        Assert.Equal("https://api-developer.zocdoc.com/", production.ApiBaseUri.ToString());
        Assert.Equal("https://auth.zocdoc.com/oauth/token", production.TokenUri.ToString());
        Assert.Equal("https://api-developer.zocdoc.com/", production.Audience);
    }

    [Fact]
    public async Task TokenAcquisitionUsesDocumentedJsonClientCredentialsRequest()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"tenant-token","expires_in":3600,"token_type":"Bearer"}"""));
        var provider = TokenProvider(handler);

        var token = await provider.GetAccessTokenAsync("practice-a", Configuration());

        Assert.Equal("tenant-token", token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://auth-api-developer-sandbox.zocdoc.com/oauth/token", request.Uri);
        Assert.Contains("\"grant_type\":\"client_credentials\"", request.Body);
        Assert.Contains("\"client_id\":\"client-id\"", request.Body);
        Assert.Contains("\"audience\":\"https://api-developer-sandbox.zocdoc.com/\"", request.Body);
    }

    [Fact]
    public async Task TokenIsCachedForSameTenantAndEnvironment()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            """{"access_token":"cached-token","expires_in":3600,"token_type":"Bearer"}"""));
        var provider = TokenProvider(handler);

        Assert.Equal("cached-token", await provider.GetAccessTokenAsync("practice-a", Configuration()));
        Assert.Equal("cached-token", await provider.GetAccessTokenAsync("practice-a", Configuration()));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TokenCacheIsIsolatedByTenant()
    {
        var sequence = 0;
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"access_token":"token-{{++sequence}}","expires_in":3600,"token_type":"Bearer"}"""));
        var provider = TokenProvider(handler);

        var tenantA = await provider.GetAccessTokenAsync("practice-a", Configuration());
        var tenantB = await provider.GetAccessTokenAsync("practice-b", Configuration());

        Assert.Equal("token-1", tenantA);
        Assert.Equal("token-2", tenantB);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidCredentialsAreTranslatedToAuthenticationFailure()
    {
        var provider = TokenProvider(new RecordingHandler(_ =>
            Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""")));

        var exception = await Assert.ThrowsAsync<ZocdocIntegrationException>(() =>
            provider.GetAccessTokenAsync("practice-a", Configuration()));

        Assert.Equal(ZocdocFailureKind.Authentication, exception.Kind);
        Assert.DoesNotContain("client-secret", exception.Message);
    }

    [Fact]
    public async Task TokenThrottlingCarriesRetryAfter()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(12));
            return response;
        });
        var provider = TokenProvider(handler);

        var exception = await Assert.ThrowsAsync<ZocdocIntegrationException>(() =>
            provider.GetAccessTokenAsync("practice-a", Configuration()));

        Assert.Equal(ZocdocFailureKind.Throttling, exception.Kind);
        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
    }

    [Fact]
    public async Task ExpiredTokensAreEvictedFromCache()
    {
        var clock = new MutableClock();
        var sequence = 0;
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            $$"""{"access_token":"token-{{++sequence}}","expires_in":3600,"token_type":"Bearer"}"""));
        var provider = TokenProvider(handler, clock);

        await provider.GetAccessTokenAsync("practice-a", Configuration());
        clock.UtcNow = clock.UtcNow.AddHours(2);
        await provider.GetAccessTokenAsync("practice-b", Configuration());

        Assert.Equal(1, provider.CachedTokenCount);
    }

    [Fact]
    public async Task ApiThrottlingIsClassifiedAndCarriesRetryAfter()
    {
        var handler = new RecordingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, "{}");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return response;
        });
        var client = ApiClient(handler);

        var exception = await Assert.ThrowsAsync<ZocdocIntegrationException>(() =>
            client.ValidateConnectionAsync("practice-a", Configuration()));

        Assert.Equal(ZocdocFailureKind.Throttling, exception.Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), exception.RetryAfter);
    }

    [Fact]
    public async Task TimeslotPublicationUsesOfficialProviderDateReplacementEndpoint()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = ApiClient(handler);

        await client.ReplaceTimeslotsAsync("practice-a", Configuration(), "pr_123",
            new DateOnly(2026, 1, 5), [new ZocdocTimeslotRequest
            {
                ProviderId = "pr_123", LocationId = "lo_456", StartTime = "2026-01-05T09:00:00",
                TimeZone = "America/Phoenix", AllowedVisitReasonIds = ["pc_exam"], PatientType = "new"
            }]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://api-developer-sandbox.zocdoc.com/v1/providers/pr_123/calendar/timeslots?date=2026-01-05", request.Uri);
        Assert.Contains("\"timeslots\"", request.Body);
        Assert.Contains("\"allowed_visit_reason_ids\":[\"pc_exam\"]", request.Body);
    }

    [Fact]
    public async Task AppointmentLifecycleUsesDocumentedActionEndpointsAndPayloads()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = ApiClient(handler);
        var start = new DateTimeOffset(2026, 9, 9, 9, 0, 0, TimeSpan.FromHours(-7));

        await client.CancelAppointmentAsync("practice-a", Configuration(), "za-1");
        await client.RescheduleAppointmentAsync("practice-a", Configuration(), "za-1", start);
        await client.UpdateAppointmentStatusAsync("practice-a", Configuration(), "za-1", "arrived");
        await client.UpdateAppointmentStatusAsync("practice-a", Configuration(), "za-1", "no_show");

        Assert.Collection(handler.Requests,
            request => { Assert.Equal(HttpMethod.Post, request.Method); Assert.EndsWith("/v1/appointments/cancel", request.Uri); Assert.Contains("other_provider_reason", request.Body); },
            request => { Assert.Equal(HttpMethod.Post, request.Method); Assert.EndsWith("/v1/appointments/reschedule", request.Uri); Assert.Contains("2026-09-09T09:00:00.0000000-07:00", request.Body); },
            request => { Assert.Equal(HttpMethod.Put, request.Method); Assert.EndsWith("/v1/appointments/update-status", request.Uri); Assert.Contains("\"appointment_status\":\"arrived\"", request.Body); },
            request => { Assert.Equal(HttpMethod.Put, request.Method); Assert.EndsWith("/v1/appointments/update-status", request.Uri); Assert.Contains("\"appointment_status\":\"no_show\"", request.Body); });
    }

    [Fact]
    public async Task TransientApiFailureIsClassifiedWithoutLeakingBody()
    {
        var client = ApiClient(new RecordingHandler(_ =>
            Json(HttpStatusCode.ServiceUnavailable, """{"message":"upstream detail"}""")));

        var exception = await Assert.ThrowsAsync<ZocdocIntegrationException>(() =>
            client.ValidateConnectionAsync("practice-a", Configuration()));

        Assert.Equal(ZocdocFailureKind.TemporaryRemoteFailure, exception.Kind);
        Assert.DoesNotContain("upstream detail", exception.Message);
    }

    [Fact]
    public async Task AdapterMapsZocdocDtosToCanonicalExternalEntities()
    {
        var api = new FakeApiClient
        {
            SchedulableEntities =
            [
                new ZocdocSchedulableEntityDto
                {
                    ProviderLocationId = "pr_123|lo_456", ProviderId = "pr_123",
                    FirstName = "Alex", LastName = "Rivera", Address1 = "1 Main St",
                    Address2 = "Suite 200", City = "Mesa", State = "AZ", Zip = "85201"
                }
            ],
            VisitReasons = [new ZocdocVisitReasonDto { VisitReasonId = "pc_exam", Name = "Dental exam" }]
        };
        var adapter = new ZocdocSchedulingAdapter(
            new FakeConfigurationStore(Configuration()), api);

        var entities = await adapter.GetExternalEntitiesAsync("practice-a");

        Assert.Contains(entities, x => x.EntityType == SchedulingResourceType.Provider && x.ExternalId == "pr_123");
        Assert.Contains(entities, x => x.EntityType == SchedulingResourceType.Location && x.ExternalId == "lo_456");
        Assert.Contains(entities, x => x.EntityType == SchedulingResourceType.Location &&
            x.DisplayName.Contains("Suite 200"));
        Assert.Contains(entities, x => x.EntityType == SchedulingResourceType.VisitReason && x.ExternalId == "pc_exam");
    }

    [Fact]
    public async Task DisabledZocdocConfigurationPreventsAdapterCalls()
    {
        var configuration = Configuration();
        configuration.Enabled = false;
        var api = new FakeApiClient();
        var adapter = new ZocdocSchedulingAdapter(new FakeConfigurationStore(configuration), api);

        await Assert.ThrowsAsync<SchedulingIntegrationDisabledException>(() =>
            adapter.ValidateConnectionAsync("practice-a"));
        Assert.Equal(0, api.CallCount);
    }

    [Fact]
    public async Task ZocdocAdapterIsRegisteredWithChannelResolver()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddDbContext<SchedulingDbContext>(options => options.UseSqlite(connection));
        services.AddSchedulingIntegrations();
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.SchedulingIntegrationConfigurations.Add(Configuration());
            await db.SaveChangesAsync();
        }

        await using var resolverScope = provider.CreateAsyncScope();
        var resolved = await resolverScope.ServiceProvider.GetRequiredService<ISchedulingChannelAdapterResolver>()
            .ResolveAsync("practice-a", SchedulingChannel.Zocdoc);

        Assert.IsType<ZocdocSchedulingAdapter>(resolved);
    }

    private static ZocdocAccessTokenProvider TokenProvider(
        RecordingHandler handler, ISchedulingClock? clock = null) => new(
        new FixedHttpClientFactory(new HttpClient(handler)),
        new FixedCredentialProvider(),
        clock ?? new FixedClock(),
        NullLogger<ZocdocAccessTokenProvider>.Instance);

    private static ZocdocApiClient ApiClient(RecordingHandler handler) => new(
        new HttpClient(handler), new FixedTokenProvider(), NullLogger<ZocdocApiClient>.Instance);

    private static SchedulingIntegrationConfiguration Configuration() => new()
    {
        TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true,
        Environment = "Sandbox", CredentialReference = "practice-a-zocdoc"
    };

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.ToString(),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Uri, string Body);
    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class FixedCredentialProvider : IZocdocCredentialProvider
    {
        public Task<ZocdocCredentials> GetAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ZocdocCredentials("client-id", "client-secret", null));
    }
    private sealed class FixedTokenProvider : IZocdocAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.FromResult("api-token");
    }
    private sealed class FixedClock : ISchedulingClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class MutableClock : ISchedulingClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class FakeConfigurationStore(SchedulingIntegrationConfiguration configuration)
        : ISchedulingIntegrationConfigurationStore
    {
        public Task<SchedulingIntegrationConfiguration?> GetAsync(string tenantId, SchedulingChannel channel,
            CancellationToken cancellationToken = default) => Task.FromResult<SchedulingIntegrationConfiguration?>(
                configuration.TenantId == tenantId && configuration.Channel == channel ? configuration : null);
    }
    private sealed class FakeApiClient : IZocdocApiClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<ZocdocSchedulableEntityDto> SchedulableEntities { get; init; } = [];
        public IReadOnlyList<ZocdocVisitReasonDto> VisitReasons { get; init; } = [];
        public Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            CancellationToken cancellationToken = default) { CallCount++; return Task.CompletedTask; }
        public Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(SchedulableEntities); }
        public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default)
        { CallCount++; return Task.FromResult(VisitReasons); }
        public Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots,
            CancellationToken cancellationToken = default)
        { CallCount++; return Task.CompletedTask; }
        public Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, string appointmentId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfirmAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RescheduleAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, DateTimeOffset startTime, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAppointmentStatusAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, string status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

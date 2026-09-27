using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Stedi;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Per-practice Stedi credentials: resolution, isolation between practices,
/// caching and rotation, the per-request handler, and tenant routing.
/// Keys are synthetic; tests assert they never reach logs.
/// </summary>
public sealed class StediCredentialTests : IDisposable
{
    private const string TenantA = "practice-a";
    private const string TenantB = "practice-b";
    private const string KeyA = "synthetic-key-for-practice-a";
    private const string KeyB = "synthetic-key-for-practice-b";
    private const string SharedKey = "synthetic-shared-key";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ServiceProvider _services;
    private readonly FakeSecretReader _secrets = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly StediOptions _options = new() { KeyVaultUri = "https://cdo-kv-test.vault.azure.net/" };

    public StediCredentialTests()
    {
        _connection.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        // Ambient tenant is the development default, as in a background job:
        // resolution must use the explicit tenant, never this one.
        services.AddScoped<ITenantProvider>(_ => new FixedTenantProvider(TenantConstants.DefaultTenantId));
        services.AddDbContext<CloudDentalDbContext>(o => o.UseSqlite(_connection));
        services.AddMemoryCache();
        services.AddSingleton<IOptionsMonitor<StediOptions>>(new StaticOptionsMonitor(_options));
        services.AddSingleton<IOptions<StediOptions>>(Options.Create(_options));
        services.AddSingleton<IStediSecretReader>(_secrets);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IStediCredentialProvider, StediCredentialProvider>();
        services.AddSingleton<IClearinghouseConnectionStore, ClearinghouseConnectionStore>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>().Database.EnsureCreated();

        _secrets.Values["stedi-apikey-" + TenantA] = KeyA;
        _secrets.Values["stedi-apikey-" + TenantB] = KeyB;
        _secrets.Values["stedi-apikey-shared"] = SharedKey;
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private IStediCredentialProvider Credentials => _services.GetRequiredService<IStediCredentialProvider>();

    // ── Credential provider ─────────────────────────────────────────────────

    [Fact]
    public async Task Integrated_practice_gets_its_own_key()
    {
        await Connect(TenantA);

        var credential = await Credentials.GetAsync(TenantA);

        Assert.Equal(KeyA, credential.ApiKey);
        Assert.Equal(TenantA, credential.TenantId);
        Assert.Equal(ClearinghouseConnectionMode.Integrated, credential.Mode);
        Assert.DoesNotContain(KeyA, credential.ToString());
    }

    [Fact]
    public async Task Practice_A_can_never_resolve_practice_B_key()
    {
        await Connect(TenantB);
        // A's row points at B's secret (a mistake or a tampered row).
        await Connect(TenantA, keyReference: "stedi-apikey-" + TenantB);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.InvalidKeyReference, error.Failure);
        Assert.DoesNotContain("stedi-apikey-" + TenantB, _secrets.Reads);
        Assert.Equal(KeyB, (await Credentials.GetAsync(TenantB)).ApiKey);
    }

    [Fact]
    public async Task Each_practice_resolves_only_its_own_key_even_when_interleaved()
    {
        await Connect(TenantA);
        await Connect(TenantB);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Credentials.GetAsync(i % 2 == 0 ? TenantA : TenantB)));

        Assert.All(results, c => Assert.Equal(c.TenantId == TenantA ? KeyA : KeyB, c.ApiKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_tenant_throws(string? tenant)
    {
        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(tenant!));

        Assert.Equal(StediCredentialFailure.MissingTenant, error.Failure);
        Assert.Empty(_secrets.Reads);
    }

    [Fact]
    public async Task Unconnected_practice_does_not_fall_back_to_another_key()
    {
        await Connect(TenantB);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.NotConnected, error.Failure);
        Assert.Empty(_secrets.Reads);
    }

    [Theory]
    [InlineData(ClearinghouseConnectionStatus.Pending)]
    [InlineData(ClearinghouseConnectionStatus.Suspended)]
    public async Task Inactive_connection_is_refused(ClearinghouseConnectionStatus status)
    {
        await Connect(TenantA, status: status);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.NotActive, error.Failure);
    }

    [Fact]
    public async Task Shared_mode_is_refused_when_the_flag_is_off()
    {
        _options.SharedAccount = new SharedStediAccountOptions { Enabled = false, SecretName = "stedi-apikey-shared" };
        await Connect(TenantA, mode: ClearinghouseConnectionMode.Shared, keyReference: null);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.SharedAccountDisabled, error.Failure);
        Assert.Empty(_secrets.Reads);
    }

    [Fact]
    public async Task Shared_mode_uses_the_shared_key_and_logs_every_use()
    {
        _options.SharedAccount = new SharedStediAccountOptions { Enabled = true, SecretName = "stedi-apikey-shared" };
        await Connect(TenantA, mode: ClearinghouseConnectionMode.Shared, keyReference: null);

        var first = await Credentials.GetAsync(TenantA);
        await Credentials.GetAsync(TenantA);

        Assert.Equal(SharedKey, first.ApiKey);
        Assert.Equal(ClearinghouseConnectionMode.Shared, first.Mode);
        Assert.Equal(2, _logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("shared Aurelianware Stedi account")));
    }

    [Fact]
    public async Task Integrated_practice_is_not_given_the_shared_key_when_the_flag_is_on()
    {
        _options.SharedAccount = new SharedStediAccountOptions { Enabled = true, SecretName = "stedi-apikey-shared" };
        await Connect(TenantA);
        _secrets.Values.TryRemove("stedi-apikey-" + TenantA, out _);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.SecretNotFound, error.Failure);
        Assert.DoesNotContain("stedi-apikey-shared", _secrets.Reads);
    }

    [Fact]
    public async Task Missing_key_vault_configuration_fails_closed()
    {
        _options.KeyVaultUri = null;
        await Connect(TenantA);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.KeyVaultNotConfigured, error.Failure);
    }

    [Fact]
    public async Task Key_vault_errors_fail_closed_without_logging_the_exception_text()
    {
        await Connect(TenantA);
        _secrets.Failure = new InvalidOperationException("vault said " + KeyA);

        var error = await Assert.ThrowsAsync<StediCredentialUnavailableException>(() => Credentials.GetAsync(TenantA));

        Assert.Equal(StediCredentialFailure.KeyVaultUnavailable, error.Failure);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(KeyA));
    }

    [Fact]
    public async Task Key_is_cached_until_invalidated()
    {
        await Connect(TenantA);

        await Credentials.GetAsync(TenantA);
        await Credentials.GetAsync(TenantA);
        Assert.Equal(1, _secrets.Reads.Count(r => r == "stedi-apikey-" + TenantA));

        _secrets.Values["stedi-apikey-" + TenantA] = "rotated-key";
        Credentials.Invalidate(TenantA);

        Assert.Equal("rotated-key", (await Credentials.GetAsync(TenantA)).ApiKey);
    }

    [Fact]
    public async Task Recording_a_rotation_refreshes_the_cached_key()
    {
        await Connect(TenantA);
        await Credentials.GetAsync(TenantA);
        _secrets.Values["stedi-apikey-" + TenantA] = "rotated-key";

        await _services.GetRequiredService<IClearinghouseConnectionStore>().MarkRotatedAsync(TenantA, default);

        Assert.Equal("rotated-key", (await Credentials.GetAsync(TenantA)).ApiKey);
    }

    [Fact]
    public async Task Rotation_recorded_elsewhere_is_seen_without_local_invalidation()
    {
        await Connect(TenantA);
        await Credentials.GetAsync(TenantA);
        _secrets.Values["stedi-apikey-" + TenantA] = "rotated-key";

        // Another replica records the rotation directly in the database.
        using (var scope = _services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
            var row = await db.TenantClearinghouseConnections.IgnoreQueryFilters().SingleAsync(x => x.TenantId == TenantA);
            row.RotatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Equal("rotated-key", (await Credentials.GetAsync(TenantA)).ApiKey);
    }

    // ── Per-request handler ─────────────────────────────────────────────────

    [Fact]
    public async Task Handler_attaches_the_stamped_practice_key_and_replaces_any_existing_header()
    {
        await Connect(TenantA);
        var inner = new RecordingHandler();
        using var client = new HttpClient(new StediCredentialHandler(Credentials) { InnerHandler = inner });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://stedi.test/eligibility");
        request.Headers.TryAddWithoutValidation("Authorization", "stale-or-injected");
        request.Options.Set(StediRequest.Tenant, TenantA);
        await client.SendAsync(request);

        Assert.Equal([KeyA], inner.Authorizations.Single());
    }

    [Fact]
    public async Task Handler_refuses_a_request_without_a_tenant()
    {
        await Connect(TenantA);
        var inner = new RecordingHandler();
        using var client = new HttpClient(new StediCredentialHandler(Credentials) { InnerHandler = inner });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://stedi.test/eligibility")));

        Assert.Empty(inner.Authorizations);
        Assert.Empty(_secrets.Reads);
    }

    [Fact]
    public async Task Handler_never_sends_practice_B_key_on_practice_A_request()
    {
        await Connect(TenantA);
        await Connect(TenantB);
        var inner = new RecordingHandler();
        using var client = new HttpClient(new StediCredentialHandler(Credentials) { InnerHandler = inner });

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            var tenant = i % 2 == 0 ? TenantA : TenantB;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://stedi.test/eligibility?t=" + tenant);
            request.Options.Set(StediRequest.Tenant, tenant);
            await client.SendAsync(request);
        }));

        Assert.All(inner.Sent, s => Assert.Equal(s.Tenant == TenantA ? KeyA : KeyB, s.Authorization));
    }

    // ── Direct Stedi eligibility client ─────────────────────────────────────

    [Fact]
    public async Task Stedi_client_sends_the_eligibility_request_with_the_practice_key()
    {
        await Connect(TenantA);
        var inner = new RecordingHandler(ActiveResponse);
        var client = StediClient(inner);

        var result = await client.CheckAsync(DependentRequest(TenantA));

        var (authorization, body) = inner.Bodies.Single();
        Assert.Equal(KeyA, authorization);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("87726", root.GetProperty("tradingPartnerServiceId").GetString());
        Assert.Equal("1999999984", root.GetProperty("provider").GetProperty("npi").GetString());
        Assert.Equal("19610417", root.GetProperty("subscriber").GetProperty("dateOfBirth").GetString());
        Assert.Equal("20110903", root.GetProperty("dependents")[0].GetProperty("dateOfBirth").GetString());
        Assert.Equal("35", root.GetProperty("encounter").GetProperty("serviceTypeCodes")[0].GetString());

        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
        Assert.Equal("Clearinghouse", result.Source);
        Assert.Equal("Dental PPO", result.PlanName);
        Assert.Equal(50m, result.Deductible);
        Assert.Equal(1250m, result.AnnualMaximumRemaining);
    }

    [Fact]
    public async Task Payer_rejection_comes_back_as_unknown_coverage_with_the_reason()
    {
        await Connect(TenantA);
        var client = StediClient(new RecordingHandler("""
            { "errors": [ { "code": "72", "description": "Invalid/Missing Subscriber/Insured ID" } ] }
            """));

        var result = await client.CheckAsync(DependentRequest(TenantA));

        Assert.Equal(CoverageStatus.Unknown, result.CoverageStatus);
        Assert.Contains("Invalid/Missing Subscriber/Insured ID", result.Messages);
    }

    [Fact]
    public async Task Unconnected_practice_gets_a_staff_message_and_no_request_is_sent()
    {
        var inner = new RecordingHandler(ActiveResponse);
        var client = StediClient(inner);

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(DependentRequest(TenantA)));

        Assert.Contains("isn't connected yet", error.Message);
        Assert.Empty(inner.Bodies);
    }

    [Fact]
    public async Task Stedi_credential_rejection_is_reported_without_the_key()
    {
        await Connect(TenantA);
        var client = StediClient(new RecordingHandler("{}", HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(DependentRequest(TenantA)));

        Assert.Contains("did not accept our credentials", error.Message);
        Assert.DoesNotContain(_logs.Entries, e => e.Message.Contains(KeyA));
    }

    // ── Per-practice routing ────────────────────────────────────────────────

    [Theory]
    [InlineData(EligibilityGatewayKind.Stedi, "Clearinghouse")]
    [InlineData(EligibilityGatewayKind.CloudHealthOffice, "CloudHealthOffice")]
    public async Task Adapter_routes_by_the_practice_connection(EligibilityGatewayKind route, string expectedSource)
    {
        await Connect(TenantA, gateway: route);
        var adapter = Adapter();

        var result = await adapter.CheckEligibilityAsync(DependentRequest(TenantA));

        Assert.Equal(expectedSource, result.Source);
    }

    [Fact]
    public async Task Adapter_refuses_a_practice_without_an_active_connection()
    {
        await Connect(TenantA, status: ClearinghouseConnectionStatus.Suspended);

        await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() =>
            Adapter().CheckEligibilityAsync(DependentRequest(TenantA)));
    }

    [Fact]
    public async Task Adapter_logs_each_use_of_the_CloudHealthOffice_account()
    {
        await Connect(TenantA, gateway: EligibilityGatewayKind.CloudHealthOffice);

        await Adapter().CheckEligibilityAsync(DependentRequest(TenantA));

        Assert.Contains(_logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("CloudHealthOffice's clearinghouse account"));
    }

    [Fact]
    public async Task Nothing_logged_contains_a_key_or_patient_identity()
    {
        _options.SharedAccount = new SharedStediAccountOptions { Enabled = true, SecretName = "stedi-apikey-shared" };
        await Connect(TenantA, gateway: EligibilityGatewayKind.Stedi);
        await Connect(TenantB, mode: ClearinghouseConnectionMode.Shared, keyReference: null, gateway: EligibilityGatewayKind.Stedi);
        var client = StediClient(new RecordingHandler(ActiveResponse));

        await client.CheckAsync(DependentRequest(TenantA));
        await client.CheckAsync(DependentRequest(TenantB));

        var logs = string.Join('\n', _logs.Entries.Select(e => e.Message));
        foreach (var secret in new[] { KeyA, KeyB, SharedKey, "MBRZX9913377", "Quillon", "Vantasserie", "Orlaith" })
            Assert.DoesNotContain(secret, logs);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private const string ActiveResponse = """
        {
          "planInformation": { "groupDescription": "Dental PPO", "groupNumber": "GRP7" },
          "planStatus": [ { "statusCode": "1", "status": "Active Coverage" } ],
          "planDateInformation": { "planBegin": "20260101" },
          "benefitsInformation": [
            { "code": "1", "serviceTypeCodes": ["35"], "name": "Dental Care" },
            { "code": "C", "serviceTypeCodes": ["35"], "coverageLevelCode": "IND", "timeQualifier": "Calendar Year", "benefitAmount": "50", "inPlanNetworkIndicatorCode": "Y" },
            { "code": "F", "serviceTypeCodes": ["35"], "coverageLevelCode": "IND", "timeQualifier": "Remaining", "benefitAmount": "1250", "inPlanNetworkIndicatorCode": "Y" }
          ]
        }
        """;

    private async Task Connect(
        string tenant,
        ClearinghouseConnectionStatus status = ClearinghouseConnectionStatus.Active,
        ClearinghouseConnectionMode mode = ClearinghouseConnectionMode.Integrated,
        string? keyReference = "",
        EligibilityGatewayKind gateway = EligibilityGatewayKind.Stedi)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
        db.TenantClearinghouseConnections.Add(new TenantClearinghouseConnection
        {
            TenantId = tenant,
            Mode = mode,
            Status = status,
            KeyReference = keyReference == "" ? "stedi-apikey-" + tenant : keyReference,
            EligibilityGateway = gateway,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private StediEligibilityClient StediClient(HttpMessageHandler inner) =>
        new(new HttpClient(new StediCredentialHandler(Credentials) { InnerHandler = inner }),
            Options.Create(_options), _services.GetRequiredService<ILogger<StediEligibilityClient>>());

    private ClearinghouseEligibilityAdapter Adapter()
    {
        var stedi = new StediEligibilityGateway(StediClient(new RecordingHandler(ActiveResponse)));
        var cho = new CloudHealthOfficeEligibilityGateway(new FakeChoClient());
        return new ClearinghouseEligibilityAdapter(
            _services.GetRequiredService<IClearinghouseConnectionStore>(), [cho, stedi],
            _services.GetRequiredService<ILogger<ClearinghouseEligibilityAdapter>>());
    }

    private static NormalizedEligibilityRequest DependentRequest(string tenant) => new()
    {
        TenantId = tenant,
        PayerId = "87726",
        MemberId = "MBRZX9913377",
        SubscriberFirstName = "Quillon",
        SubscriberLastName = "Vantasserie",
        SubscriberDateOfBirth = new DateOnly(1961, 4, 17),
        Dependent = new EligibilityDependent("Orlaith", "Vantasserie", new DateOnly(2011, 9, 3), EligibilityRelationship.Child),
        ProviderNpi = "1999999984",
        ServiceDate = DateOnly.FromDateTime(DateTime.Today),
        CorrelationId = "check-1"
    };

    private sealed class FakeSecretReader : IStediSecretReader
    {
        public ConcurrentDictionary<string, string> Values { get; } = new();
        public ConcurrentQueue<string> Reads { get; } = new();
        public Exception? Failure { get; set; }

        public Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
        {
            Reads.Enqueue(name);
            if (Failure is not null) throw Failure;
            return Task.FromResult(Values.TryGetValue(name, out var value) ? value : null);
        }
    }

    private sealed class RecordingHandler(string body = "{}", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public ConcurrentQueue<string[]> Authorizations { get; } = new();
        public ConcurrentQueue<(string Tenant, string Authorization)> Sent { get; } = new();
        public ConcurrentQueue<(string Authorization, string Body)> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var authorization = request.Headers.TryGetValues("Authorization", out var values) ? values.ToArray() : [];
            Authorizations.Enqueue(authorization);
            var tenant = request.RequestUri?.Query.Replace("?t=", "") ?? "";
            Sent.Enqueue((tenant, authorization.SingleOrDefault() ?? ""));
            if (request.Content is not null)
                Bodies.Enqueue((authorization.SingleOrDefault() ?? "", await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeChoClient : ICloudHealthOfficeEligibilityClient
    {
        public bool IsConfigured => true;

        public Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EligibilityResult
            {
                CorrelationId = "cho", CoverageStatus = CoverageStatus.Active, Source = "CloudHealthOffice", VerifiedAt = DateTimeOffset.UtcNow
            });
    }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }

    private sealed class StaticOptionsMonitor(StediOptions value) : IOptionsMonitor<StediOptions>
    {
        public StediOptions CurrentValue => value;
        public StediOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StediOptions, string?> listener) => null;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(Entries);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
        }
    }
}

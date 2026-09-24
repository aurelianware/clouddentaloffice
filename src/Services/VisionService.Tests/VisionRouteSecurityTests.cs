using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Contracts.Vision;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using VisionService.Auth;
using VisionService.Domain;

public sealed class VisionRouteSecurityTests : IClassFixture<VisionSecurityFactory>
{
    private static readonly Guid AnyId = Guid.NewGuid();
    private readonly VisionSecurityFactory _factory;

    public VisionRouteSecurityTests(VisionSecurityFactory factory) => _factory = factory;

    public static TheoryData<string, string> StaffRoutes() => new()
    {
        { "POST", "/api/vision/devices" },
        { "GET", "/api/vision/devices" },
        { "GET", $"/api/vision/devices/{AnyId}" },
        { "PUT", $"/api/vision/devices/{AnyId}/status" },
        { "GET", "/api/vision/events" },
        { "GET", $"/api/vision/events/{AnyId}" },
        { "POST", "/api/vision/insurance/scan" },
        { "GET", "/api/vision/insurance/scans" },
        { "POST", "/api/vision/consent/start" },
        { "POST", $"/api/vision/consent/{AnyId}/complete" },
        { "GET", "/api/vision/consent" },
        { "GET", "/api/vision/cabinet/access-log" },
        { "POST", "/api/vision/clinical-notes/generate" },
        { "POST", $"/api/vision/clinical-notes/{AnyId}/approve" },
        { "GET", "/api/vision/clinical-notes" }
    };

    public static TheoryData<string, string> DeviceRoutes() => new()
    {
        { "POST", $"/api/vision/devices/{AnyId}/heartbeat" },
        { "POST", "/api/vision/detections" },
        { "POST", "/api/vision/cabinet/access" }
    };

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    [MemberData(nameof(DeviceRoutes))]
    public async Task Routes_reject_requests_without_credentials(string method, string route)
    {
        var response = await _factory.CreateClient().SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    public async Task Staff_routes_reject_device_keys(string method, string route)
    {
        var response = await _factory.DeviceClient("tenant-a").SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(StaffRoutes))]
    public async Task Staff_routes_forbid_patient_portal_tokens(string method, string route)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", VisionSecurityFactory.Token("tenant-a", role: "Patient"));

        var response = await client.SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(DeviceRoutes))]
    public async Task Device_routes_reject_staff_tokens_and_wrong_keys(string method, string route)
    {
        var staff = await _factory.StaffClient("tenant-a").SendAsync(Request(method, route));
        var wrongKey = await _factory.DeviceClient("tenant-a", "wrong-device-key-with-at-least-32-chars!").SendAsync(Request(method, route));
        var otherTenantKey = await _factory.DeviceClient("tenant-a", VisionSecurityFactory.DeviceKeyB).SendAsync(Request(method, route));

        Assert.Equal(HttpStatusCode.Unauthorized, staff.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherTenantKey.StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_staff_reads_and_writes_return_404_and_leave_rows_unchanged()
    {
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.StaffClient("tenant-a");

        var device = await client.GetAsync($"/api/vision/devices/{b.DeviceId}");
        var status = await client.PutAsync($"/api/vision/devices/{b.DeviceId}/status?status={DeviceStatus.Offline}", null);
        var visionEvent = await client.GetAsync($"/api/vision/events/{b.EventId}");
        var consent = await client.PostAsync($"/api/vision/consent/{b.ConsentId}/complete", null);
        var note = await client.PostAsync($"/api/vision/clinical-notes/{b.NoteId}/approve", null);
        var scanOnOtherDevice = await client.PostAsJsonAsync("/api/vision/insurance/scan",
            new ScanInsuranceCardRequest { DeviceId = b.DeviceId, ImageBase64 = Convert.ToBase64String([1, 2, 3]) });

        Assert.Equal(HttpStatusCode.NotFound, device.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, visionEvent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, consent.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, note.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, scanOnOtherDevice.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VisionDbContext>();
        Assert.Equal(DeviceStatus.Online, (await db.Devices.SingleAsync(d => d.Id == b.DeviceId)).Status);
        Assert.Equal(ConsentStatus.InProgress, (await db.ConsentRecordings.SingleAsync(c => c.Id == b.ConsentId)).Status);
        Assert.False((await db.ClinicalNoteDrafts.SingleAsync(n => n.Id == b.NoteId)).ApprovedByProvider);
    }

    [Fact]
    public async Task Staff_lists_return_only_the_token_tenant()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var b = await _factory.SeedTenantAsync("tenant-b");
        var client = _factory.StaffClient("tenant-a");

        var devices = await client.GetFromJsonAsync<List<VisionDeviceDto>>("/api/vision/devices");
        var events = await client.GetFromJsonAsync<List<VisionEventDto>>("/api/vision/events?limit=500");
        var consents = await client.GetFromJsonAsync<List<ConsentRecordingDto>>("/api/vision/consent?limit=500");
        var notes = await client.GetFromJsonAsync<List<ClinicalNoteDraftDto>>("/api/vision/clinical-notes?limit=500");

        Assert.Contains(devices!, d => d.Id == a.DeviceId);
        Assert.All(devices!, d => Assert.Equal("tenant-a", d.TenantId));
        Assert.Contains(events!, e => e.Id == a.EventId);
        Assert.All(events!, e => Assert.Equal("tenant-a", e.TenantId));
        Assert.DoesNotContain(consents!, c => c.Id == b.ConsentId);
        Assert.DoesNotContain(notes!, n => n.Id == b.NoteId);
    }

    [Fact]
    public async Task Registered_devices_take_the_token_tenant()
    {
        var client = _factory.StaffClient("tenant-a");

        var response = await client.PostAsJsonAsync("/api/vision/devices",
            new RegisterDeviceRequest { Name = "Op 1", Type = DeviceType.IpCamera, Location = CameraLocation.Operatory });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("tenant-a", (await response.Content.ReadFromJsonAsync<VisionDeviceDto>())!.TenantId);
    }

    [Fact]
    public async Task Device_key_reaches_only_its_own_tenant_devices()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var b = await _factory.SeedTenantAsync("tenant-b");
        var device = _factory.DeviceClient("tenant-a");
        var detections = new List<DetectionPayload> { new() { ClassName = "Person", Confidence = 0.9 } };

        var otherHeartbeat = await device.PostAsync($"/api/vision/devices/{b.DeviceId}/heartbeat", null);
        var otherIngest = await device.PostAsJsonAsync("/api/vision/detections",
            new IngestDetectionRequest { DeviceId = b.DeviceId, Detections = detections, Timestamp = DateTime.UtcNow });
        var otherCabinet = await device.PostAsJsonAsync("/api/vision/cabinet/access",
            new LogCabinetAccessRequest { DeviceId = b.DeviceId, BadgeId = "B-1", DoorOpenedAt = DateTime.UtcNow });
        var ownHeartbeat = await device.PostAsync($"/api/vision/devices/{a.DeviceId}/heartbeat", null);
        var ownIngest = await device.PostAsJsonAsync("/api/vision/detections",
            new IngestDetectionRequest { DeviceId = a.DeviceId, Detections = detections, Timestamp = DateTime.UtcNow });

        Assert.Equal(HttpStatusCode.NotFound, otherHeartbeat.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherIngest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherCabinet.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownHeartbeat.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownIngest.StatusCode);
        Assert.Equal("tenant-a", (await ownIngest.Content.ReadFromJsonAsync<VisionEventDto>())!.TenantId);
    }

    private static HttpRequestMessage Request(string method, string route)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT") request.Content = JsonContent.Create(new { });
        return request;
    }
}

public sealed class VisionHubSecurityTests : IClassFixture<VisionSecurityFactory>
{
    private readonly VisionSecurityFactory _factory;

    public VisionHubSecurityTests(VisionSecurityFactory factory) => _factory = factory;

    [Fact]
    public async Task Hub_rejects_connections_without_credentials()
    {
        var negotiate = await _factory.CreateClient().PostAsync("/hubs/vision/negotiate?negotiateVersion=1", null);
        var connection = _factory.HubConnection();

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    [Fact]
    public async Task Hub_accepts_staff_token_in_access_token_query_and_device_key_headers()
    {
        var token = VisionSecurityFactory.Token("tenant-a");
        var client = _factory.CreateClient();

        var staff = await client.PostAsync($"/hubs/vision/negotiate?negotiateVersion=1&access_token={token}", null);
        var patient = await client.PostAsync(
            $"/hubs/vision/negotiate?negotiateVersion=1&access_token={VisionSecurityFactory.Token("tenant-a", role: "Patient")}", null);
        var device = await _factory.DeviceClient("tenant-a").PostAsync("/hubs/vision/negotiate?negotiateVersion=1", null);

        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, patient.StatusCode);
        Assert.Equal(HttpStatusCode.OK, device.StatusCode);
    }

    [Fact]
    public async Task Hub_clients_receive_only_their_own_tenant_events()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var b = await _factory.SeedTenantAsync("tenant-b");
        var staffA = _factory.HubConnection(token: VisionSecurityFactory.Token("tenant-a"));
        var staffB = _factory.HubConnection(token: VisionSecurityFactory.Token("tenant-b"));
        var receivedByA = new List<VisionEventDto>();
        var receivedByB = new TaskCompletionSource<VisionEventDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        staffA.On<VisionEventDto>("VisionEvent", e => { lock (receivedByA) receivedByA.Add(e); });
        staffB.On<VisionEventDto>("VisionEvent", e => receivedByB.TrySetResult(e));
        await staffA.StartAsync();
        await staffB.StartAsync();

        // Legacy clients could pick any group with ?tenantId=; that value is now ignored.
        var spoofing = _factory.HubConnection(token: VisionSecurityFactory.Token("tenant-a"), query: "?tenantId=tenant-b");
        var receivedBySpoofer = new List<VisionEventDto>();
        spoofing.On<VisionEventDto>("VisionEvent", e => { lock (receivedBySpoofer) receivedBySpoofer.Add(e); });
        await spoofing.StartAsync();

        var ingest = await _factory.DeviceClient("tenant-b").PostAsJsonAsync("/api/vision/detections", new IngestDetectionRequest
        {
            DeviceId = b.DeviceId, Timestamp = DateTime.UtcNow,
            Detections = [new() { ClassName = "Person", Confidence = 0.9 }]
        });
        ingest.EnsureSuccessStatusCode();

        var delivered = await receivedByB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        Assert.Equal("tenant-b", delivered.TenantId);
        lock (receivedByA) Assert.DoesNotContain(receivedByA, e => e.TenantId == "tenant-b");
        lock (receivedBySpoofer) Assert.DoesNotContain(receivedBySpoofer, e => e.TenantId == "tenant-b");

        await staffA.DisposeAsync();
        await staffB.DisposeAsync();
        await spoofing.DisposeAsync();
        _ = a;
    }

    [Fact]
    public async Task Hub_methods_enforce_caller_kind_and_device_tenant()
    {
        var a = await _factory.SeedTenantAsync("tenant-a");
        var b = await _factory.SeedTenantAsync("tenant-b");
        var device = _factory.HubConnection(deviceTenant: "tenant-a");
        var staff = _factory.HubConnection(token: VisionSecurityFactory.Token("tenant-a"));
        await device.StartAsync();
        await staff.StartAsync();

        await device.InvokeAsync("Heartbeat", a.DeviceId, DeviceStatus.Online);
        await Assert.ThrowsAsync<HubException>(() => device.InvokeAsync("Heartbeat", b.DeviceId, DeviceStatus.Online));
        await Assert.ThrowsAsync<HubException>(() => device.InvokeAsync("SubscribeToAlerts"));
        await Assert.ThrowsAsync<HubException>(() => staff.InvokeAsync("Heartbeat", a.DeviceId, DeviceStatus.Online));
        await Assert.ThrowsAsync<HubException>(() => staff.InvokeAsync("SubscribeToDevice", b.DeviceId));
        await staff.InvokeAsync("SubscribeToDevice", a.DeviceId);

        await device.DisposeAsync();
        await staff.DisposeAsync();
    }
}

public sealed class VisionStartupTests
{
    [Fact]
    public void Missing_jwt_key_outside_development_fails_startup()
    {
        using var factory = VisionSecurityFactory.For("Production", "");

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("Jwt:Key", error.Message);
    }
}

public sealed class VisionSecurityFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-vision-security-key-1234567890";
    public const string DeviceKeyA = "test-only-device-key-tenant-a-1234567890";
    public const string DeviceKeyB = "test-only-device-key-tenant-b-1234567890";
    private const string Issuer = "test-issuer";
    private const string Audience = "test-audience";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-vision-security-{Guid.NewGuid():N}.db");
    private readonly string _environment;
    private readonly string _jwtKey;

    public VisionSecurityFactory() : this("Testing", JwtKey) { }

    private VisionSecurityFactory(string environment, string jwtKey)
    {
        _environment = environment;
        _jwtKey = jwtKey;
    }

    public static VisionSecurityFactory For(string environment, string jwtKey) => new(environment, jwtKey);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Jwt:Key", _jwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("DeviceApi:Clients:0:TenantId", "tenant-a");
        builder.UseSetting("DeviceApi:Clients:0:ApiKey", DeviceKeyA);
        builder.UseSetting("DeviceApi:Clients:1:TenantId", "tenant-b");
        builder.UseSetting("DeviceApi:Clients:1:ApiKey", DeviceKeyB);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<VisionDbContext>>();
            services.AddDbContext<VisionDbContext>(options => options.UseSqlite($"Data Source={_database}"));
        });
    }

    // Mirrors the Portal's staff tokens: HMAC-SHA256 with the shared key and a tenant_id claim.
    public static string Token(string? tenantId, string key = JwtKey, string? role = "Staff")
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, "staff-user") };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        if (tenantId is not null) claims.Add(new("tenant_id", tenantId));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer, Audience, claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
    }

    public HttpClient StaffClient(string tenantId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(tenantId));
        return client;
    }

    public HttpClient DeviceClient(string tenantId, string? key = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(DeviceKeyAuthentication.TenantHeader, tenantId);
        client.DefaultRequestHeaders.Add(DeviceKeyAuthentication.KeyHeader, key ?? (tenantId == "tenant-a" ? DeviceKeyA : DeviceKeyB));
        return client;
    }

    public HubConnection HubConnection(string? token = null, string? deviceTenant = null, string query = "")
    {
        _ = Server;
        return new HubConnectionBuilder()
            .WithUrl(new Uri(Server.BaseAddress, "/hubs/vision" + query), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                if (deviceTenant is not null)
                {
                    options.Headers[DeviceKeyAuthentication.TenantHeader] = deviceTenant;
                    options.Headers[DeviceKeyAuthentication.KeyHeader] = deviceTenant == "tenant-a" ? DeviceKeyA : DeviceKeyB;
                }
            })
            .Build();
    }

    public sealed record TenantSeed(Guid DeviceId, Guid EventId, Guid ConsentId, Guid NoteId);

    public async Task<TenantSeed> SeedTenantAsync(string tenantId)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<VisionDbContext>();
        var device = new VisionDevice
        {
            Name = $"{tenantId} camera", Type = DeviceType.IpCamera, Location = CameraLocation.Operatory,
            Status = DeviceStatus.Online, TenantId = tenantId
        };
        var visionEvent = new VisionEvent
        {
            EventType = VisionEventType.Detection, Timestamp = DateTime.UtcNow, Device = device, TenantId = tenantId
        };
        var consent = new ConsentRecording
        {
            DeviceId = device.Id, PatientId = Guid.NewGuid(), ProviderId = Guid.NewGuid(), ConsentType = "Extraction",
            Status = ConsentStatus.InProgress, TenantId = tenantId
        };
        var note = new ClinicalNoteDraft
        {
            AppointmentId = Guid.NewGuid(), PatientId = Guid.NewGuid(), ProviderId = Guid.NewGuid(),
            DraftNoteText = "Draft", TenantId = tenantId
        };
        db.AddRange(device, visionEvent, consent, note);
        await db.SaveChangesAsync();
        return new(device.Id, visionEvent.Id, consent.Id, note.Id);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_database)) File.Delete(_database);
    }
}

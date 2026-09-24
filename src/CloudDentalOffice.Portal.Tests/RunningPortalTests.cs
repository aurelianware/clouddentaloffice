using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Playwright;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Runs the real Portal process on real Kestrel sockets, behind a stand-in for the Container Apps
/// EasyAuth proxy, and drives it with a real browser (Blazor Server circuit over WebSockets).
/// </summary>
public sealed class RunningPortalTests(RunningPortalFixture portal) : IClassFixture<RunningPortalFixture>
{
    [Fact]
    public async Task Http_client_handlers_inside_a_Blazor_circuit_forward_the_signed_in_staff_user()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        var circuit = page.WaitForWebSocketAsync();

        await page.GotoAsync(new Uri(portal.EasyAuthUrl, "/appointment-requests").ToString(),
            new() { WaitUntil = WaitUntilState.NetworkIdle });
        await circuit;
        await page.WaitForFunctionAsync("() => window.Blazor !== undefined");

        // Changing the status filter runs inside the live circuit only (not prerendering) and calls
        // BookingRequestServiceHttpClient through SchedulingTenantAuthorizationHandler.
        await page.Locator(".mud-select").First.ClickAsync();
        await page.GetByText("Approved", new() { Exact = true }).Last.ClickAsync();
        var request = await portal.WaitForGatewayRequestAsync(r => r.PathAndQuery == "/api/booking-requests?status=Approved");

        Assert.Equal("Bearer", request.Scheme);
        var principal = new JwtSecurityTokenHandler().ValidateToken(request.Token, new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(RunningPortalFixture.JwtKey)),
            ValidIssuer = "CloudDentalOffice",
            ValidAudience = "CloudDentalOffice"
        }, out _);
        Assert.Equal(RunningPortalFixture.TenantId, principal.FindFirst("tenant_id")?.Value);
    }

    [Fact]
    public async Task Internal_match_or_create_is_only_reachable_on_the_internal_socket()
    {
        using var http = new HttpClient();
        var path = $"{InternalPatientApi.MatchOrCreatePath}?tenantId={RunningPortalFixture.TenantId}";

        var viaPublicIngress = await http.SendAsync(MatchRequest(new Uri(portal.EasyAuthUrl, path), RunningPortalFixture.ServiceKey));
        // Host header claims the internal port, but the request arrives on the public socket.
        var spoofedHost = MatchRequest(new Uri(portal.PublicUrl, path), RunningPortalFixture.ServiceKey);
        spoofedHost.Headers.Host = $"127.0.0.1:{portal.InternalUrl.Port}";
        spoofedHost.Headers.Add(ContainerAppsStaffIdentity.PrincipalHeader, RunningPortalFixture.EasyAuthPrincipal);
        var viaSpoofedHost = await http.SendAsync(spoofedHost);
        var wrongKey = await http.SendAsync(MatchRequest(new Uri(portal.InternalUrl, path), "wrong-internal-service-key-1234567890"));
        var valid = await http.SendAsync(MatchRequest(new Uri(portal.InternalUrl, path), RunningPortalFixture.ServiceKey));

        Assert.Equal(HttpStatusCode.NotFound, viaPublicIngress.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaSpoofedHost.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    private static HttpRequestMessage MatchRequest(Uri uri, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new MatchOrCreateExternalPatientRequest
            {
                FirstName = "Kim", LastName = "Kestrel", DateOfBirth = new DateOnly(1988, 8, 8)
            })
        };
        request.Headers.Add(InternalServiceKey.Header, key);
        return request;
    }
}

public sealed record GatewayRequest(string Method, string PathAndQuery, string? Scheme, string? Token);

public sealed class RunningPortalFixture : IAsyncLifetime
{
    public const string JwtKey = "running-portal-circuit-test-key-1234567890";
    public const string ServiceKey = "running-portal-internal-service-key-1234567890";
    public const string TenantId = "tenant-a";
    private const string StaffEmail = "staff@example.test";

    public static readonly string EasyAuthPrincipal = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
    {
        auth_typ = "google", identity_provider = "google", user_id = "staff-1",
        claims = new[] { new { typ = "email", val = StaffEmail }, new { typ = "name", val = "Staff User" } }
    }));

    private readonly ConcurrentQueue<GatewayRequest> _gatewayRequests = new();
    private readonly StringBuilder _portalOutput = new();
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-running-portal-{Guid.NewGuid():N}.db");
    private WebApplication? _gateway;
    private WebApplication? _easyAuth;
    private Process? _portal;

    public Uri PublicUrl { get; private set; } = null!;
    public Uri InternalUrl { get; private set; } = null!;
    public Uri EasyAuthUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var gatewayUrl = new Uri($"http://127.0.0.1:{FreePort()}");
        PublicUrl = new Uri($"http://127.0.0.1:{FreePort()}");
        InternalUrl = new Uri($"http://127.0.0.1:{FreePort()}");
        EasyAuthUrl = new Uri($"http://127.0.0.1:{FreePort()}");

        _gateway = StartGateway(gatewayUrl);
        await _gateway.StartAsync();
        _portal = StartPortal(gatewayUrl);
        await WaitForHealthyAsync();
        _easyAuth = StartEasyAuthProxy();
        await _easyAuth.StartAsync();
    }

    public async Task<GatewayRequest> WaitForGatewayRequestAsync(Func<GatewayRequest, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var match = _gatewayRequests.FirstOrDefault(predicate);
            if (match is not null) return match;
            await Task.Delay(100);
        }
        throw new TimeoutException("The gateway never received the expected request. Received: " +
            string.Join(", ", _gatewayRequests.Select(r => $"{r.Method} {r.PathAndQuery} ({r.Scheme ?? "no auth"})")) +
            "\nPortal output tail:\n" + OutputTail());
    }

    private WebApplication StartGateway(Uri url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url.ToString());
        var app = builder.Build();
        app.Run(async context =>
        {
            var authorization = context.Request.Headers.Authorization.ToString().Split(' ', 2);
            _gatewayRequests.Enqueue(new(context.Request.Method, context.Request.Path + context.Request.QueryString,
                authorization.Length == 2 ? authorization[0] : null, authorization.Length == 2 ? authorization[1] : null));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("[]");
        });
        return app;
    }

    private Process StartPortal(Uri gatewayUrl)
    {
        var root = RepositoryRoot();
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = Path.Combine(root, "src", "CloudDentalOffice.Portal"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "run", "--no-build", "--no-launch-profile", "-c", configuration })
            start.ArgumentList.Add(argument);
        var settings = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_URLS"] = PublicUrl.ToString(),
            ["InternalApi__Port"] = InternalUrl.Port.ToString(),
            ["InternalApi__Clients__0__TenantId"] = TenantId,
            ["InternalApi__Clients__0__ApiKey"] = ServiceKey,
            ["AzureAd__Enabled"] = "false",
            ["StaffAuth__Enabled"] = "true",
            ["StaffAuth__TenantId"] = TenantId,
            ["StaffAuth__Users__0__Email"] = StaffEmail,
            ["StaffAuth__Users__0__Role"] = "Admin",
            ["Jwt__Key"] = JwtKey,
            ["ApiGateway__BaseUrl"] = gatewayUrl.ToString(),
            ["Database__Provider"] = "Sqlite",
            ["ConnectionStrings__DefaultConnection"] = $"Data Source={_database}"
        };
        foreach (var (key, value) in settings) start.Environment[key] = value;

        var process = Process.Start(start) ?? throw new InvalidOperationException("The Portal process did not start.");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (_portalOutput) _portalOutput.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_portalOutput) _portalOutput.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task WaitForHealthyAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (_portal!.HasExited)
                throw new InvalidOperationException($"The Portal exited ({_portal.ExitCode}).\n{OutputTail()}");
            try
            {
                if ((await http.GetAsync(new Uri(PublicUrl, "/health/live"))).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(500);
        }
        throw new TimeoutException($"The Portal did not become healthy.\n{OutputTail()}");
    }

    // Stand-in for the Container Apps EasyAuth sidecar: injects the principal header on every
    // request, including the WebSocket upgrade that opens the Blazor circuit.
    private WebApplication StartEasyAuthProxy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(EasyAuthUrl.ToString());
        builder.Services.AddReverseProxy()
            .LoadFromMemory(
                [new RouteConfig { RouteId = "portal", ClusterId = "portal", Match = new RouteMatch { Path = "{**catch-all}" } }],
                [new ClusterConfig
                {
                    ClusterId = "portal",
                    Destinations = new Dictionary<string, DestinationConfig> { ["portal"] = new() { Address = PublicUrl.ToString() } }
                }])
            .AddTransforms(context => context.AddRequestHeader(ContainerAppsStaffIdentity.PrincipalHeader, EasyAuthPrincipal, append: false));
        var app = builder.Build();
        app.MapReverseProxy();
        return app;
    }

    private string OutputTail()
    {
        lock (_portalOutput)
        {
            var text = _portalOutput.ToString();
            return text.Length <= 6000 ? text : text[^6000..];
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CloudDentalOffice.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public async Task DisposeAsync()
    {
        if (_easyAuth is not null) await _easyAuth.DisposeAsync();
        if (_portal is { HasExited: false })
        {
            _portal.Kill(entireProcessTree: true);
            await _portal.WaitForExitAsync();
        }
        _portal?.Dispose();
        if (_gateway is not null) await _gateway.DisposeAsync();
        foreach (var file in new[] { _database, _database + "-shm", _database + "-wal" })
            if (File.Exists(file)) File.Delete(file);
    }
}

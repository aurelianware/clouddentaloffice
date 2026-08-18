using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;

namespace CloudDentalOffice.Portal.Tests;

public sealed class SchedulingIntegrationAdminTests
{
    [Fact]
    public async Task AuthorizationHandler_ForwardsOnlyAdminTenantContext()
    {
        var terminal = new CaptureHandler();
        var handler = Handler([new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Admin"), new("TenantId", "tenant-a")]);
        handler.InnerHandler = terminal;
        await new HttpClient(handler).GetAsync("https://gateway.test/api/scheduling-integrations/zocdoc/overview");

        var token = new JwtSecurityTokenHandler().ReadJwtToken(terminal.Authorization);
        Assert.Equal("tenant-a", token.Claims.Single(x => x.Type == "tenant_id").Value);
        Assert.Contains(token.Claims, x => x.Type == ClaimTypes.Role && x.Value == "Admin");
        Assert.DoesNotContain(token.Claims, x => x.Value == "tenant-b");
    }

    [Fact]
    public async Task AuthorizationHandler_RejectsNonAdminAndMissingTenant()
    {
        var nonAdmin = Handler([new(ClaimTypes.Name, "staff"), new("TenantId", "tenant-a")]);
        nonAdmin.InnerHandler = new CaptureHandler();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new HttpClient(nonAdmin).GetAsync("https://gateway.test/"));

        var noTenant = Handler([new(ClaimTypes.Name, "admin"), new(ClaimTypes.Role, "Admin")]);
        noTenant.InnerHandler = new CaptureHandler();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new HttpClient(noTenant).GetAsync("https://gateway.test/"));
    }

    [Fact]
    public async Task StaffAuthorizationHandler_ForwardsAuthenticatedTenantWithoutAdminRole()
    {
        var terminal = new CaptureHandler();
        var identity = new ClaimsIdentity([new(ClaimTypes.Name, "staff"), new("TenantId", "tenant-a")], "test");
        var handler = new SchedulingTenantAuthorizationHandler(
            new FixedAuthenticationStateProvider(new(new ClaimsPrincipal(identity))), Configuration());
        handler.InnerHandler = terminal;

        await new HttpClient(handler).GetAsync("https://gateway.test/api/booking-requests");

        var token = new JwtSecurityTokenHandler().ReadJwtToken(terminal.Authorization);
        Assert.Equal("tenant-a", token.Claims.Single(x => x.Type == "tenant_id").Value);
        Assert.DoesNotContain(token.Claims, x => x.Type == ClaimTypes.Role && x.Value == "Admin");
    }

    [Fact]
    public async Task StaffAuthorizationHandler_FailsClosedWithoutTenantOrJwtKey()
    {
        var noTenant = new SchedulingTenantAuthorizationHandler(
            new FixedAuthenticationStateProvider(new(new ClaimsPrincipal(
                new ClaimsIdentity([new(ClaimTypes.Name, "staff")], "test")))),
            Configuration());
        noTenant.InnerHandler = new CaptureHandler();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new HttpClient(noTenant).GetAsync("https://gateway.test/"));

        var noKey = new SchedulingTenantAuthorizationHandler(
            new FixedAuthenticationStateProvider(new(new ClaimsPrincipal(new ClaimsIdentity(
                [new(ClaimTypes.Name, "staff"), new("TenantId", "tenant-a")], "test")))),
            new ConfigurationBuilder().Build());
        noKey.InnerHandler = new CaptureHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new HttpClient(noKey).GetAsync("https://gateway.test/"));
    }

    [Fact]
    public void PageRequiresAdminRole()
    {
        var attributes = typeof(CloudDentalOffice.Portal.Pages.Admin.SchedulingIntegrations)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>();
        Assert.Contains(attributes, attribute => attribute.Roles == "Admin");
    }

    [Fact]
    public void RemotePayloadIsNotSurfacedToStaff()
    {
        const string phi = "patient Jane Doe failed: raw-token-123";
        Assert.Equal("Scheduling integration request failed.",
            SchedulingIntegrationAdminClient.Sanitize(phi, "Bad Gateway"));
        Assert.Equal("Authentication failed",
            SchedulingIntegrationAdminClient.Sanitize("{\"title\":\"Authentication failed\",\"patient\":\"Jane Doe\"}", null));
        Assert.Equal("TimeZoneId is required.",
            SchedulingIntegrationAdminClient.Sanitize("{\"title\":\"One or more validation errors occurred.\",\"errors\":{\"TimeZoneId\":[\"TimeZoneId is required.\"]}}", null));
    }

    [Fact]
    public async Task BookingRequestClient_DoesNotSendCallerControlledTenant()
    {
        var terminal = new JsonCaptureHandler();
        var service = new BookingRequestServiceHttpClient(new HttpClient(terminal)
        { BaseAddress = new Uri("https://gateway.test/") });

        await service.GetAsync("New");

        Assert.Equal("/api/booking-requests?status=New", terminal.RequestUri?.PathAndQuery);
        Assert.DoesNotContain("tenant", terminal.RequestUri?.Query ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private static SchedulingAdminAuthorizationHandler Handler(IEnumerable<Claim> claims)
    {
        var identity = new ClaimsIdentity(claims, "test");
        var auth = new FixedAuthenticationStateProvider(new(new(identity)));
        return new(auth, Configuration());
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-key-with-at-least-thirty-two-bytes-long",
            ["Jwt:Issuer"] = "CloudDentalOffice",
            ["Jwt:Audience"] = "CloudDentalOffice"
        }).Build();

    private sealed class FixedAuthenticationStateProvider(AuthenticationState state) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state); }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string Authorization { get; private set; } = string.Empty;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.Parameter ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class JsonCaptureHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}

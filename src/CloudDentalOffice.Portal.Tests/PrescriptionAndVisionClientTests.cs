using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Contracts.Prescriptions;
using CloudDentalOffice.Contracts.Vision;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PrescriptionAndVisionClientTests
{
    private const string JwtKey = "portal-rx-vision-forwarding-test-key-1234567890";

    [Fact]
    public async Task Prescription_and_vision_clients_forward_staff_bearer_token()
    {
        var capture = new CaptureHandler();
        using var factory = new PortalFactory(capture);
        _ = factory.Services;

        await using var scope = factory.Services.CreateAsyncScope();
        var prescriptions = scope.ServiceProvider.GetRequiredService<IPrescriptionService>();
        var vision = scope.ServiceProvider.GetRequiredService<IVisionService>();
        Assert.IsType<PrescriptionServiceHttpClient>(prescriptions);
        Assert.IsType<VisionServiceHttpClient>(vision);

        await prescriptions.GetPatientPrescriptionsAsync(Guid.NewGuid());
        await vision.GetDevicesAsync();

        Assert.Equal(2, capture.Requests.Count);
        Assert.StartsWith("/api/prescriptions/patient/", capture.Requests[0].Path);
        Assert.Equal("/api/vision/devices", capture.Requests[1].Path);
        foreach (var request in capture.Requests)
        {
            Assert.Equal("Bearer", request.Scheme);
            var principal = new JwtSecurityTokenHandler().ValidateToken(request.Token, new TokenValidationParameters
            {
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
                ValidIssuer = "CloudDentalOffice",
                ValidAudience = "CloudDentalOffice"
            }, out _);
            Assert.Equal("tenant-a", principal.FindFirst("tenant_id")?.Value);
        }
    }

    private sealed class PortalFactory(CaptureHandler capture) : WebApplicationFactory<Program>
    {
        private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-portal-rx-vision-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ApiGateway:BaseUrl", "https://gateway.test");
            builder.UseSetting("Jwt:Key", JwtKey);
            builder.UseSetting("AzureAd:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_database}");
            builder.ConfigureTestServices(services =>
            {
                var identity = new ClaimsIdentity(
                    [new(ClaimTypes.Name, "staff"), new(ClaimTypes.Role, "Staff"), new("TenantId", "tenant-a")], "test");
                services.AddScoped<AuthenticationStateProvider>(_ =>
                    new FixedAuthenticationStateProvider(new(new ClaimsPrincipal(identity))));
                services.AddHttpClient<IPrescriptionService, PrescriptionServiceHttpClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => capture);
                services.AddHttpClient<IVisionService, VisionServiceHttpClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => capture);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_database)) File.Delete(_database);
        }
    }

    private sealed class FixedAuthenticationStateProvider(AuthenticationState state) : AuthenticationStateProvider
    { public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state); }

    private sealed record CapturedRequest(string Path, string? Scheme, string? Token);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientServiceHttpClientTests
{
    private const string JwtKey = "portal-patient-forwarding-test-key-1234567890";

    [Fact]
    public async Task Patient_microservice_client_forwards_staff_bearer_token_without_tenant_query()
    {
        var capture = new CaptureHandler();
        using var factory = new PatientMicroserviceFactory(capture);
        _ = factory.Services;

        await using var scope = factory.Services.CreateAsyncScope();
        var patients = scope.ServiceProvider.GetRequiredService<IPatientService>();
        Assert.IsType<PatientServiceHttpClient>(patients);

        await patients.GetPatientsAsync();
        await patients.DeletePatientAsync("42");

        Assert.Equal(["GET /api/patients", "DELETE /api/patients/42"], capture.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"));
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

    private sealed class PatientMicroserviceFactory(CaptureHandler capture) : WebApplicationFactory<Program>
    {
        private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-portal-patient-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Microservices:Patient:Enabled", "true");
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
                services.AddHttpClient<IPatientService, PatientServiceHttpClient>()
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

    private sealed record CapturedRequest(string Method, string PathAndQuery, string? Scheme, string? Token);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter));
            return Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}

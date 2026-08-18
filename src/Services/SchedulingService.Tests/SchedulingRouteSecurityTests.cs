using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class SchedulingRouteSecurityTests : IClassFixture<SchedulingSecurityFactory>
{
    private readonly SchedulingSecurityFactory _factory;
    private readonly HttpClient _client;

    public SchedulingRouteSecurityTests(SchedulingSecurityFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/api/appointments")]
    [InlineData("/api/booking-requests")]
    [InlineData("/api/scheduling-integrations/zocdoc/overview")]
    public async Task Protected_routes_reject_anonymous_requests(string route)
    {
        var response = await _client.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Appointment_reads_use_authenticated_tenant_and_hide_other_tenants()
    {
        var ownId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await _factory.SeedAsync(
            new Appointment { Id = ownId, TenantId = "tenant-a", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddMinutes(30) },
            new Appointment { Id = otherId, TenantId = "tenant-b", StartTime = DateTime.UtcNow, EndTime = DateTime.UtcNow.AddMinutes(30) });
        Authorize("tenant-a");

        var list = await _client.GetStringAsync("/api/appointments");
        Assert.Contains(ownId.ToString(), list);
        Assert.DoesNotContain(otherId.ToString(), list);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/appointments/{otherId}")).StatusCode);
    }

    [Fact]
    public async Task Integration_admin_requires_admin_role()
    {
        Authorize("tenant-a");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _client.GetAsync("/api/scheduling-integrations/zocdoc/overview")).StatusCode);
        Authorize("tenant-a", "Admin");
        Assert.Equal(HttpStatusCode.OK,
            (await _client.GetAsync("/api/scheduling-integrations/zocdoc/overview")).StatusCode);
    }

    [Fact]
    public async Task Health_is_public_and_contains_no_diagnostics_or_topology()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    private void Authorize(string tenantId, string? role = null)
    {
        var claims = new List<Claim> { new("tenant_id", tenantId) };
        if (role is not null) claims.Add(new(ClaimTypes.Role, role));
        var token = new JwtSecurityToken("test-issuer", "test-audience", claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SchedulingSecurityFactory.JwtKey)),
                SecurityAlgorithms.HmacSha256));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
    }
}

public sealed class SchedulingSecurityFactory : WebApplicationFactory<Program>
{
    public const string JwtKey = "test-only-scheduling-security-key-1234567890";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-scheduling-security-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", "test-issuer");
        builder.UseSetting("Jwt:Audience", "test-audience");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["DatabaseProvider"] = "Sqlite",
                ["ConnectionStrings:SchedulingDb"] = $"Data Source={_database}",
                ["Jwt:Key"] = JwtKey,
                ["Jwt:Issuer"] = "test-issuer",
                ["Jwt:Audience"] = "test-audience"
            }));
    }

    public async Task SeedAsync(params Appointment[] appointments)
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        db.Appointments.AddRange(appointments);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_database)) File.Delete(_database);
    }
}

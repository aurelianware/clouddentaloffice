using System.IdentityModel.Tokens.Jwt;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class JwtSettingsStartupTests
{
    [Fact]
    public void Production_without_key_fails_startup()
    {
        using var factory = new PortalFactory(Environments.Production, jwtKey: null);

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("Jwt:Key is required", error.Message);
    }

    [Fact]
    public void Production_with_short_key_fails_startup()
    {
        using var factory = new PortalFactory(Environments.Production, jwtKey: "too-short-signing-key");

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains($"at least {JwtSettings.MinimumKeyBytes} bytes", error.Message);
    }

    [Theory]
    [MemberData(nameof(KnownDevelopmentKeys))]
    public void Production_with_published_development_key_fails_startup(string key)
    {
        using var factory = new PortalFactory(Environments.Production, jwtKey: key);

        var error = Assert.Throws<InvalidOperationException>(() => factory.Services);

        Assert.Contains("published development key", error.Message);
    }

    [Fact]
    public async Task Development_without_key_starts_and_validates_issued_tokens()
    {
        using var factory = new PortalFactory(Environments.Development, jwtKey: null);
        using var client = factory.CreateClient();

        var settings = factory.Services.GetRequiredService<JwtSettings>();
        Assert.True(settings.UsesEphemeralKey);
        Assert.Equal(JwtSettings.MinimumKeyBytes, settings.SigningKey.Key.Length);

        var live = await client.GetAsync("/health/live");
        Assert.True(live.IsSuccessStatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var token = scope.ServiceProvider.GetRequiredService<ITokenService>()
            .GenerateToken("user-id", "user@example.com", "tenant-a", "Admin");
        var validation = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme).TokenValidationParameters;

        var principal = new JwtSecurityTokenHandler().ValidateToken(token, validation, out _);

        Assert.Equal("tenant-a", principal.FindFirst("tenant_id")?.Value);
    }

    [Fact]
    public void Issued_token_validates_with_resolved_production_key()
    {
        var settings = JwtSettings.Resolve(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "unit-test-production-key-with-at-least-32-bytes",
                ["Jwt:Issuer"] = "issuer",
                ["Jwt:Audience"] = "audience"
            }).Build(),
            new HostingEnvironment { EnvironmentName = Environments.Production });
        var token = new TokenService(settings).GenerateToken("user-id", "user@example.com", "tenant-a", "Staff");

        new JwtSecurityTokenHandler().ValidateToken(token, settings.CreateValidationParameters(), out var validated);

        Assert.Equal("issuer", validated.Issuer);
        Assert.False(settings.UsesEphemeralKey);
    }

    public static TheoryData<string> KnownDevelopmentKeys()
    {
        var data = new TheoryData<string>();
        foreach (var key in JwtSettings.KnownDevelopmentKeys) data.Add(key);
        return data;
    }

    private sealed class PortalFactory(string environment, string? jwtKey) : WebApplicationFactory<Program>
    {
        private readonly string _database = Path.Combine(Path.GetTempPath(), $"cdo-portal-jwt-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Jwt:Key", jwtKey ?? "");
            builder.UseSetting("AzureAd:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_database}");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && File.Exists(_database)) File.Delete(_database);
        }
    }
}

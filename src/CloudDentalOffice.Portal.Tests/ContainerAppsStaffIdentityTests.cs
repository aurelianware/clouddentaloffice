using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public sealed class ContainerAppsStaffIdentityTests
{
    [Theory]
    [InlineData("matt@3rdsetsmiles.com")]
    [InlineData("markus.phillips@gmail.com")]
    public void AllowlistedGoogleIdentityGetsAdminRoleAndInitialTenant(string email)
    {
        var principal = ContainerAppsStaffIdentity.Resolve(Context(email), Configuration());

        Assert.NotNull(principal);
        Assert.True(principal.Identity?.IsAuthenticated);
        Assert.Equal(email, principal.FindFirstValue(ClaimTypes.Email));
        Assert.True(principal.IsInRole("Admin"));
        Assert.Equal("third-set-smiles", principal.FindFirstValue("tenant_id"));
    }

    [Fact]
    public void SameWorkspaceDomainIsNotEnoughWithoutExplicitAllowlist()
    {
        var principal = ContainerAppsStaffIdentity.Resolve(Context("unknown@3rdsetsmiles.com"), Configuration());
        Assert.Null(principal);
    }

    [Fact]
    public void ConsumerGoogleAccountIsDeniedUnlessExplicitlyAllowlisted()
    {
        var principal = ContainerAppsStaffIdentity.Resolve(Context("other@gmail.com"), Configuration());
        Assert.Null(principal);
    }

    [Fact]
    public void InvalidEasyAuthHeaderIsDenied()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader] = "not-base64";
        Assert.Null(ContainerAppsStaffIdentity.Resolve(context, Configuration()));
    }

    [Fact]
    public void OversizedInvalidEasyAuthHeaderIsDenied()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader] = new string('x', 32769);

        Assert.Null(ContainerAppsStaffIdentity.Resolve(context, Configuration()));
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthProbeWithoutPrincipalBypassesAllowlistWhenStaffAuthEnabled(string path)
    {
        var nextCalled = false;
        var middleware = new StaffAccessMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            ConfigurationWithStaffAuthEnabled(), NullLogger<StaffAccessMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HealthPathWithUnauthorizedPrincipalStillEnforcesAllowlist()
    {
        var nextCalled = false;
        var middleware = new StaffAccessMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            ConfigurationWithStaffAuthEnabled(), NullLogger<StaffAccessMiddleware>.Instance);
        var context = Context("unknown@3rdsetsmiles.com");
        context.Request.Path = "/health/ready";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static IConfiguration ConfigurationWithStaffAuthEnabled() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StaffAuth:Enabled"] = "true",
            ["StaffAuth:TenantId"] = "third-set-smiles",
            ["StaffAuth:Users:0:Email"] = "matt@3rdsetsmiles.com",
            ["StaffAuth:Users:0:Role"] = "Admin"
        })
        .Build();

    private static DefaultHttpContext Context(string email)
    {
        var payload = JsonSerializer.Serialize(new
        {
            identityProvider = "google",
            userId = $"google-{email}",
            claims = new[]
            {
                new { typ = "email", val = email },
                new { typ = "name", val = "Test Staff" }
            }
        });
        var context = new DefaultHttpContext();
        context.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return context;
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StaffAuth:TenantId"] = "third-set-smiles",
            ["StaffAuth:Users:0:Email"] = "matt@3rdsetsmiles.com",
            ["StaffAuth:Users:0:Role"] = "Admin",
            ["StaffAuth:Users:1:Email"] = "markus.phillips@gmail.com",
            ["StaffAuth:Users:1:Role"] = "Admin"
        })
        .Build();
}

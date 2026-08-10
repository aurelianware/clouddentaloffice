using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

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

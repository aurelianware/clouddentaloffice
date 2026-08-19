using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CloudDentalOffice.Portal.Services;
using Microsoft.Extensions.Configuration;

namespace CloudDentalOffice.Portal.Tests;

public sealed class TokenServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GenerateToken_defaults_missing_legacy_role_to_staff(string? role)
    {
        var service = new TokenService(new ConfigurationBuilder().AddInMemoryCollection().Build());

        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            service.GenerateToken("user-id", "user@example.com", "tenant-a", role!));

        Assert.Equal("Staff", token.Claims.Single(x => x.Type == ClaimTypes.Role).Value);
    }

    [Fact]
    public void GenerateToken_trims_explicit_role()
    {
        var service = new TokenService(new ConfigurationBuilder().AddInMemoryCollection().Build());

        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            service.GenerateToken("user-id", "user@example.com", "tenant-a", " Patient "));

        Assert.Equal("Patient", token.Claims.Single(x => x.Type == ClaimTypes.Role).Value);
    }
}

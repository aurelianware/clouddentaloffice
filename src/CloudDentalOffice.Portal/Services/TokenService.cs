using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using CloudDentalOffice.Portal.Services.Auth;
using Microsoft.IdentityModel.Tokens;

namespace CloudDentalOffice.Portal.Services;

public interface ITokenService
{
    string GenerateToken(string userId, string email, string tenantId, string role);
}

public class TokenService : ITokenService
{
    private readonly JwtSettings _jwtSettings;

    public TokenService(JwtSettings jwtSettings)
    {
        _jwtSettings = jwtSettings;
    }

    public string GenerateToken(string userId, string email, string tenantId, string role)
    {
        var effectiveRole = string.IsNullOrWhiteSpace(role) ? "Staff" : role.Trim();
        var credentials = new SigningCredentials(_jwtSettings.SigningKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("tenant_id", tenantId),
            new Claim(ClaimTypes.Role, effectiveRole),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: DateTime.Now.AddMinutes(120),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

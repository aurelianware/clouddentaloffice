using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace CloudDentalOffice.Portal.Services.Auth;

/// <summary>
/// Portal JWT signing settings, resolved once at startup and shared by bearer
/// validation (Program.cs) and token issuance (<see cref="TokenService"/>).
/// </summary>
public sealed class JwtSettings
{
    public const int MinimumKeyBytes = 32;
    public const string DefaultIssuer = "CloudDentalOffice";
    public const string DefaultAudience = "CloudDentalOffice";

    // Keys that have been published in this repository. They must never sign
    // tokens outside Development.
    public static readonly IReadOnlyList<string> KnownDevelopmentKeys =
    [
        "ThisIsASecretKeyForDevelopmentOnly_DoNotUseInProduction_MakeItLonger",
        "CloudDentalOffice-Local-DevelopmentOnly-Key-ChangeMe!",
        "your-secure-jwt-key-at-least-32-characters-long"
    ];

    private JwtSettings(byte[] keyBytes, string issuer, string audience, bool usesEphemeralKey = false)
    {
        SigningKey = new SymmetricSecurityKey(keyBytes);
        Issuer = issuer;
        Audience = audience;
        UsesEphemeralKey = usesEphemeralKey;
    }

    public SymmetricSecurityKey SigningKey { get; }
    public string Issuer { get; }
    public string Audience { get; }

    /// <summary>True when Development had no Jwt:Key and a random per-process key was generated.</summary>
    public bool UsesEphemeralKey { get; }

    public TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey
    };

    /// <summary>
    /// Outside Development a missing, short, or published key fails startup.
    /// In Development a missing key is replaced by a random per-process key, so
    /// tokens only validate within this process.
    /// </summary>
    public static JwtSettings Resolve(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredKey = configuration["Jwt:Key"];
        var issuer = configuration["Jwt:Issuer"] ?? DefaultIssuer;
        var audience = configuration["Jwt:Audience"] ?? DefaultAudience;

        if (!environment.IsDevelopment())
        {
            if (string.IsNullOrWhiteSpace(configuredKey))
                throw new InvalidOperationException(
                    $"Jwt:Key is required in the {environment.EnvironmentName} environment. Supply a secret-backed Jwt__Key of at least {MinimumKeyBytes} bytes.");
            if (Encoding.UTF8.GetByteCount(configuredKey) < MinimumKeyBytes)
                throw new InvalidOperationException(
                    $"Jwt:Key must be at least {MinimumKeyBytes} bytes in the {environment.EnvironmentName} environment.");
            if (KnownDevelopmentKeys.Contains(configuredKey.Trim(), StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Jwt:Key is a published development key and cannot be used in the {environment.EnvironmentName} environment. Supply a unique secret-backed Jwt__Key.");

            return new JwtSettings(Encoding.UTF8.GetBytes(configuredKey), issuer, audience);
        }

        if (!string.IsNullOrWhiteSpace(configuredKey))
            return new JwtSettings(Encoding.UTF8.GetBytes(configuredKey), issuer, audience);

        return new JwtSettings(RandomNumberGenerator.GetBytes(MinimumKeyBytes), issuer, audience, usesEphemeralKey: true);
    }
}

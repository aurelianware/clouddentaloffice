using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudDentalOffice.Portal.Services.Auth;

public sealed record StaffAccessEntry(string Email, string Role);

public static class ContainerAppsStaffIdentity
{
    public const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";
    private const int MaxPrincipalHeaderLength = 32768;
    public const string AuthenticationType = "AzureContainerAppsGoogle";

    public static ClaimsPrincipal? Resolve(HttpContext context, IConfiguration configuration)
    {
        var encoded = context.Request.Headers[PrincipalHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        if (encoded.Length > MaxPrincipalHeaderLength) return null;

        ClientPrincipal? source;
        try
        {
            source = JsonSerializer.Deserialize<ClientPrincipal>(
                Convert.FromBase64String(encoded),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception)
        {
            return null;
        }

        var sourceClaims = source?.Claims ?? [];
        var email = ClaimValue(sourceClaims, ClaimTypes.Email, "email", "emails", "preferred_username", "upn")
            ?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email)) return null;

        var allowed = configuration.GetSection("StaffAuth:Users").Get<List<StaffAccessEntry>>() ?? [];
        var access = allowed.FirstOrDefault(entry =>
            string.Equals(entry.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase));
        if (access is null) return null;

        var tenantId = configuration.GetValue<string>("StaffAuth:TenantId") ?? "third-set-smiles";
        var name = ClaimValue(sourceClaims, ClaimTypes.Name, "name") ?? email;
        var providerId = source?.UserId ?? ClaimValue(sourceClaims, ClaimTypes.NameIdentifier, "sub") ?? email;
        var role = string.IsNullOrWhiteSpace(access.Role) ? "Staff" : access.Role.Trim();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, providerId),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, role),
            new Claim("tenant_id", tenantId),
            new Claim("TenantId", tenantId),
            new Claim("identity_provider", source?.IdentityProvider ?? "google")
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType));
    }

    private static string? ClaimValue(IEnumerable<ClientClaim> claims, params string[] types) =>
        claims.FirstOrDefault(claim => types.Any(type =>
            string.Equals(claim.Type, type, StringComparison.OrdinalIgnoreCase)))?.Value;

    private sealed class ClientPrincipal
    {
        [JsonPropertyName("auth_typ")]
        public string? AuthenticationType { get; init; }
        [JsonPropertyName("identity_provider")]
        public string? IdentityProvider { get; init; }
        [JsonPropertyName("user_id")]
        public string? UserId { get; init; }
        [JsonPropertyName("claims")]
        public List<ClientClaim> Claims { get; init; } = [];
    }

    private sealed class ClientClaim
    {
        [JsonPropertyName("typ")]
        public string Type { get; init; } = string.Empty;
        [JsonPropertyName("val")]
        public string Value { get; init; } = string.Empty;
    }
}

public sealed class StaffAccessMiddleware(RequestDelegate next, IConfiguration configuration, ILogger<StaffAccessMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Platform health probes reach the container directly, without the
        // Container Apps EasyAuth principal header. They must never be gated by
        // the staff allowlist, or every readiness/liveness probe would 403 and
        // the platform would take the replica out of service. Only bypass for the
        // headerless probe case — a request that DOES carry an EasyAuth principal
        // (an authenticated browser session) still goes through the allowlist, so
        // an authenticated-but-unauthorized account cannot reach /health.
        if (context.Request.Path.StartsWithSegments("/health") &&
            string.IsNullOrWhiteSpace(context.Request.Headers[ContainerAppsStaffIdentity.PrincipalHeader]))
        {
            await next(context);
            return;
        }

        if (!configuration.GetValue("StaffAuth:Enabled", false))
        {
            await next(context);
            return;
        }

        var principal = ContainerAppsStaffIdentity.Resolve(context, configuration);
        if (principal is null)
        {
            logger.LogWarning("Denied Portal request because the authenticated Google identity is not on the staff allowlist");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("This Google account is not authorized for CloudDentalOffice.");
            return;
        }

        context.User = principal;
        await next(context);
    }
}

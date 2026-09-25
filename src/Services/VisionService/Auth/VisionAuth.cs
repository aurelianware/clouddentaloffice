// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Business Source License 1.1. See LICENSE in the repository root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace VisionService.Auth;

/// <summary>
/// Two kinds of caller reach VisionService:
///   • Portal staff, with the Portal's bearer token (same Jwt:Key/Issuer/Audience; tenant from tenant_id).
///   • privaseeAI edge devices, with a per-tenant device key (X-CDO-Tenant-Id + X-CDO-Device-Key).
/// Each policy accepts exactly one kind, so a device key never reaches staff routes and vice versa.
/// </summary>
public static class VisionAuth
{
    public const string StaffPolicy = "VisionStaff";
    public const string DevicePolicy = "VisionDevice";
    public const string HubPolicy = "VisionHubClient";
    public const string TenantClaim = "tenant_id";
    public const string HubPath = "/hubs/vision";

    public static string Tenant(this ClaimsPrincipal user) =>
        user.FindFirstValue(TenantClaim) ?? throw new InvalidOperationException("Vision policies guarantee tenant_id.");

    // Staff roles are open-ended (Admin, Staff, Dentist, FrontDesk, ...), but the
    // Portal also signs tokens for patient-portal users with the same tenant_id.
    public static bool IsStaff(ClaimsPrincipal user) =>
        user.Identities.Any(identity => identity.IsAuthenticated) &&
        !user.Identities.Any(identity => identity.IsAuthenticated && identity.AuthenticationType == DeviceKeyAuthentication.SchemeName) &&
        !string.IsNullOrWhiteSpace(user.FindFirstValue(TenantClaim)) &&
        user.HasClaim(claim => claim.Type == ClaimTypes.Role && !string.IsNullOrWhiteSpace(claim.Value)) &&
        !user.IsInRole("Patient");

    // A device principal is authenticated by the device key alone, never mixed with a staff token.
    public static bool IsDevice(ClaimsPrincipal user)
    {
        var authenticated = user.Identities.Where(identity => identity.IsAuthenticated).ToList();
        return authenticated.Count == 1 &&
            authenticated[0].AuthenticationType == DeviceKeyAuthentication.SchemeName &&
            !string.IsNullOrWhiteSpace(user.FindFirstValue(TenantClaim));
    }

    /// <summary>
    /// Outside Development a missing or weak Jwt:Key fails startup. In Development a
    /// missing key becomes a per-process random key, so every staff request stays unauthorized.
    /// Device keys come from DeviceApi:Clients; with none configured every device request is rejected.
    /// </summary>
    public static void AddVisionAuthentication(this WebApplicationBuilder builder)
    {
        var configuredKey = builder.Configuration["Jwt:Key"];
        if (!builder.Environment.IsDevelopment() &&
            (string.IsNullOrWhiteSpace(configuredKey) || Encoding.UTF8.GetByteCount(configuredKey) < 32))
            throw new InvalidOperationException(
                "VisionService requires Jwt:Key (at least 32 bytes) outside Development. Supply a secret-backed Jwt__Key.");
        var signingKey = string.IsNullOrWhiteSpace(configuredKey)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configuredKey);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Issuer"]),
                    ValidIssuer = builder.Configuration["Jwt:Issuer"],
                    ValidateAudience = !string.IsNullOrWhiteSpace(builder.Configuration["Jwt:Audience"]),
                    ValidAudience = builder.Configuration["Jwt:Audience"],
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };
                // Browsers cannot set headers on WebSockets, so the hub also accepts ?access_token=.
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"].ToString();
                        if (string.IsNullOrEmpty(context.Token) && !string.IsNullOrEmpty(accessToken) &&
                            context.HttpContext.Request.Path.StartsWithSegments(HubPath))
                            context.Token = accessToken;
                        return Task.CompletedTask;
                    }
                };
            })
            .AddScheme<AuthenticationSchemeOptions, DeviceKeyAuthentication>(DeviceKeyAuthentication.SchemeName, _ => { });

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(StaffPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsStaff(context.User)));
            options.AddPolicy(DevicePolicy, policy => policy
                .AddAuthenticationSchemes(DeviceKeyAuthentication.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsDevice(context.User)));
            options.AddPolicy(HubPolicy, policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, DeviceKeyAuthentication.SchemeName)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsStaff(context.User) || IsDevice(context.User)));
        });
    }
}

/// <summary>
/// Per-tenant device key, compared in constant time (same pattern as PatientService's
/// InternalPatientApiAuth). Configured as DeviceApi:Clients:{i}:TenantId / ApiKey.
/// </summary>
public sealed class DeviceKeyAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceKey";
    public const string TenantHeader = "X-CDO-Tenant-Id";
    public const string KeyHeader = "X-CDO-Device-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var tenantId = Request.Headers[TenantHeader].ToString();
        var provided = Request.Headers[KeyHeader].ToString();
        if (string.IsNullOrEmpty(tenantId) && string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());
        if (!IsAuthorized(configuration, tenantId, provided))
            return Task.FromResult(AuthenticateResult.Fail("Invalid device credentials."));

        var identity = new ClaimsIdentity([new Claim(VisionAuth.TenantClaim, tenantId)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    public static bool IsAuthorized(IConfiguration configuration, string tenantId, string provided)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(provided)) return false;
        var client = configuration.GetSection("DeviceApi:Clients").GetChildren()
            .FirstOrDefault(x => string.Equals(x["TenantId"], tenantId, StringComparison.Ordinal));
        var expected = client?["ApiKey"];
        if (string.IsNullOrWhiteSpace(expected) || expected.Length < 32) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}

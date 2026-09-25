// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Business Source License 1.1. See LICENSE in the repository root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PrescriptionService.Auth;

/// <summary>
/// Staff bearer-token authentication shared with the Portal (same Jwt:Key/Issuer/Audience).
/// The tenant comes only from the token's tenant_id claim.
/// </summary>
public static class StaffAuth
{
    public const string Policy = "PrescriptionStaff";
    public const string TenantClaim = "tenant_id";

    public static string Tenant(this ClaimsPrincipal user) =>
        user.FindFirstValue(TenantClaim) ?? throw new InvalidOperationException("Staff policy guarantees tenant_id.");

    // Staff roles are open-ended (Admin, Staff, Dentist, FrontDesk, ...), but the
    // Portal also signs tokens for patient-portal users with the same tenant_id.
    // Require a tenant and at least one role, and keep Patient principals out.
    public static bool IsStaff(ClaimsPrincipal user) =>
        !string.IsNullOrWhiteSpace(user.FindFirstValue(TenantClaim)) &&
        user.HasClaim(claim => claim.Type == ClaimTypes.Role && !string.IsNullOrWhiteSpace(claim.Value)) &&
        !user.IsInRole("Patient");

    /// <summary>
    /// Outside Development a missing or weak Jwt:Key fails startup. In Development a
    /// missing key becomes a per-process random key, so every request stays unauthorized.
    /// </summary>
    public static void AddStaffAuthentication(this WebApplicationBuilder builder)
    {
        var configuredKey = builder.Configuration["Jwt:Key"];
        if (!builder.Environment.IsDevelopment() &&
            (string.IsNullOrWhiteSpace(configuredKey) || Encoding.UTF8.GetByteCount(configuredKey) < 32))
            throw new InvalidOperationException(
                "PrescriptionService requires Jwt:Key (at least 32 bytes) outside Development. Supply a secret-backed Jwt__Key.");
        var signingKey = string.IsNullOrWhiteSpace(configuredKey)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configuredKey);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
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
        });
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(Policy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => IsStaff(context.User))));
    }
}

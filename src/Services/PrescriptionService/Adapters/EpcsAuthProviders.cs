// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace PrescriptionService.Adapters;

/// <summary>
/// Abstraction for EPCS two-factor authentication.
/// Supports multiple providers: DoseSpot built-in, Imprivata, or external IDPs.
/// The active provider is determined by the prescriber's EpcsAuthMethod setting.
/// </summary>
public interface IEpcsAuthProvider
{
    string ProviderName { get; }

    /// <summary>Check if a provider has completed identity proofing.</summary>
    Task<bool> IsIdentityProofedAsync(string providerId, CancellationToken ct = default);

    /// <summary>Initiate two-factor authentication for EPCS signing.</summary>
    Task<EpcsAuthResult> AuthenticateAsync(EpcsAuthRequest request, CancellationToken ct = default);

    /// <summary>Verify a second-factor response (OTP, biometric, push notification).</summary>
    Task<EpcsVerifyResult> VerifyAsync(string challengeId, string response, CancellationToken ct = default);

    /// <summary>Get the identity proofing status and enrollment URL for a provider.</summary>
    Task<EpcsEnrollmentStatus> GetEnrollmentStatusAsync(string providerId, CancellationToken ct = default);
}

// ─── Auth DTOs ──────────────────────────────────────────────────────────────

public record EpcsAuthRequest
{
    public string ProviderId { get; init; } = string.Empty;
    public string PrescriptionId { get; init; } = string.Empty;
    public string AuthMethod { get; init; } = string.Empty; // push, otp, fingerprint, faceid
}

public record EpcsAuthResult
{
    public bool Success { get; init; }
    public string? ChallengeId { get; init; }
    public string? ChallengeType { get; init; } // push, otp, biometric
    public string? ErrorMessage { get; init; }
    public bool RequiresSecondFactor { get; init; } = true;
}

public record EpcsVerifyResult
{
    public bool Verified { get; init; }
    public string? SigningToken { get; init; } // opaque token to attach to the prescription send
    public string? ErrorMessage { get; init; }
    public int? RemainingAttempts { get; init; }
}

public record EpcsEnrollmentStatus
{
    public bool IdentityProofed { get; init; }
    public bool CredentialsEnrolled { get; init; }
    public string? EnrollmentUrl { get; init; }
    public List<string> AvailableAuthMethods { get; init; } = new();
    public DateTime? IdentityProofingDate { get; init; }
    public string? StatusMessage { get; init; }
}

// ─── Imprivata Implementation ───────────────────────────────────────────────

/// <summary>
/// Imprivata Confirm ID / Enterprise Access Management implementation.
/// Uses the Imprivata Confirm ID API for EPCS authentication workflows.
///
/// This is used in enterprise deployments (like PCHP) where the organization
/// already has Imprivata deployed for provider identity management.
///
/// Reference: https://docs.imprivata.com/confirmid/
/// </summary>
public class ImprivataEpcsAuthProvider : IEpcsAuthProvider
{
    private readonly HttpClient _httpClient;
    private readonly ImprivataOptions _options;
    private readonly ILogger<ImprivataEpcsAuthProvider> _logger;

    public string ProviderName => "Imprivata";

    public ImprivataEpcsAuthProvider(
        HttpClient httpClient,
        IOptions<ImprivataOptions> options,
        ILogger<ImprivataEpcsAuthProvider> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> IsIdentityProofedAsync(string providerId, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<ImprivataIdentityStatus>(
                $"/api/v1/users/{providerId}/identity-proofing/status", ct);

            return response?.IdentityProofed ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check Imprivata identity proofing for provider {ProviderId}", providerId);
            return false;
        }
    }

    public async Task<EpcsAuthResult> AuthenticateAsync(EpcsAuthRequest request, CancellationToken ct = default)
    {
        try
        {
            // Imprivata Confirm ID API - initiate MFA challenge
            var imprivataRequest = new
            {
                UserId = request.ProviderId,
                WorkflowType = "EPCS",
                TransactionId = request.PrescriptionId,
                PreferredMethod = MapAuthMethod(request.AuthMethod),
                PolicyId = _options.EpcsPolicyId
            };

            var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/authentication/challenges", imprivataRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Imprivata auth challenge failed: {Error}", error);
                return new EpcsAuthResult
                {
                    Success = false,
                    ErrorMessage = "Failed to initiate EPCS authentication"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<ImprivataChallengeResponse>(cancellationToken: ct);

            return new EpcsAuthResult
            {
                Success = true,
                ChallengeId = result?.ChallengeId,
                ChallengeType = result?.ChallengeType,
                RequiresSecondFactor = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Imprivata EPCS auth failed for provider {ProviderId}", request.ProviderId);
            return new EpcsAuthResult
            {
                Success = false,
                ErrorMessage = $"Authentication error: {ex.Message}"
            };
        }
    }

    public async Task<EpcsVerifyResult> VerifyAsync(string challengeId, string response, CancellationToken ct = default)
    {
        try
        {
            var verifyRequest = new
            {
                ChallengeId = challengeId,
                Response = response // OTP code, biometric token, or push approval
            };

            var httpResponse = await _httpClient.PostAsJsonAsync(
                "/api/v1/authentication/verify", verifyRequest, ct);

            var result = await httpResponse.Content.ReadFromJsonAsync<ImprivataVerifyResponse>(cancellationToken: ct);

            if (result == null || !result.Verified)
            {
                return new EpcsVerifyResult
                {
                    Verified = false,
                    ErrorMessage = result?.ErrorMessage ?? "Verification failed",
                    RemainingAttempts = result?.RemainingAttempts
                };
            }

            return new EpcsVerifyResult
            {
                Verified = true,
                SigningToken = result.SigningToken // Use this when sending the Rx
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Imprivata EPCS verify failed for challenge {ChallengeId}", challengeId);
            return new EpcsVerifyResult
            {
                Verified = false,
                ErrorMessage = $"Verification error: {ex.Message}"
            };
        }
    }

    public async Task<EpcsEnrollmentStatus> GetEnrollmentStatusAsync(string providerId, CancellationToken ct = default)
    {
        try
        {
            var status = await _httpClient.GetFromJsonAsync<ImprivataEnrollmentResponse>(
                $"/api/v1/users/{providerId}/enrollment/status", ct);

            return new EpcsEnrollmentStatus
            {
                IdentityProofed = status?.IdentityProofed ?? false,
                CredentialsEnrolled = status?.CredentialsEnrolled ?? false,
                EnrollmentUrl = status?.EnrollmentUrl,
                AvailableAuthMethods = status?.AvailableMethods ?? new(),
                IdentityProofingDate = status?.IdentityProofingDate,
                StatusMessage = status?.StatusMessage
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Imprivata enrollment status for {ProviderId}", providerId);
            return new EpcsEnrollmentStatus
            {
                StatusMessage = $"Unable to retrieve enrollment status: {ex.Message}"
            };
        }
    }

    private static string MapAuthMethod(string method) => method.ToLower() switch
    {
        "push" => "PUSH_NOTIFICATION",
        "otp" => "ONE_TIME_PASSWORD",
        "fingerprint" => "FINGERPRINT_BIOMETRIC",
        "faceid" => "FACIAL_BIOMETRIC",
        "handsfree" => "HANDS_FREE",
        _ => "PUSH_NOTIFICATION"
    };
}

// ─── DoseSpot Built-In EPCS Auth ────────────────────────────────────────────

/// <summary>
/// DoseSpot's built-in EPCS authentication.
/// Used for standalone practices (like 3rd Set Smiles) that don't have Imprivata.
/// DoseSpot handles identity proofing and 2FA internally via their platform.
/// </summary>
public class DoseSpotEpcsAuthProvider : IEpcsAuthProvider
{
    private readonly ILogger<DoseSpotEpcsAuthProvider> _logger;

    public string ProviderName => "DoseSpot";

    public DoseSpotEpcsAuthProvider(ILogger<DoseSpotEpcsAuthProvider> logger)
    {
        _logger = logger;
    }

    public Task<bool> IsIdentityProofedAsync(string providerId, CancellationToken ct = default)
    {
        // When using DoseSpot built-in EPCS, identity proofing status is managed
        // within DoseSpot's platform. We delegate to their SSO UI.
        _logger.LogInformation("EPCS identity proofing delegated to DoseSpot for provider {ProviderId}", providerId);
        return Task.FromResult(true); // DoseSpot handles this in their iFrame workflow
    }

    public Task<EpcsAuthResult> AuthenticateAsync(EpcsAuthRequest request, CancellationToken ct = default)
    {
        // DoseSpot handles EPCS 2FA within their embedded UI — no separate API call needed.
        // The prescriber completes 2FA within the DoseSpot iFrame when sending a controlled substance.
        return Task.FromResult(new EpcsAuthResult
        {
            Success = true,
            ChallengeType = "dosespot-embedded",
            RequiresSecondFactor = false // Handled within DoseSpot UI
        });
    }

    public Task<EpcsVerifyResult> VerifyAsync(string challengeId, string response, CancellationToken ct = default)
    {
        // Not applicable — DoseSpot handles verification internally
        return Task.FromResult(new EpcsVerifyResult { Verified = true });
    }

    public Task<EpcsEnrollmentStatus> GetEnrollmentStatusAsync(string providerId, CancellationToken ct = default)
    {
        return Task.FromResult(new EpcsEnrollmentStatus
        {
            IdentityProofed = true,
            CredentialsEnrolled = true,
            AvailableAuthMethods = new List<string> { "dosespot-embedded" },
            StatusMessage = "EPCS managed within DoseSpot platform"
        });
    }
}

// ─── Imprivata Configuration & Response Models ──────────────────────────────

public class ImprivataOptions
{
    public const string SectionName = "Imprivata";

    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string EpcsPolicyId { get; set; } = string.Empty;
    public string EnterpriseId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;
}

internal class ImprivataIdentityStatus
{
    public bool IdentityProofed { get; set; }
    public DateTime? ProofingDate { get; set; }
}

internal class ImprivataChallengeResponse
{
    public string? ChallengeId { get; set; }
    public string? ChallengeType { get; set; }
}

internal class ImprivataVerifyResponse
{
    public bool Verified { get; set; }
    public string? SigningToken { get; set; }
    public string? ErrorMessage { get; set; }
    public int? RemainingAttempts { get; set; }
}

internal class ImprivataEnrollmentResponse
{
    public bool IdentityProofed { get; set; }
    public bool CredentialsEnrolled { get; set; }
    public string? EnrollmentUrl { get; set; }
    public List<string>? AvailableMethods { get; set; }
    public DateTime? IdentityProofingDate { get; set; }
    public string? StatusMessage { get; set; }
}

// ─── EPCS Auth Provider Factory ─────────────────────────────────────────────

/// <summary>
/// Resolves the correct IEpcsAuthProvider based on the prescriber's configuration.
/// This enables per-provider auth method selection — a practice could have some
/// dentists using DoseSpot built-in and others using Imprivata.
/// </summary>
public class EpcsAuthProviderFactory
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<EpcsAuthProviderFactory> _logger;

    public EpcsAuthProviderFactory(
        IServiceProvider serviceProvider,
        ILogger<EpcsAuthProviderFactory> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public IEpcsAuthProvider GetProvider(string authMethod)
    {
        var provider = authMethod switch
        {
            "Imprivata" => _serviceProvider.GetRequiredService<ImprivataEpcsAuthProvider>() as IEpcsAuthProvider,
            "DoseSpotBuiltIn" or _ => _serviceProvider.GetRequiredService<DoseSpotEpcsAuthProvider>()
        };

        _logger.LogDebug("Resolved EPCS auth provider: {Provider} for method: {Method}",
            provider.ProviderName, authMethod);

        return provider;
    }
}

// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Prescriptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace PrescriptionService.Adapters;

/// <summary>
/// DoseSpot eRx gateway implementation.
///
/// DoseSpot integration uses two approaches:
/// 1. API calls (70+ endpoints) for programmatic operations
/// 2. JumpStart iFrame/SSO for embedded UI (prescribing workflow)
///
/// Authentication: DoseSpot uses HMAC-based SSO tokens for clinician context.
/// API calls use a clinic-level API key + secret.
///
/// Reference: https://developers.dosespot.com
/// </summary>
public class DoseSpotGateway : IErxGateway
{
    private readonly HttpClient _httpClient;
    private readonly DoseSpotOptions _options;
    private readonly ILogger<DoseSpotGateway> _logger;

    public DoseSpotGateway(
        HttpClient httpClient,
        IOptions<DoseSpotOptions> options,
        ILogger<DoseSpotGateway> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ErxSendResult> SendPrescriptionAsync(
        ErxPrescriptionPayload payload, CancellationToken ct = default)
    {
        try
        {
            // Step 1: Ensure patient exists in DoseSpot
            var patientId = await EnsurePatientAsync(payload, ct);

            // Step 2: Create the prescription in DoseSpot
            var doseSpotRx = new
            {
                PatientId = patientId,
                ClinicianId = payload.ErxClinicianId,
                DisplayName = payload.DrugName,
                RxCUI = payload.RxNormCode,
                NDC = payload.NdcCode,
                Strength = payload.Strength,
                Route = payload.DoseForm,
                Directions = payload.Directions,
                Quantity = payload.Quantity,
                DispenseUnitDescription = payload.QuantityUnit,
                Refills = payload.Refills,
                DaysSupply = payload.DaysSupply,
                NoSubstitutions = payload.DispenseAsWritten,
                PharmacyId = payload.PharmacyNcpdpId,
                PharmacyNotes = payload.PharmacyNotes
            };

            var response = await PostAuthenticatedAsync<DoseSpotCreateRxResponse>(
                "/api/v1/prescriptions", doseSpotRx, ct);

            if (response == null || !response.Success)
            {
                return new ErxSendResult
                {
                    Success = false,
                    ErrorMessage = response?.ErrorMessage ?? "Failed to create prescription in DoseSpot",
                    ErrorCode = response?.ErrorCode
                };
            }

            // Step 3: Send the prescription to the pharmacy
            var sendResponse = await PostAuthenticatedAsync<DoseSpotSendResponse>(
                $"/api/v1/prescriptions/{response.PrescriptionId}/send", new { }, ct);

            if (sendResponse == null || !sendResponse.Success)
            {
                // Check if it's an interaction alert requiring override
                if (sendResponse?.InteractionAlerts?.Count > 0)
                {
                    return new ErxSendResult
                    {
                        Success = false,
                        ErxPrescriptionId = response.PrescriptionId,
                        RequiresInteractionOverride = true,
                        InteractionAlerts = sendResponse.InteractionAlerts
                            .Select(a => new DrugInteractionAlertDto
                            {
                                Severity = a.Severity,
                                InteractionType = a.Type,
                                Drug1 = a.Drug1,
                                Drug2 = a.Drug2,
                                Description = a.Description,
                                ClinicalEffect = a.ClinicalEffect,
                                RequiresOverride = a.RequiresOverride
                            }).ToList()
                    };
                }

                return new ErxSendResult
                {
                    Success = false,
                    ErxPrescriptionId = response.PrescriptionId,
                    ErrorMessage = sendResponse?.ErrorMessage ?? "Failed to send prescription"
                };
            }

            return new ErxSendResult
            {
                Success = true,
                ErxPrescriptionId = response.PrescriptionId,
                SurescriptsMessageId = sendResponse.SurescriptsMessageId,
                ErxStatus = sendResponse.Status
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DoseSpot SendPrescription failed for clinician {ClinicianId}",
                payload.ErxClinicianId);
            return new ErxSendResult
            {
                Success = false,
                ErrorMessage = $"eRx gateway error: {ex.Message}"
            };
        }
    }

    public async Task<ErxCancelResult> CancelPrescriptionAsync(
        string erxPrescriptionId, string reason, CancellationToken ct = default)
    {
        var response = await PostAuthenticatedAsync<DoseSpotBaseResponse>(
            $"/api/v1/prescriptions/{erxPrescriptionId}/cancel",
            new { Reason = reason }, ct);

        return new ErxCancelResult
        {
            Success = response?.Success ?? false,
            ErrorMessage = response?.ErrorMessage
        };
    }

    public async Task<List<DrugInteractionAlertDto>> CheckInteractionsAsync(
        ErxInteractionCheckPayload payload, CancellationToken ct = default)
    {
        var response = await PostAuthenticatedAsync<DoseSpotInteractionResponse>(
            "/api/v1/interactions/check", new
            {
                PatientId = payload.ErxPatientId,
                NewDrugRxCUI = payload.RxNormCode,
                CurrentMedications = payload.CurrentMedicationRxNormCodes,
                Allergies = payload.AllergyRxNormCodes
            }, ct);

        if (response?.Interactions == null)
            return new List<DrugInteractionAlertDto>();

        return response.Interactions.Select(i => new DrugInteractionAlertDto
        {
            Severity = i.Severity,
            InteractionType = i.Type,
            Drug1 = i.Drug1,
            Drug2 = i.Drug2,
            Description = i.Description,
            ClinicalEffect = i.ClinicalEffect,
            RequiresOverride = i.RequiresOverride
        }).ToList();
    }

    public async Task<List<MedicationHistoryDto>> GetMedicationHistoryAsync(
        string erxPatientId, CancellationToken ct = default)
    {
        var response = await GetAuthenticatedAsync<DoseSpotMedHistoryResponse>(
            $"/api/v1/patients/{erxPatientId}/medication-history", ct);

        if (response?.Medications == null)
            return new List<MedicationHistoryDto>();

        return response.Medications.Select(m => new MedicationHistoryDto
        {
            DrugName = m.DisplayName,
            RxNormCode = m.RxCUI,
            Strength = m.Strength ?? string.Empty,
            Directions = m.Directions,
            Prescriber = m.PrescriberName,
            Pharmacy = m.PharmacyName,
            LastFillDate = m.LastFillDate,
            Source = m.Source,
            IsActive = m.IsActive
        }).ToList();
    }

    public async Task<List<PharmacyDto>> SearchPharmaciesAsync(
        PharmacySearchRequest request, CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(request.Name))
            queryParams.Add($"name={Uri.EscapeDataString(request.Name)}");
        if (!string.IsNullOrEmpty(request.ZipCode))
            queryParams.Add($"zipCode={request.ZipCode}");
        if (request.Latitude.HasValue && request.Longitude.HasValue)
        {
            queryParams.Add($"latitude={request.Latitude}");
            queryParams.Add($"longitude={request.Longitude}");
        }
        queryParams.Add($"radius={request.RadiusMiles}");

        var query = string.Join("&", queryParams);
        var response = await GetAuthenticatedAsync<DoseSpotPharmacySearchResponse>(
            $"/api/v1/pharmacies?{query}", ct);

        if (response?.Pharmacies == null)
            return new List<PharmacyDto>();

        return response.Pharmacies.Select(p => new PharmacyDto
        {
            NcpdpId = p.NcpdpId,
            Name = p.StoreName,
            Address = p.Address1 + (string.IsNullOrEmpty(p.Address2) ? "" : $" {p.Address2}"),
            City = p.City,
            State = p.State,
            ZipCode = p.ZipCode,
            Phone = p.Phone,
            Fax = p.Fax,
            DistanceMiles = p.Distance,
            AcceptsEpcs = p.AcceptsEpcs,
            Is24Hour = p.Is24Hour,
            PharmacyType = MapPharmacyType(p.PharmacyType)
        }).ToList();
    }

    public async Task<PrescriptionBenefitDto> CheckBenefitsAsync(
        string erxPatientId, string rxNormCode, string? pharmacyNcpdpId,
        CancellationToken ct = default)
    {
        var response = await PostAuthenticatedAsync<DoseSpotRtpbResponse>(
            "/api/v1/benefits/check", new
            {
                PatientId = erxPatientId,
                RxCUI = rxNormCode,
                PharmacyId = pharmacyNcpdpId
            }, ct);

        if (response == null)
            return new PrescriptionBenefitDto { DrugName = rxNormCode };

        return new PrescriptionBenefitDto
        {
            DrugName = response.DrugName ?? rxNormCode,
            PatientCopay = response.Copay,
            PatientCoinsurance = response.Coinsurance,
            FormularyStatus = response.FormularyStatus,
            TierLevel = response.TierLevel,
            PriorAuthRequired = response.PriorAuthRequired,
            Alternatives = response.Alternatives?.Select(a => new FormularyAlternativeDto
            {
                DrugName = a.DrugName,
                RxNormCode = a.RxCUI,
                EstimatedCopay = a.EstimatedCopay,
                TierLevel = a.TierLevel
            }).ToList() ?? new()
        };
    }

    public async Task<ErxPrescriberRegistrationResult> RegisterPrescriberAsync(
        ErxPrescriberPayload payload, CancellationToken ct = default)
    {
        var response = await PostAuthenticatedAsync<DoseSpotRegisterClinicianResponse>(
            "/api/v1/clinicians", new
            {
                FirstName = payload.FirstName,
                LastName = payload.LastName,
                NPI = payload.Npi,
                DEANumber = payload.DeaNumber,
                StateLicenseNumber = payload.StateLicenseNumber,
                StateLicenseState = payload.StateLicenseState,
                PracticeAddress = payload.ClinicAddress,
                PracticeCity = payload.ClinicCity,
                PracticeState = payload.ClinicState,
                PracticeZip = payload.ClinicZip,
                PracticePhone = payload.ClinicPhone,
                PracticeFax = payload.ClinicFax,
                EnableEPCS = payload.EnableEpcs
            }, ct);

        return new ErxPrescriberRegistrationResult
        {
            Success = response?.Success ?? false,
            ClinicianId = response?.ClinicianId,
            ErrorMessage = response?.ErrorMessage,
            RequiresIdentityProofing = response?.RequiresIdentityProofing ?? payload.EnableEpcs
        };
    }

    public Task<string> GetSsoUrlAsync(
        string clinicianId, string? patientId, CancellationToken ct = default)
    {
        // DoseSpot SSO uses HMAC-SHA256 to generate a signed URL
        // The clinician is authenticated via the token, and the UI loads in context
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var phrase = $"{_options.ClinicId}{clinicianId}{timestamp}";
        var token = ComputeHmac(phrase, _options.ClinicKey);

        var ssoUrl = $"{_options.BaseUrl}/LoginSingleSignOn.aspx" +
                     $"?SingleSignOnClinicId={_options.ClinicId}" +
                     $"&SingleSignOnUserId={clinicianId}" +
                     $"&SingleSignOnPhraseLength={phrase.Length}" +
                     $"&SingleSignOnCode={token}" +
                     $"&SingleSignOnUserIdVerify={clinicianId}";

        if (!string.IsNullOrEmpty(patientId))
            ssoUrl += $"&PatientId={patientId}";

        return Task.FromResult(ssoUrl);
    }

    public async Task<ErxRefillResult> RespondToRefillAsync(
        string erxPrescriptionId, bool approved, string? denialReason, int? newRefillCount,
        CancellationToken ct = default)
    {
        var response = await PostAuthenticatedAsync<DoseSpotBaseResponse>(
            $"/api/v1/prescriptions/{erxPrescriptionId}/refill-response", new
            {
                Approved = approved,
                DenialReason = denialReason,
                NewRefillCount = newRefillCount
            }, ct);

        return new ErxRefillResult
        {
            Success = response?.Success ?? false,
            ErrorMessage = response?.ErrorMessage
        };
    }

    public async Task<List<ErxNotification>> GetNotificationsAsync(
        string clinicianId, CancellationToken ct = default)
    {
        var response = await GetAuthenticatedAsync<DoseSpotNotificationsResponse>(
            $"/api/v1/clinicians/{clinicianId}/notifications", ct);

        if (response?.Notifications == null)
            return new List<ErxNotification>();

        return response.Notifications.Select(n => new ErxNotification
        {
            NotificationId = n.Id,
            Type = n.Type,
            PrescriptionId = n.PrescriptionId,
            Message = n.Message,
            Timestamp = n.Timestamp,
            Acknowledged = n.Acknowledged
        }).ToList();
    }

    // ─── Private Helpers ────────────────────────────────────────────────────

    private async Task<string> EnsurePatientAsync(ErxPrescriptionPayload payload, CancellationToken ct)
    {
        // Check if patient already exists in DoseSpot
        if (!string.IsNullOrEmpty(payload.ErxPatientId))
            return payload.ErxPatientId;

        // Create patient in DoseSpot
        var response = await PostAuthenticatedAsync<DoseSpotCreatePatientResponse>(
            "/api/v1/patients", new
            {
                FirstName = payload.PatientFirstName,
                LastName = payload.PatientLastName,
                DateOfBirth = payload.PatientDateOfBirth.ToString("yyyy-MM-dd"),
                Gender = payload.PatientGender,
                Address1 = payload.PatientAddress,
                City = payload.PatientCity,
                State = payload.PatientState,
                ZipCode = payload.PatientZip,
                Phone = payload.PatientPhone
            }, ct);

        return response?.PatientId ?? throw new InvalidOperationException("Failed to create patient in DoseSpot");
    }

    private async Task<T?> GetAuthenticatedAsync<T>(string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private async Task<T?> PostAuthenticatedAsync<T>(string path, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body)
        };
        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("X-DoseSpot-ClinicId", _options.ClinicId);
        request.Headers.Add("X-DoseSpot-ClinicKey", _options.ClinicKey);
    }

    private static string ComputeHmac(string message, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(message));
        return Convert.ToBase64String(hash);
    }

    private static PharmacyType MapPharmacyType(string? type) => type?.ToLower() switch
    {
        "retail" => PharmacyType.Retail,
        "mailorder" or "mail-order" => PharmacyType.MailOrder,
        "specialty" => PharmacyType.Specialty,
        "compounding" => PharmacyType.Compounding,
        "hospital" or "institutional" => PharmacyType.Hospital,
        _ => PharmacyType.Retail
    };
}

// ─── DoseSpot Configuration ─────────────────────────────────────────────────

public class DoseSpotOptions
{
    public const string SectionName = "DoseSpot";

    public string BaseUrl { get; set; } = "https://my.dosespot.com";
    public string ClinicId { get; set; } = string.Empty;
    public string ClinicKey { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://my.dosespot.com";
    public bool UseSandbox { get; set; } = true;
    public string SandboxBaseUrl { get; set; } = "https://my.staging.dosespot.com";
}

// ─── DoseSpot API Response Models (internal) ────────────────────────────────

internal class DoseSpotBaseResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

internal class DoseSpotCreateRxResponse : DoseSpotBaseResponse
{
    public string? PrescriptionId { get; set; }
}

internal class DoseSpotSendResponse : DoseSpotBaseResponse
{
    public string? SurescriptsMessageId { get; set; }
    public string? Status { get; set; }
    public List<DoseSpotInteractionAlert>? InteractionAlerts { get; set; }
}

internal class DoseSpotInteractionAlert
{
    public string Severity { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Drug1 { get; set; } = string.Empty;
    public string? Drug2 { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ClinicalEffect { get; set; }
    public bool RequiresOverride { get; set; }
}

internal class DoseSpotInteractionResponse : DoseSpotBaseResponse
{
    public List<DoseSpotInteractionAlert>? Interactions { get; set; }
}

internal class DoseSpotMedHistoryResponse : DoseSpotBaseResponse
{
    public List<DoseSpotMedHistoryEntry>? Medications { get; set; }
}

internal class DoseSpotMedHistoryEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public string? RxCUI { get; set; }
    public string? Strength { get; set; }
    public string? Directions { get; set; }
    public string? PrescriberName { get; set; }
    public string? PharmacyName { get; set; }
    public DateTime? LastFillDate { get; set; }
    public string? Source { get; set; }
    public bool IsActive { get; set; }
}

internal class DoseSpotPharmacySearchResponse : DoseSpotBaseResponse
{
    public List<DoseSpotPharmacy>? Pharmacies { get; set; }
}

internal class DoseSpotPharmacy
{
    public string NcpdpId { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ZipCode { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public double? Distance { get; set; }
    public bool AcceptsEpcs { get; set; }
    public bool Is24Hour { get; set; }
    public string? PharmacyType { get; set; }
}

internal class DoseSpotRtpbResponse : DoseSpotBaseResponse
{
    public string? DrugName { get; set; }
    public decimal? Copay { get; set; }
    public decimal? Coinsurance { get; set; }
    public string? FormularyStatus { get; set; }
    public string? TierLevel { get; set; }
    public bool PriorAuthRequired { get; set; }
    public List<DoseSpotFormularyAlt>? Alternatives { get; set; }
}

internal class DoseSpotFormularyAlt
{
    public string DrugName { get; set; } = string.Empty;
    public string? RxCUI { get; set; }
    public decimal? EstimatedCopay { get; set; }
    public string? TierLevel { get; set; }
}

internal class DoseSpotCreatePatientResponse : DoseSpotBaseResponse
{
    public string? PatientId { get; set; }
}

internal class DoseSpotRegisterClinicianResponse : DoseSpotBaseResponse
{
    public string? ClinicianId { get; set; }
    public bool RequiresIdentityProofing { get; set; }
}

internal class DoseSpotNotificationsResponse : DoseSpotBaseResponse
{
    public List<DoseSpotNotification>? Notifications { get; set; }
}

internal class DoseSpotNotification
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? PrescriptionId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool Acknowledged { get; set; }
}

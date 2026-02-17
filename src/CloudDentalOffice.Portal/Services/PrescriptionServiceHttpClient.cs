// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Prescriptions;
using System.Net.Http.Json;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// HTTP client for the PrescriptionService microservice.
/// Calls go through the API Gateway (YARP) at /api/prescriptions/*.
/// 
/// Registered in DI when Microservices.UseMicroservices = true.
/// When false, the Portal uses a local/direct implementation instead.
/// </summary>
public class PrescriptionServiceHttpClient : IPrescriptionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PrescriptionServiceHttpClient> _logger;

    public PrescriptionServiceHttpClient(HttpClient httpClient, ILogger<PrescriptionServiceHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ── Prescriptions ──────────────────────────────────────────────────────

    public async Task<PrescriptionDto> CreatePrescriptionAsync(
        CreatePrescriptionRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/prescriptions", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PrescriptionDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize prescription");
    }

    public async Task<PrescriptionDto?> GetPrescriptionAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PrescriptionDto>($"/api/prescriptions/{id}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<PrescriptionDto>> GetPatientPrescriptionsAsync(
        Guid patientId, bool includeExpired = false, CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<PrescriptionDto>>(
            $"/api/prescriptions/patient/{patientId}?includeExpired={includeExpired}", ct)
            ?? new List<PrescriptionDto>();
    }

    public async Task<PrescriptionDto> SendPrescriptionAsync(
        SendPrescriptionRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/prescriptions/{request.PrescriptionId}/send", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PrescriptionDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize prescription");
    }

    public async Task<PrescriptionDto> CancelPrescriptionAsync(
        Guid id, string reason, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/prescriptions/{id}/cancel", reason, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PrescriptionDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize prescription");
    }

    public async Task<RefillRequestResponse> RespondToRefillRequestAsync(
        Guid prescriptionId, RefillRequestResponse refillResponse, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/prescriptions/{prescriptionId}/refill-response", refillResponse, ct);
        response.EnsureSuccessStatusCode();
        return refillResponse;
    }

    // ── Safety Checks ──────────────────────────────────────────────────────

    public async Task<List<DrugInteractionAlertDto>> CheckInteractionsAsync(
        Guid patientId, string rxNormCode, CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<DrugInteractionAlertDto>>(
            $"/api/prescriptions/check-interactions?patientId={patientId}&rxNormCode={rxNormCode}", ct)
            ?? new List<DrugInteractionAlertDto>();
    }

    // ── Medication History ─────────────────────────────────────────────────

    public async Task<List<MedicationHistoryDto>> GetMedicationHistoryAsync(
        GetMedicationHistoryRequest request, CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<MedicationHistoryDto>>(
            $"/api/prescriptions/patient/{request.PatientId}/medication-history?includeInactive={request.IncludeInactive}", ct)
            ?? new List<MedicationHistoryDto>();
    }

    // ── Allergies ──────────────────────────────────────────────────────────

    public async Task<List<PatientAllergyDto>> GetPatientAllergiesAsync(
        Guid patientId, CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<PatientAllergyDto>>(
            $"/api/patients/{patientId}/allergies", ct)
            ?? new List<PatientAllergyDto>();
    }

    public async Task<PatientAllergyDto> AddPatientAllergyAsync(
        Guid patientId, PatientAllergyDto allergy, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/patients/{patientId}/allergies", allergy, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PatientAllergyDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize allergy");
    }

    // ── Benefits (RTPB / Da Vinci Formulary) ───────────────────────────────

    public async Task<PrescriptionBenefitDto> CheckBenefitsAsync(
        CheckBenefitsRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/prescriptions/check-benefits", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PrescriptionBenefitDto>(cancellationToken: ct)
            ?? new PrescriptionBenefitDto();
    }

    // ── Pharmacy Search ────────────────────────────────────────────────────

    public async Task<List<PharmacyDto>> SearchPharmaciesAsync(
        PharmacySearchRequest request, CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(request.Name))
            queryParams.Add($"name={Uri.EscapeDataString(request.Name)}");
        if (!string.IsNullOrEmpty(request.ZipCode))
            queryParams.Add($"zipCode={request.ZipCode}");
        if (request.Latitude.HasValue)
            queryParams.Add($"lat={request.Latitude}");
        if (request.Longitude.HasValue)
            queryParams.Add($"lng={request.Longitude}");
        queryParams.Add($"radius={request.RadiusMiles}");

        var query = string.Join("&", queryParams);
        return await _httpClient.GetFromJsonAsync<List<PharmacyDto>>(
            $"/api/pharmacies/search?{query}", ct)
            ?? new List<PharmacyDto>();
    }

    // ── Prescriber Management ──────────────────────────────────────────────

    public async Task<PrescriberDto> RegisterPrescriberAsync(
        RegisterPrescriberRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/prescribers", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PrescriberDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize prescriber");
    }

    public async Task<PrescriberDto?> GetPrescriberAsync(Guid providerId, CancellationToken ct = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<PrescriberDto>(
                $"/api/prescribers/{providerId}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── DoseSpot SSO ───────────────────────────────────────────────────────

    public async Task<string> GetDoseSpotSsoUrlAsync(
        Guid providerId, Guid patientId, CancellationToken ct = default)
    {
        var result = await _httpClient.GetFromJsonAsync<SsoUrlResponse>(
            $"/api/prescriptions/sso-url?providerId={providerId}&patientId={patientId}", ct);

        return result?.SsoUrl ?? throw new InvalidOperationException("Failed to get DoseSpot SSO URL");
    }

    private record SsoUrlResponse(string SsoUrl);
}

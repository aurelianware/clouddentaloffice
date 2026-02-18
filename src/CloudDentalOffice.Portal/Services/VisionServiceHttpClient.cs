// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Vision;
using System.Net.Http.Json;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// HTTP client for the VisionService microservice.
/// Calls go through the API Gateway (YARP) at /api/vision/*.
/// </summary>
public class VisionServiceHttpClient : IVisionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<VisionServiceHttpClient> _logger;

    public VisionServiceHttpClient(HttpClient httpClient, ILogger<VisionServiceHttpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    // ── Devices ────────────────────────────────────────────────────────────

    public async Task<VisionDeviceDto> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/devices", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VisionDeviceDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize device");
    }

    public async Task<VisionDeviceDto?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisionDeviceDto>($"/api/vision/devices/{deviceId}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<List<VisionDeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        return await _httpClient.GetFromJsonAsync<List<VisionDeviceDto>>("/api/vision/devices", ct)
            ?? new List<VisionDeviceDto>();
    }

    public async Task<VisionDeviceDto> UpdateDeviceStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/vision/devices/{deviceId}/status", status, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VisionDeviceDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize device");
    }

    // ── Detection Ingestion ────────────────────────────────────────────────

    public async Task<VisionEventDto> IngestDetectionsAsync(IngestDetectionRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/ingest", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<VisionEventDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize vision event");
    }

    // ── Events ─────────────────────────────────────────────────────────────

    public async Task<List<VisionEventDto>> GetEventsAsync(
        DateTime? from = null, DateTime? to = null, CameraLocation? location = null,
        AlertSeverity? minSeverity = null, int limit = 50, CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (from.HasValue) queryParams.Add($"from={from.Value:O}");
        if (to.HasValue) queryParams.Add($"to={to.Value:O}");
        if (location.HasValue) queryParams.Add($"location={location.Value}");
        if (minSeverity.HasValue) queryParams.Add($"minSeverity={minSeverity.Value}");
        queryParams.Add($"limit={limit}");

        var query = string.Join("&", queryParams);
        return await _httpClient.GetFromJsonAsync<List<VisionEventDto>>($"/api/vision/events?{query}", ct)
            ?? new List<VisionEventDto>();
    }

    public async Task<VisionEventDto?> GetEventAsync(Guid eventId, CancellationToken ct = default)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<VisionEventDto>($"/api/vision/events/{eventId}", ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── Insurance Card OCR ─────────────────────────────────────────────────

    public async Task<InsuranceCardScanDto> ScanInsuranceCardAsync(ScanInsuranceCardRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/insurance-scan", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InsuranceCardScanDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize insurance scan");
    }

    public async Task<List<InsuranceCardScanDto>> GetInsuranceScansAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = patientId.HasValue ? $"?patientId={patientId.Value}&limit={limit}" : $"?limit={limit}";
        return await _httpClient.GetFromJsonAsync<List<InsuranceCardScanDto>>($"/api/vision/insurance-scans{query}", ct)
            ?? new List<InsuranceCardScanDto>();
    }

    // ── Consent Recording ──────────────────────────────────────────────────

    public async Task<ConsentRecordingDto> StartConsentRecordingAsync(StartConsentRecordingRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/consent/start", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConsentRecordingDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize consent recording");
    }

    public async Task<ConsentRecordingDto> CompleteConsentRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync($"/api/vision/consent/{recordingId}/complete", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConsentRecordingDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize consent recording");
    }

    public async Task<List<ConsentRecordingDto>> GetConsentRecordingsAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = patientId.HasValue ? $"?patientId={patientId.Value}&limit={limit}" : $"?limit={limit}";
        return await _httpClient.GetFromJsonAsync<List<ConsentRecordingDto>>($"/api/vision/consent{query}", ct)
            ?? new List<ConsentRecordingDto>();
    }

    // ── Narcotics Cabinet ──────────────────────────────────────────────────

    public async Task<CabinetAccessLogDto> LogCabinetAccessAsync(LogCabinetAccessRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/cabinet-access", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CabinetAccessLogDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize cabinet access log");
    }

    public async Task<List<CabinetAccessLogDto>> GetCabinetAccessLogsAsync(
        DateTime? from = null, DateTime? to = null, AlertSeverity? minSeverity = null,
        int limit = 50, CancellationToken ct = default)
    {
        var queryParams = new List<string>();
        if (from.HasValue) queryParams.Add($"from={from.Value:O}");
        if (to.HasValue) queryParams.Add($"to={to.Value:O}");
        if (minSeverity.HasValue) queryParams.Add($"minSeverity={minSeverity.Value}");
        queryParams.Add($"limit={limit}");

        var query = string.Join("&", queryParams);
        return await _httpClient.GetFromJsonAsync<List<CabinetAccessLogDto>>($"/api/vision/cabinet-access?{query}", ct)
            ?? new List<CabinetAccessLogDto>();
    }

    // ── Clinical Notes ─────────────────────────────────────────────────────

    public async Task<ClinicalNoteDraftDto> GenerateClinicalNoteAsync(GenerateClinicalNoteRequest request, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/vision/clinical-notes/generate", request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClinicalNoteDraftDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize clinical note draft");
    }

    public async Task<ClinicalNoteDraftDto> ApproveClinicalNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync($"/api/vision/clinical-notes/{noteId}/approve", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClinicalNoteDraftDto>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Failed to deserialize clinical note draft");
    }

    public async Task<List<ClinicalNoteDraftDto>> GetClinicalNotesAsync(Guid? appointmentId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = appointmentId.HasValue ? $"?appointmentId={appointmentId.Value}&limit={limit}" : $"?limit={limit}";
        return await _httpClient.GetFromJsonAsync<List<ClinicalNoteDraftDto>>($"/api/vision/clinical-notes{query}", ct)
            ?? new List<ClinicalNoteDraftDto>();
    }
}

// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Vision;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Portal HTTP client for VisionService.
/// Routes through API Gateway at /api/vision/*.
/// </summary>
public class VisionServiceHttpClient : IVisionService
{
    private readonly HttpClient _http;

    public VisionServiceHttpClient(HttpClient httpClient)
    {
        _http = httpClient;
    }

    // ── Devices ─────────────────────────────────────────────────────────────

    public async Task<VisionDeviceDto> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/devices", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VisionDeviceDto>(ct))!;
    }

    public async Task<VisionDeviceDto?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<VisionDeviceDto>($"/api/vision/devices/{deviceId}", ct);
    }

    public async Task<List<VisionDeviceDto>> GetDevicesAsync(CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<List<VisionDeviceDto>>("/api/vision/devices", ct)
            ?? new();
    }

    public async Task<VisionDeviceDto> UpdateDeviceStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/vision/devices/{deviceId}/status", status, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VisionDeviceDto>(ct))!;
    }

    // ── Detection Ingestion ─────────────────────────────────────────────────

    public async Task<VisionEventDto> IngestDetectionsAsync(IngestDetectionRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/detections", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<VisionEventDto>(ct))!;
    }

    // ── Events ──────────────────────────────────────────────────────────────

    public async Task<List<VisionEventDto>> GetEventsAsync(DateTime? from = null, DateTime? to = null,
        CameraLocation? location = null, AlertSeverity? minSeverity = null,
        int limit = 50, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("from", from?.ToString("O")),
            ("to", to?.ToString("O")),
            ("location", location?.ToString()),
            ("minSeverity", minSeverity?.ToString()),
            ("limit", limit.ToString()));

        return await _http.GetFromJsonAsync<List<VisionEventDto>>($"/api/vision/events{query}", ct)
            ?? new();
    }

    public async Task<VisionEventDto?> GetEventAsync(Guid eventId, CancellationToken ct = default)
    {
        return await _http.GetFromJsonAsync<VisionEventDto>($"/api/vision/events/{eventId}", ct);
    }

    // ── Insurance Card OCR ──────────────────────────────────────────────────

    public async Task<InsuranceCardScanDto> ScanInsuranceCardAsync(ScanInsuranceCardRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/insurance/scan", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InsuranceCardScanDto>(ct))!;
    }

    public async Task<List<InsuranceCardScanDto>> GetInsuranceScansAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("patientId", patientId?.ToString()),
            ("limit", limit.ToString()));

        return await _http.GetFromJsonAsync<List<InsuranceCardScanDto>>($"/api/vision/insurance/scans{query}", ct)
            ?? new();
    }

    // ── Consent Recording ───────────────────────────────────────────────────

    public async Task<ConsentRecordingDto> StartConsentRecordingAsync(StartConsentRecordingRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/consent/start", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConsentRecordingDto>(ct))!;
    }

    public async Task<ConsentRecordingDto> CompleteConsentRecordingAsync(Guid recordingId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/vision/consent/{recordingId}/complete", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConsentRecordingDto>(ct))!;
    }

    public async Task<List<ConsentRecordingDto>> GetConsentRecordingsAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("patientId", patientId?.ToString()),
            ("limit", limit.ToString()));

        return await _http.GetFromJsonAsync<List<ConsentRecordingDto>>($"/api/vision/consent{query}", ct)
            ?? new();
    }

    // ── Narcotics Cabinet ───────────────────────────────────────────────────

    public async Task<CabinetAccessLogDto> LogCabinetAccessAsync(LogCabinetAccessRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/cabinet/access", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CabinetAccessLogDto>(ct))!;
    }

    public async Task<List<CabinetAccessLogDto>> GetCabinetAccessLogsAsync(DateTime? from = null, DateTime? to = null,
        AlertSeverity? minSeverity = null, int limit = 50, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("from", from?.ToString("O")),
            ("to", to?.ToString("O")),
            ("minSeverity", minSeverity?.ToString()),
            ("limit", limit.ToString()));

        return await _http.GetFromJsonAsync<List<CabinetAccessLogDto>>($"/api/vision/cabinet/access-log{query}", ct)
            ?? new();
    }

    // ── Clinical Notes ──────────────────────────────────────────────────────

    public async Task<ClinicalNoteDraftDto> GenerateClinicalNoteAsync(GenerateClinicalNoteRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/vision/clinical-notes/generate", request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClinicalNoteDraftDto>(ct))!;
    }

    public async Task<ClinicalNoteDraftDto> ApproveClinicalNoteAsync(Guid noteId, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"/api/vision/clinical-notes/{noteId}/approve", null, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ClinicalNoteDraftDto>(ct))!;
    }

    public async Task<List<ClinicalNoteDraftDto>> GetClinicalNotesAsync(Guid? appointmentId = null, int limit = 20, CancellationToken ct = default)
    {
        var query = BuildQuery(
            ("appointmentId", appointmentId?.ToString()),
            ("limit", limit.ToString()));

        return await _http.GetFromJsonAsync<List<ClinicalNoteDraftDto>>($"/api/vision/clinical-notes{query}", ct)
            ?? new();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildQuery(params (string key, string? value)[] parameters)
    {
        var pairs = parameters
            .Where(p => p.value != null)
            .Select(p => $"{p.key}={Uri.EscapeDataString(p.value!)}");

        var query = string.Join("&", pairs);
        return string.IsNullOrEmpty(query) ? "" : $"?{query}";
    }
}

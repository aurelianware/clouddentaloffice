// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Vision;

namespace VisionService.Adapters;

// ════════════════════════════════════════════════════════════════════════════
//  CONTEXT CORRELATION ENGINE
//
//  The bridge between privaseeAI vision events and CDO clinical context.
//  When a detection event arrives, the correlation engine:
//  1. Looks up the device → location mapping (which operatory/room)
//  2. Queries CDO services (via API Gateway) for active context at that location
//  3. Enriches the event with patient, appointment, provider info
//  4. Evaluates alert rules and triggers notifications
// ════════════════════════════════════════════════════════════════════════════

public interface IContextCorrelationEngine
{
    /// <summary>
    /// Correlate a vision event with CDO clinical context.
    /// </summary>
    Task<CorrelationResult> CorrelateAsync(Guid deviceId, CameraLocation location,
        List<DetectionPayload> detections, DateTime timestamp);

    /// <summary>
    /// Check narcotics cabinet access against CDO PrescriptionService and SchedulingService.
    /// </summary>
    Task<CabinetAccessCorrelation> CorrelateCabinetAccessAsync(
        string? badgeId, DateTime accessTime, Guid tenantId);
}

public class CorrelationResult
{
    public Guid? PatientId { get; set; }
    public string? PatientName { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? CdtCode { get; set; }
    public string? ProcedureName { get; set; }
    public AlertSeverity? AlertSeverity { get; set; }
    public AlertType? AlertType { get; set; }
    public string? AlertMessage { get; set; }
}

public class CabinetAccessCorrelation
{
    public Guid? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public bool IdentityVerified { get; set; }

    // PrescriptionService cross-reference
    public Guid? ActivePrescriptionId { get; set; }
    public string? PrescriptionDrugName { get; set; }
    public bool HasActiveControlledSubstanceRx { get; set; }

    // SchedulingService cross-reference
    public Guid? ActiveAppointmentId { get; set; }
    public string? AppointmentPatientName { get; set; }
    public bool HasActiveSedationAppointment { get; set; }

    // Compliance evaluation
    public bool IsAfterHours { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;
    public string? AlertMessage { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  IMPLEMENTATION (calls CDO services via API Gateway)
// ════════════════════════════════════════════════════════════════════════════

public class ContextCorrelationEngine : IContextCorrelationEngine
{
    private readonly HttpClient _httpClient; // Configured to point at API Gateway :5200
    private readonly ILogger<ContextCorrelationEngine> _logger;
    private readonly PracticeHoursConfig _hoursConfig;

    public ContextCorrelationEngine(HttpClient httpClient,
        ILogger<ContextCorrelationEngine> logger,
        PracticeHoursConfig hoursConfig)
    {
        _httpClient = httpClient;
        _logger = logger;
        _hoursConfig = hoursConfig;
    }

    public async Task<CorrelationResult> CorrelateAsync(
        Guid deviceId, CameraLocation location,
        List<DetectionPayload> detections, DateTime timestamp)
    {
        var result = new CorrelationResult();

        if (location == CameraLocation.Operatory)
        {
            // Query SchedulingService for the active appointment in this operatory
            try
            {
                var appointment = await GetActiveAppointmentAsync(deviceId, timestamp);
                if (appointment != null)
                {
                    result.PatientId = appointment.PatientId;
                    result.PatientName = appointment.PatientName;
                    result.AppointmentId = appointment.AppointmentId;
                    result.ProviderId = appointment.ProviderId;
                    result.ProviderName = appointment.ProviderName;
                    result.CdtCode = appointment.PrimaryCdtCode;
                    result.ProcedureName = appointment.ProcedureName;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to correlate operatory context for device {DeviceId}", deviceId);
            }
        }
        else if (location == CameraLocation.NarcoticsCabinet)
        {
            // Person at cabinet without badge scan → medium alert
            if (detections.Any(d => d.ClassName.Equals("person", StringComparison.OrdinalIgnoreCase)))
            {
                result.AlertSeverity = AlertSeverity.Medium;
                result.AlertType = AlertType.UnauthorizedCabinetAccess;
                result.AlertMessage = "Person detected at narcotics cabinet without badge scan";
            }
        }
        else if (IsAfterHours(timestamp))
        {
            // Any person detection after hours
            if (detections.Any(d => d.ClassName.Equals("person", StringComparison.OrdinalIgnoreCase)))
            {
                result.AlertSeverity = AlertSeverity.High;
                result.AlertType = AlertType.AfterHoursMotion;
                result.AlertMessage = $"Person detected at {location} during after-hours ({timestamp:HH:mm})";
            }
        }

        return result;
    }

    public async Task<CabinetAccessCorrelation> CorrelateCabinetAccessAsync(
        string? badgeId, DateTime accessTime, Guid tenantId)
    {
        var result = new CabinetAccessCorrelation
        {
            IsAfterHours = IsAfterHours(accessTime)
        };

        // Resolve provider from badge ID
        if (!string.IsNullOrEmpty(badgeId))
        {
            try
            {
                var provider = await ResolveProviderByBadgeAsync(badgeId);
                if (provider != null)
                {
                    result.ProviderId = provider.ProviderId;
                    result.ProviderName = provider.ProviderName;
                    result.IdentityVerified = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve provider for badge {BadgeId}", badgeId);
            }
        }

        // Cross-reference with PrescriptionService — active controlled substance prescriptions
        if (result.ProviderId.HasValue)
        {
            try
            {
                var rxCheck = await CheckActiveControlledSubstanceRxAsync(result.ProviderId.Value);
                result.HasActiveControlledSubstanceRx = rxCheck.HasActive;
                result.ActivePrescriptionId = rxCheck.PrescriptionId;
                result.PrescriptionDrugName = rxCheck.DrugName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check active Rx for provider {ProviderId}", result.ProviderId);
            }
        }

        // Cross-reference with SchedulingService — active sedation appointment
        try
        {
            var sedationCheck = await CheckActiveSedationAppointmentAsync(accessTime);
            result.HasActiveSedationAppointment = sedationCheck.HasActive;
            result.ActiveAppointmentId = sedationCheck.AppointmentId;
            result.AppointmentPatientName = sedationCheck.PatientName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check active sedation appointments");
        }

        // Evaluate compliance
        EvaluateCabinetCompliance(result);

        return result;
    }

    // ── CDO Service Calls (via API Gateway) ─────────────────────────────────

    private async Task<AppointmentContext?> GetActiveAppointmentAsync(Guid deviceId, DateTime timestamp)
    {
        // GET /api/scheduling/appointments/active?operatoryDeviceId={deviceId}&at={timestamp}
        var response = await _httpClient.GetAsync(
            $"/api/scheduling/appointments/active?operatoryDeviceId={deviceId}&at={timestamp:O}");

        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<AppointmentContext>();
    }

    private async Task<ProviderContext?> ResolveProviderByBadgeAsync(string badgeId)
    {
        // GET /api/auth/providers/by-badge/{badgeId}
        var response = await _httpClient.GetAsync($"/api/auth/providers/by-badge/{badgeId}");

        if (!response.IsSuccessStatusCode) return null;

        return await response.Content.ReadFromJsonAsync<ProviderContext>();
    }

    private async Task<ControlledSubstanceCheck> CheckActiveControlledSubstanceRxAsync(Guid providerId)
    {
        // GET /api/prescriptions/active-controlled?prescriberId={providerId}
        var response = await _httpClient.GetAsync(
            $"/api/prescriptions/active-controlled?prescriberId={providerId}");

        if (!response.IsSuccessStatusCode)
            return new ControlledSubstanceCheck();

        return await response.Content.ReadFromJsonAsync<ControlledSubstanceCheck>()
            ?? new ControlledSubstanceCheck();
    }

    private async Task<SedationAppointmentCheck> CheckActiveSedationAppointmentAsync(DateTime at)
    {
        // GET /api/scheduling/appointments/active-sedation?at={timestamp}
        var response = await _httpClient.GetAsync(
            $"/api/scheduling/appointments/active-sedation?at={at:O}");

        if (!response.IsSuccessStatusCode)
            return new SedationAppointmentCheck();

        return await response.Content.ReadFromJsonAsync<SedationAppointmentCheck>()
            ?? new SedationAppointmentCheck();
    }

    // ── Compliance Evaluation ───────────────────────────────────────────────

    private static void EvaluateCabinetCompliance(CabinetAccessCorrelation result)
    {
        var alerts = new List<string>();

        if (!result.IdentityVerified)
        {
            result.Severity = AlertSeverity.Critical;
            alerts.Add("Unidentified person accessed narcotics cabinet");
        }
        else if (result.IsAfterHours)
        {
            result.Severity = AlertSeverity.High;
            alerts.Add("After-hours narcotics cabinet access");
        }
        else if (!result.HasActiveControlledSubstanceRx && !result.HasActiveSedationAppointment)
        {
            result.Severity = AlertSeverity.High;
            alerts.Add("Cabinet access without active controlled substance prescription or sedation appointment");
        }
        else if (result.HasActiveControlledSubstanceRx || result.HasActiveSedationAppointment)
        {
            result.Severity = AlertSeverity.Info;
        }

        result.AlertMessage = alerts.Any() ? string.Join("; ", alerts) : null;
    }

    private bool IsAfterHours(DateTime timestamp)
    {
        var localTime = timestamp.TimeOfDay;
        return localTime < _hoursConfig.OpenTime || localTime > _hoursConfig.CloseTime
            || timestamp.DayOfWeek == DayOfWeek.Sunday
            || (timestamp.DayOfWeek == DayOfWeek.Saturday && !_hoursConfig.OpenSaturday);
    }

    // ── Internal DTOs for CDO API responses ─────────────────────────────────

    private record AppointmentContext(Guid AppointmentId, Guid PatientId, string PatientName,
        Guid ProviderId, string ProviderName, string? PrimaryCdtCode, string? ProcedureName);

    private record ProviderContext(Guid ProviderId, string ProviderName);

    private record ControlledSubstanceCheck(bool HasActive = false, Guid? PrescriptionId = null, string? DrugName = null);

    private record SedationAppointmentCheck(bool HasActive = false, Guid? AppointmentId = null, string? PatientName = null);
}

// ════════════════════════════════════════════════════════════════════════════
//  PRACTICE HOURS CONFIG
// ════════════════════════════════════════════════════════════════════════════

public class PracticeHoursConfig
{
    public const string SectionName = "PracticeHours";
    public TimeSpan OpenTime { get; set; } = new(8, 0, 0);   // 8:00 AM
    public TimeSpan CloseTime { get; set; } = new(17, 0, 0);  // 5:00 PM
    public bool OpenSaturday { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  MOCK CORRELATION ENGINE (development without running CDO services)
// ════════════════════════════════════════════════════════════════════════════

public class MockContextCorrelationEngine : IContextCorrelationEngine
{
    public Task<CorrelationResult> CorrelateAsync(
        Guid deviceId, CameraLocation location,
        List<DetectionPayload> detections, DateTime timestamp)
    {
        var result = new CorrelationResult();

        if (location == CameraLocation.Operatory)
        {
            result.PatientId = Guid.Parse("00000000-0000-0000-0000-000000000099");
            result.PatientName = "Doe, John";
            result.AppointmentId = Guid.NewGuid();
            result.ProviderId = Guid.Parse("00000000-0000-0000-0000-000000000001");
            result.ProviderName = "Dr. Smith";
            result.CdtCode = "D7210";
            result.ProcedureName = "Surgical Extraction";
        }

        return Task.FromResult(result);
    }

    public Task<CabinetAccessCorrelation> CorrelateCabinetAccessAsync(
        string? badgeId, DateTime accessTime, Guid tenantId)
    {
        return Task.FromResult(new CabinetAccessCorrelation
        {
            ProviderId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ProviderName = "Dr. Smith",
            IdentityVerified = !string.IsNullOrEmpty(badgeId),
            HasActiveControlledSubstanceRx = true,
            ActivePrescriptionId = Guid.NewGuid(),
            PrescriptionDrugName = "Hydrocodone/Acetaminophen 5/325mg",
            HasActiveSedationAppointment = true,
            ActiveAppointmentId = Guid.NewGuid(),
            AppointmentPatientName = "Jane Doe",
            IsAfterHours = accessTime.TimeOfDay > new TimeSpan(17, 0, 0),
            Severity = string.IsNullOrEmpty(badgeId) ? AlertSeverity.Critical : AlertSeverity.Info
        });
    }
}

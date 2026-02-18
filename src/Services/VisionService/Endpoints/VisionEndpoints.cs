// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;
using CloudDentalOffice.Contracts.Vision;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionService.Adapters;
using VisionService.Domain;
using VisionService.Hubs;

namespace VisionService.Endpoints;

public static class VisionEndpoints
{
    public static void MapVisionEndpoints(this WebApplication app)
    {
        // ── Devices ─────────────────────────────────────────────────────────
        var devices = app.MapGroup("/api/vision/devices").WithTags("Devices");

        devices.MapPost("/", RegisterDevice);
        devices.MapGet("/", GetDevices);
        devices.MapGet("/{id:guid}", GetDevice);
        devices.MapPut("/{id:guid}/status", UpdateDeviceStatus);
        devices.MapPost("/{id:guid}/heartbeat", DeviceHeartbeat);

        // ── Detection Ingestion ─────────────────────────────────────────────
        var detections = app.MapGroup("/api/vision/detections").WithTags("Detections");

        detections.MapPost("/", IngestDetections);

        // ── Vision Events ───────────────────────────────────────────────────
        var events = app.MapGroup("/api/vision/events").WithTags("Events");

        events.MapGet("/", GetEvents);
        events.MapGet("/{id:guid}", GetEvent);

        // ── Insurance Card OCR ──────────────────────────────────────────────
        var insurance = app.MapGroup("/api/vision/insurance").WithTags("Insurance Card OCR");

        insurance.MapPost("/scan", ScanInsuranceCard);
        insurance.MapGet("/scans", GetInsuranceScans);

        // ── Consent Recordings ──────────────────────────────────────────────
        var consent = app.MapGroup("/api/vision/consent").WithTags("Consent Recording");

        consent.MapPost("/start", StartConsentRecording);
        consent.MapPost("/{id:guid}/complete", CompleteConsentRecording);
        consent.MapGet("/", GetConsentRecordings);

        // ── Narcotics Cabinet ───────────────────────────────────────────────
        var cabinet = app.MapGroup("/api/vision/cabinet").WithTags("Narcotics Cabinet");

        cabinet.MapPost("/access", LogCabinetAccess);
        cabinet.MapGet("/access-log", GetCabinetAccessLogs);

        // ── Clinical Notes ──────────────────────────────────────────────────
        var notes = app.MapGroup("/api/vision/clinical-notes").WithTags("Clinical Notes");

        notes.MapPost("/generate", GenerateClinicalNote);
        notes.MapPost("/{id:guid}/approve", ApproveClinicalNote);
        notes.MapGet("/", GetClinicalNotes);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DEVICE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> RegisterDevice(
        RegisterDeviceRequest request, VisionDbContext db, IHubContext<VisionHub> hub)
    {
        var device = new VisionDevice
        {
            Name = request.Name,
            Type = request.Type,
            Location = request.Location,
            LocationName = request.LocationName,
            OperatoryId = request.OperatoryId,
            IpAddress = request.IpAddress,
            MacAddress = request.MacAddress,
            SupportsOcr = request.SupportsOcr,
            Status = DeviceStatus.Online,
            TenantId = Guid.Empty // TODO: from auth context
        };

        db.Devices.Add(device);
        await db.SaveChangesAsync();

        await hub.BroadcastDeviceStatus(device.TenantId, device.Id, DeviceStatus.Online);

        return Results.Created($"/api/vision/devices/{device.Id}", MapDeviceDto(device));
    }

    private static async Task<IResult> GetDevices(VisionDbContext db)
    {
        var devices = await db.Devices
            .OrderBy(d => d.Location)
            .ThenBy(d => d.Name)
            .ToListAsync();

        return Results.Ok(devices.Select(MapDeviceDto).ToList());
    }

    private static async Task<IResult> GetDevice(Guid id, VisionDbContext db)
    {
        var device = await db.Devices.FindAsync(id);
        return device == null ? Results.NotFound() : Results.Ok(MapDeviceDto(device));
    }

    private static async Task<IResult> UpdateDeviceStatus(
        Guid id, DeviceStatus status, VisionDbContext db, IHubContext<VisionHub> hub)
    {
        var device = await db.Devices.FindAsync(id);
        if (device == null) return Results.NotFound();

        device.Status = status;
        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await hub.BroadcastDeviceStatus(device.TenantId, device.Id, status);

        return Results.Ok(MapDeviceDto(device));
    }

    private static async Task<IResult> DeviceHeartbeat(
        Guid id, VisionDbContext db, IHubContext<VisionHub> hub)
    {
        var device = await db.Devices.FindAsync(id);
        if (device == null) return Results.NotFound();

        device.LastHeartbeat = DateTime.UtcNow;
        device.Status = DeviceStatus.Online;
        device.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DETECTION INGESTION
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> IngestDetections(
        IngestDetectionRequest request,
        VisionDbContext db,
        IContextCorrelationEngine correlator,
        IHubContext<VisionHub> hub)
    {
        var device = await db.Devices.FindAsync(request.DeviceId);
        if (device == null) return Results.NotFound("Device not registered");

        // Correlate with CDO context
        var correlation = await correlator.CorrelateAsync(
            device.Id, device.Location, request.Detections, request.Timestamp);

        // Create vision event
        var visionEvent = new VisionEvent
        {
            EventType = VisionEventType.Detection,
            Timestamp = request.Timestamp,
            DeviceId = device.Id,
            PatientId = correlation.PatientId,
            AppointmentId = correlation.AppointmentId,
            ProviderId = correlation.ProviderId,
            DetectionsJson = JsonSerializer.Serialize(request.Detections),
            AlertSeverity = correlation.AlertSeverity,
            AlertType = correlation.AlertType,
            AlertMessage = correlation.AlertMessage,
            TenantId = device.TenantId,
            ProcessedByCorrelation = true
        };

        // TODO: Save image to Azure Blob if ImageBase64 provided
        // visionEvent.ImageUrl = await blobService.UploadAsync(...)

        db.Events.Add(visionEvent);
        await db.SaveChangesAsync();

        // Broadcast to Portal in real-time
        var dto = MapEventDto(visionEvent, device);
        await hub.BroadcastVisionEvent(dto);

        return Results.Ok(dto);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  VISION EVENTS
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> GetEvents(
        DateTime? from, DateTime? to, CameraLocation? location,
        AlertSeverity? minSeverity, int limit,
        VisionDbContext db)
    {
        var query = db.Events
            .Include(e => e.Device)
            .AsQueryable();

        if (from.HasValue) query = query.Where(e => e.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(e => e.Timestamp <= to.Value);
        if (location.HasValue) query = query.Where(e => e.Device.Location == location.Value);
        if (minSeverity.HasValue) query = query.Where(e => e.AlertSeverity >= minSeverity.Value);

        var events = await query
            .OrderByDescending(e => e.Timestamp)
            .Take(limit > 0 ? limit : 50)
            .ToListAsync();

        return Results.Ok(events.Select(e => MapEventDto(e, e.Device)).ToList());
    }

    private static async Task<IResult> GetEvent(Guid id, VisionDbContext db)
    {
        var ev = await db.Events.Include(e => e.Device).FirstOrDefaultAsync(e => e.Id == id);
        return ev == null ? Results.NotFound() : Results.Ok(MapEventDto(ev, ev.Device));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  INSURANCE CARD OCR
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> ScanInsuranceCard(
        ScanInsuranceCardRequest request,
        IOcrGateway ocr,
        VisionDbContext db,
        IHubContext<VisionHub> hub)
    {
        var imageBytes = Convert.FromBase64String(request.ImageBase64);
        byte[]? backBytes = request.BackImageBase64 != null
            ? Convert.FromBase64String(request.BackImageBase64) : null;

        var ocrResult = await ocr.ExtractInsuranceCardAsync(imageBytes, backBytes);

        var scan = new InsuranceCardScan
        {
            DeviceId = request.DeviceId,
            OcrConfidence = ocrResult.Confidence,
            PayerName = ocrResult.PayerName,
            PayerId = ocrResult.PayerId,
            MemberId = ocrResult.MemberId,
            GroupNumber = ocrResult.GroupNumber,
            SubscriberName = ocrResult.SubscriberName,
            PlanName = ocrResult.PlanName,
            RxBin = ocrResult.RxBin,
            RxPcn = ocrResult.RxPcn,
            RxGroup = ocrResult.RxGroup,
            CopayAmount = ocrResult.CopayAmount,
            PhoneNumber = ocrResult.PhoneNumber,
            MatchedPatientId = request.PatientId,
            TenantId = Guid.Empty // TODO: from auth context
        };

        // TODO: Trigger eligibility check via CDO EligibilityService :5104
        // if (scan.PayerId != null && scan.MemberId != null)
        //     scan.EligibilityCheckTriggered = true;

        db.InsuranceCardScans.Add(scan);
        await db.SaveChangesAsync();

        var dto = MapInsuranceScanDto(scan);
        await hub.BroadcastInsuranceScan(dto);

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetInsuranceScans(
        Guid? patientId, int limit, VisionDbContext db)
    {
        var query = db.InsuranceCardScans.AsQueryable();
        if (patientId.HasValue)
            query = query.Where(s => s.MatchedPatientId == patientId.Value);

        var scans = await query
            .OrderByDescending(s => s.Timestamp)
            .Take(limit > 0 ? limit : 20)
            .ToListAsync();

        return Results.Ok(scans.Select(MapInsuranceScanDto).ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CONSENT RECORDINGS
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> StartConsentRecording(
        StartConsentRecordingRequest request, VisionDbContext db)
    {
        var recording = new ConsentRecording
        {
            DeviceId = request.DeviceId,
            PatientId = request.PatientId,
            ProviderId = request.ProviderId,
            AppointmentId = request.AppointmentId,
            ConsentType = request.ConsentType,
            Status = ConsentStatus.InProgress,
            TenantId = Guid.Empty // TODO: from auth context
        };

        db.ConsentRecordings.Add(recording);
        await db.SaveChangesAsync();

        return Results.Created($"/api/vision/consent/{recording.Id}", MapConsentDto(recording));
    }

    private static async Task<IResult> CompleteConsentRecording(Guid id, VisionDbContext db)
    {
        var recording = await db.ConsentRecordings.FindAsync(id);
        if (recording == null) return Results.NotFound();

        recording.Status = ConsentStatus.Captured;
        recording.CompletedAt = DateTime.UtcNow;
        recording.DurationSeconds = (int)(DateTime.UtcNow - recording.StartedAt).TotalSeconds;

        await db.SaveChangesAsync();

        return Results.Ok(MapConsentDto(recording));
    }

    private static async Task<IResult> GetConsentRecordings(
        Guid? patientId, int limit, VisionDbContext db)
    {
        var query = db.ConsentRecordings.AsQueryable();
        if (patientId.HasValue)
            query = query.Where(c => c.PatientId == patientId.Value);

        var recordings = await query
            .OrderByDescending(c => c.StartedAt)
            .Take(limit > 0 ? limit : 20)
            .ToListAsync();

        return Results.Ok(recordings.Select(MapConsentDto).ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  NARCOTICS CABINET
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> LogCabinetAccess(
        LogCabinetAccessRequest request,
        IContextCorrelationEngine correlator,
        VisionDbContext db,
        IHubContext<VisionHub> hub)
    {
        var device = await db.Devices.FindAsync(request.DeviceId);
        if (device == null) return Results.NotFound("Device not registered");

        // Cross-reference with CDO services
        var correlation = await correlator.CorrelateCabinetAccessAsync(
            request.BadgeId, request.DoorOpenedAt, device.TenantId);

        var log = new CabinetAccessLog
        {
            DeviceId = device.Id,
            ProviderId = correlation.ProviderId,
            ProviderName = correlation.ProviderName,
            BadgeId = request.BadgeId,
            IdentityVerified = correlation.IdentityVerified,
            DoorOpenedAt = request.DoorOpenedAt,
            DoorClosedAt = request.DoorClosedAt,
            DurationSeconds = request.DoorClosedAt.HasValue
                ? (int)(request.DoorClosedAt.Value - request.DoorOpenedAt).TotalSeconds
                : 0,
            ActivePrescriptionId = correlation.ActivePrescriptionId,
            PrescriptionDrugName = correlation.PrescriptionDrugName,
            ActiveAppointmentId = correlation.ActiveAppointmentId,
            AppointmentPatientName = correlation.AppointmentPatientName,
            HasActiveRx = correlation.HasActiveControlledSubstanceRx,
            HasActiveAppointment = correlation.HasActiveSedationAppointment,
            TwoPersonWitnessVerified = false, // TODO: detect two persons in frame
            IsAfterHours = correlation.IsAfterHours,
            Severity = correlation.Severity,
            AlertMessage = correlation.AlertMessage,
            TenantId = device.TenantId
        };

        db.CabinetAccessLogs.Add(log);
        await db.SaveChangesAsync();

        var dto = MapCabinetLogDto(log);
        await hub.BroadcastCabinetAlert(dto);

        return Results.Ok(dto);
    }

    private static async Task<IResult> GetCabinetAccessLogs(
        DateTime? from, DateTime? to, AlertSeverity? minSeverity, int limit,
        VisionDbContext db)
    {
        var query = db.CabinetAccessLogs.AsQueryable();

        if (from.HasValue) query = query.Where(l => l.Timestamp >= from.Value);
        if (to.HasValue) query = query.Where(l => l.Timestamp <= to.Value);
        if (minSeverity.HasValue) query = query.Where(l => l.Severity >= minSeverity.Value);

        var logs = await query
            .OrderByDescending(l => l.Timestamp)
            .Take(limit > 0 ? limit : 50)
            .ToListAsync();

        return Results.Ok(logs.Select(MapCabinetLogDto).ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  CLINICAL NOTES
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<IResult> GenerateClinicalNote(
        GenerateClinicalNoteRequest request, VisionDbContext db)
    {
        // Gather procedure observations from vision events for this appointment
        var observations = await db.Events
            .Where(e => e.AppointmentId == request.AppointmentId
                && e.EventType == VisionEventType.Detection)
            .OrderBy(e => e.Timestamp)
            .ToListAsync();

        var note = new ClinicalNoteDraft
        {
            AppointmentId = request.AppointmentId,
            PatientId = request.PatientId,
            ProviderId = request.ProviderId,
            CdtCode = request.CdtCode,
            ObservationStart = observations.FirstOrDefault()?.Timestamp ?? DateTime.UtcNow,
            ObservationEnd = observations.LastOrDefault()?.Timestamp ?? DateTime.UtcNow,
            ObservationsJson = JsonSerializer.Serialize(observations.Select(o => new
            {
                o.Timestamp,
                o.DetectionsJson
            })),
            // TODO: Call Azure OpenAI to generate note text from observations
            DraftNoteText = GenerateMockNote(request.CdtCode, observations.Count),
            TenantId = Guid.Empty // TODO: from auth context
        };

        db.ClinicalNoteDrafts.Add(note);
        await db.SaveChangesAsync();

        return Results.Ok(MapClinicalNoteDto(note));
    }

    private static async Task<IResult> ApproveClinicalNote(Guid id, VisionDbContext db)
    {
        var note = await db.ClinicalNoteDrafts.FindAsync(id);
        if (note == null) return Results.NotFound();

        note.ReviewedByProvider = true;
        note.ApprovedByProvider = true;
        note.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return Results.Ok(MapClinicalNoteDto(note));
    }

    private static async Task<IResult> GetClinicalNotes(
        Guid? appointmentId, int limit, VisionDbContext db)
    {
        var query = db.ClinicalNoteDrafts.AsQueryable();
        if (appointmentId.HasValue)
            query = query.Where(n => n.AppointmentId == appointmentId.Value);

        var notes = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(limit > 0 ? limit : 20)
            .ToListAsync();

        return Results.Ok(notes.Select(MapClinicalNoteDto).ToList());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  MAPPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static VisionDeviceDto MapDeviceDto(VisionDevice d) => new()
    {
        Id = d.Id, Name = d.Name, Type = d.Type, Status = d.Status,
        Location = d.Location, LocationName = d.LocationName,
        OperatoryId = d.OperatoryId, IpAddress = d.IpAddress,
        MacAddress = d.MacAddress, FirmwareVersion = d.FirmwareVersion,
        HasCamera = d.HasCamera, CanRecord = d.CanRecord,
        CanDetectObjects = d.CanDetectObjects, SupportsOcr = d.SupportsOcr,
        ActiveModelName = d.ActiveModelName, ActiveModelVersion = d.ActiveModelVersion,
        LastHeartbeat = d.LastHeartbeat, RegisteredAt = d.RegisteredAt,
        TenantId = d.TenantId
    };

    private static VisionEventDto MapEventDto(VisionEvent e, VisionDevice d) => new()
    {
        Id = e.Id, EventType = e.EventType, Timestamp = e.Timestamp,
        DeviceId = e.DeviceId, DeviceName = d.Name, Location = d.Location,
        PatientId = e.PatientId, AppointmentId = e.AppointmentId,
        ProviderId = e.ProviderId,
        AlertSeverity = e.AlertSeverity, AlertType = e.AlertType,
        AlertMessage = e.AlertMessage,
        ImageUrl = e.ImageUrl, VideoUrl = e.VideoUrl,
        TenantId = e.TenantId,
        Detections = DeserializeDetections(e.DetectionsJson)
    };

    private static InsuranceCardScanDto MapInsuranceScanDto(InsuranceCardScan s) => new()
    {
        Id = s.Id, DeviceId = s.DeviceId, Timestamp = s.Timestamp,
        OcrConfidence = s.OcrConfidence,
        PayerName = s.PayerName, PayerId = s.PayerId,
        MemberId = s.MemberId, GroupNumber = s.GroupNumber,
        SubscriberName = s.SubscriberName, PlanName = s.PlanName,
        RxBin = s.RxBin, RxPcn = s.RxPcn, RxGroup = s.RxGroup,
        CopayAmount = s.CopayAmount, PhoneNumber = s.PhoneNumber,
        CardImageUrl = s.CardImageUrl, CardImageBackUrl = s.CardImageBackUrl,
        MatchedPatientId = s.MatchedPatientId,
        EligibilityCheckTriggered = s.EligibilityCheckTriggered,
        EligibilityResult = s.EligibilityResult,
        TenantId = s.TenantId
    };

    private static ConsentRecordingDto MapConsentDto(ConsentRecording c) => new()
    {
        Id = c.Id, PatientId = c.PatientId, ProviderId = c.ProviderId,
        AppointmentId = c.AppointmentId, Status = c.Status,
        ConsentType = c.ConsentType, StartedAt = c.StartedAt,
        CompletedAt = c.CompletedAt,
        Duration = c.DurationSeconds.HasValue ? TimeSpan.FromSeconds(c.DurationSeconds.Value) : null,
        VideoUrl = c.VideoUrl, ThumbnailUrl = c.ThumbnailUrl,
        PatientDetected = c.PatientDetected, ConsentFormDetected = c.ConsentFormDetected,
        ProviderDetected = c.ProviderDetected,
        TenantId = c.TenantId
    };

    private static CabinetAccessLogDto MapCabinetLogDto(CabinetAccessLog l) => new()
    {
        Id = l.Id, Timestamp = l.Timestamp, DeviceId = l.DeviceId,
        ProviderId = l.ProviderId, ProviderName = l.ProviderName,
        BadgeId = l.BadgeId, IdentityVerified = l.IdentityVerified,
        Duration = TimeSpan.FromSeconds(l.DurationSeconds),
        ImageUrl = l.ImageUrl, VideoUrl = l.VideoUrl,
        ActivePrescriptionId = l.ActivePrescriptionId,
        PrescriptionDrugName = l.PrescriptionDrugName,
        ActiveAppointmentId = l.ActiveAppointmentId,
        AppointmentPatientName = l.AppointmentPatientName,
        HasActiveRx = l.HasActiveRx, HasActiveAppointment = l.HasActiveAppointment,
        TwoPersonWitnessVerified = l.TwoPersonWitnessVerified,
        IsAfterHours = l.IsAfterHours,
        Severity = l.Severity, AlertMessage = l.AlertMessage,
        TenantId = l.TenantId
    };

    private static ClinicalNoteDraftDto MapClinicalNoteDto(ClinicalNoteDraft n) => new()
    {
        Id = n.Id, AppointmentId = n.AppointmentId,
        PatientId = n.PatientId, ProviderId = n.ProviderId,
        CdtCode = n.CdtCode, ProcedureName = n.ProcedureName,
        ObservationStart = n.ObservationStart, ObservationEnd = n.ObservationEnd,
        DraftNoteText = n.DraftNoteText, StructuredNoteJson = n.StructuredNoteJson,
        ReviewedByProvider = n.ReviewedByProvider,
        ApprovedByProvider = n.ApprovedByProvider, ApprovedAt = n.ApprovedAt,
        TenantId = n.TenantId
    };

    private static List<DetectionEventDto> DeserializeDetections(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            var payloads = JsonSerializer.Deserialize<List<DetectionPayload>>(json);
            return payloads?.Select(p => new DetectionEventDto
            {
                DetectionClass = Enum.TryParse<DetectionClass>(p.ClassName, true, out var cls) ? cls : DetectionClass.Unknown,
                Confidence = p.Confidence,
                BoundingBox = new BoundingBoxDto { X = p.BboxX, Y = p.BboxY, Width = p.BboxW, Height = p.BboxH }
            }).ToList() ?? new();
        }
        catch { return new(); }
    }

    private static string GenerateMockNote(string? cdtCode, int observationCount) => cdtCode switch
    {
        "D7210" => $"Surgical extraction performed. {observationCount} procedural steps observed via operatory camera. " +
                   "Anesthesia administered (lidocaine 2% with 1:100,000 epinephrine). Mucoperiosteal flap raised. " +
                   "Bone removal with handpiece. Tooth sectioned and elevated. Socket irrigated with sterile saline. " +
                   "Primary closure with 4-0 chromic gut sutures. Hemostasis achieved. Post-op instructions provided.",
        "D2740" => $"Crown preparation observed. {observationCount} steps captured. Tooth prepared with chamfer margin. " +
                   "Impression taken with PVS material. Provisional crown fabricated and cemented with TempBond.",
        _ => $"Procedure observed with {observationCount} detection events recorded. " +
             "Clinical observations captured via operatory camera system. Provider review required."
    };
}

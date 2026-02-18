// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace CloudDentalOffice.Contracts.Vision;

// ════════════════════════════════════════════════════════════════════════════
//  ENUMS
// ════════════════════════════════════════════════════════════════════════════

public enum DeviceType
{
    IpCamera,
    Tablet,
    MobileIos,
    MobileAndroid,
    RaspberryPi,
    UsbWebcam,
    DesktopBrowser
}

public enum DeviceStatus
{
    Online,
    Offline,
    Error,
    Maintenance,
    Provisioning
}

public enum CameraLocation
{
    Operatory,
    WaitingRoom,
    FrontDesk,
    NarcoticsCabinet,
    Hallway,
    ExteriorEntrance,
    ExteriorParking,
    Lab,
    Sterilization,
    Other
}

public enum DetectionClass
{
    // Generic (COCO-SSD)
    Person,
    Document,
    CellPhone,
    Backpack,
    Handbag,

    // Dental instruments (custom YOLOv8 model)
    Handpiece,
    DentalMirror,
    Explorer,
    ForcepsExtraction,
    Elevator,
    ScalerCurette,
    SyringeAnesthetic,
    SyringeIrrigation,
    SutureKit,
    CottonRoll,
    GauzePacket,
    ImprintTray,
    CrownBridge,
    DentalDam,

    // Insurance / Document
    InsuranceCard,
    ConsentForm,
    IdDocument,
    PrescriptionPad,

    // Narcotics cabinet
    CabinetDoorOpen,
    CabinetDoorClosed,
    BadgeScan,
    MedicationVial,
    NarcoticsSafe,

    // General security
    Vehicle,
    Animal,
    Unknown
}

public enum AlertSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public enum AlertType
{
    // Narcotics
    UnauthorizedCabinetAccess,
    AfterHoursCabinetAccess,
    CabinetDoorLeftOpen,
    NoActiveRxForAccess,
    FrequencyAnomaly,
    InventoryMismatch,
    MissingTwoPersonWitness,

    // Security
    AfterHoursMotion,
    UnrecognizedPerson,
    TailgatingDetected,
    DoorPropped,

    // Clinical
    InstrumentCountMismatch,
    ProcedureStepMissed,
    ConsentNotRecorded,

    // Intake
    InsuranceCardCaptured,
    EligibilityAutoVerified,

    // System
    DeviceOffline,
    DeviceError,
    StorageLimitApproaching
}

public enum VisionEventType
{
    Detection,
    CabinetAccess,
    ConsentCapture,
    InsuranceCardScan,
    ProcedureObservation,
    MotionAlert,
    DeviceHeartbeat,
    SystemAlert
}

public enum ConsentStatus
{
    NotStarted,
    InProgress,
    Captured,
    Verified,
    Expired,
    Declined
}

// ════════════════════════════════════════════════════════════════════════════
//  DTOs
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// A camera or edge device registered with the VisionService.
/// </summary>
public class VisionDeviceDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DeviceType Type { get; set; }
    public DeviceStatus Status { get; set; }
    public CameraLocation Location { get; set; }
    public string? LocationName { get; set; }
    public Guid? OperatoryId { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public string? FirmwareVersion { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime RegisteredAt { get; set; }

    // Capabilities
    public bool HasCamera { get; set; } = true;
    public bool CanRecord { get; set; } = true;
    public bool CanDetectObjects { get; set; } = true;
    public bool SupportsOcr { get; set; }
    public string? ActiveModelName { get; set; }
    public string? ActiveModelVersion { get; set; }

    // Tenant
    public Guid TenantId { get; set; }
}

/// <summary>
/// A single detection from a privaseeAI edge device.
/// </summary>
public class DetectionEventDto
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DetectionClass DetectionClass { get; set; }
    public double Confidence { get; set; }
    public BoundingBoxDto BoundingBox { get; set; } = new();
    public DateTime Timestamp { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? VideoClipUrl { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public class BoundingBoxDto
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// A correlated vision event — detection enriched with CDO clinical context.
/// </summary>
public class VisionEventDto
{
    public Guid Id { get; set; }
    public VisionEventType EventType { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid DeviceId { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public CameraLocation Location { get; set; }

    // Context correlation
    public Guid? PatientId { get; set; }
    public string? PatientName { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? ProviderId { get; set; }
    public string? ProviderName { get; set; }

    // Detection data
    public List<DetectionEventDto> Detections { get; set; } = new();

    // Alert (if triggered)
    public AlertSeverity? AlertSeverity { get; set; }
    public AlertType? AlertType { get; set; }
    public string? AlertMessage { get; set; }

    // Media
    public string? ImageUrl { get; set; }
    public string? VideoUrl { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// Narcotics cabinet access log entry.
/// </summary>
public class CabinetAccessLogDto
{
    public Guid Id { get; set; }
    public DateTime Timestamp { get; set; }
    public Guid DeviceId { get; set; }

    // Who
    public Guid? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public string? BadgeId { get; set; }
    public bool IdentityVerified { get; set; }

    // What
    public TimeSpan Duration { get; set; }
    public string? ImageUrl { get; set; }
    public string? VideoUrl { get; set; }

    // Cross-reference with CDO
    public Guid? ActivePrescriptionId { get; set; }
    public string? PrescriptionDrugName { get; set; }
    public Guid? ActiveAppointmentId { get; set; }
    public string? AppointmentPatientName { get; set; }

    // Compliance
    public bool HasActiveRx { get; set; }
    public bool HasActiveAppointment { get; set; }
    public bool TwoPersonWitnessVerified { get; set; }
    public bool IsAfterHours { get; set; }
    public AlertSeverity Severity { get; set; }
    public string? AlertMessage { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// Insurance card OCR result.
/// </summary>
public class InsuranceCardScanDto
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTime Timestamp { get; set; }
    public double OcrConfidence { get; set; }

    // Extracted fields
    public string? PayerName { get; set; }
    public string? PayerId { get; set; }
    public string? MemberId { get; set; }
    public string? GroupNumber { get; set; }
    public string? SubscriberName { get; set; }
    public string? PlanName { get; set; }
    public string? RxBin { get; set; }
    public string? RxPcn { get; set; }
    public string? RxGroup { get; set; }
    public string? CopayAmount { get; set; }
    public string? PhoneNumber { get; set; }

    // Source image
    public string? CardImageUrl { get; set; }
    public string? CardImageBackUrl { get; set; }

    // CDO correlation
    public Guid? MatchedPatientId { get; set; }
    public bool EligibilityCheckTriggered { get; set; }
    public string? EligibilityResult { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// Patient consent recording reference.
/// </summary>
public class ConsentRecordingDto
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public string PatientName { get; set; } = string.Empty;
    public Guid ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public Guid? AppointmentId { get; set; }

    public ConsentStatus Status { get; set; }
    public string ConsentType { get; set; } = string.Empty; // "Sedation", "Extraction", "General Treatment"
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? Duration { get; set; }

    // Media
    public string? VideoUrl { get; set; }
    public string? ThumbnailUrl { get; set; }

    // Verification
    public bool PatientDetected { get; set; }
    public bool ConsentFormDetected { get; set; }
    public bool ProviderDetected { get; set; }

    public Guid TenantId { get; set; }
}

/// <summary>
/// Clinical note draft generated from procedure observations.
/// </summary>
public class ClinicalNoteDraftDto
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }

    public string? CdtCode { get; set; }
    public string? ProcedureName { get; set; }
    public DateTime ObservationStart { get; set; }
    public DateTime ObservationEnd { get; set; }

    // Vision observations
    public List<ProcedureObservationDto> Observations { get; set; } = new();

    // Generated note
    public string? DraftNoteText { get; set; }
    public string? StructuredNoteJson { get; set; }
    public bool ReviewedByProvider { get; set; }
    public bool ApprovedByProvider { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public Guid TenantId { get; set; }
}

public class ProcedureObservationDto
{
    public DateTime Timestamp { get; set; }
    public DetectionClass DetectedInstrument { get; set; }
    public double Confidence { get; set; }
    public string? InferredAction { get; set; } // "Anesthesia administration", "Extraction", "Suturing"
}

// ════════════════════════════════════════════════════════════════════════════
//  REQUESTS
// ════════════════════════════════════════════════════════════════════════════

public class RegisterDeviceRequest
{
    public string Name { get; set; } = string.Empty;
    public DeviceType Type { get; set; }
    public CameraLocation Location { get; set; }
    public string? LocationName { get; set; }
    public Guid? OperatoryId { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public bool SupportsOcr { get; set; }
}

public class IngestDetectionRequest
{
    public Guid DeviceId { get; set; }
    public List<DetectionPayload> Detections { get; set; } = new();
    public DateTime Timestamp { get; set; }
    public string? ImageBase64 { get; set; }
}

public class DetectionPayload
{
    public string ClassName { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public double BboxX { get; set; }
    public double BboxY { get; set; }
    public double BboxW { get; set; }
    public double BboxH { get; set; }
}

public class ScanInsuranceCardRequest
{
    public Guid DeviceId { get; set; }
    public string ImageBase64 { get; set; } = string.Empty;
    public string? BackImageBase64 { get; set; }
    public Guid? PatientId { get; set; }
}

public class StartConsentRecordingRequest
{
    public Guid DeviceId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? AppointmentId { get; set; }
    public string ConsentType { get; set; } = string.Empty;
}

public class LogCabinetAccessRequest
{
    public Guid DeviceId { get; set; }
    public string? BadgeId { get; set; }
    public string? ImageBase64 { get; set; }
    public DateTime DoorOpenedAt { get; set; }
    public DateTime? DoorClosedAt { get; set; }
}

public class GenerateClinicalNoteRequest
{
    public Guid AppointmentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public string? CdtCode { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  SERVICE INTERFACE
// ════════════════════════════════════════════════════════════════════════════

public interface IVisionService
{
    // Devices
    Task<VisionDeviceDto> RegisterDeviceAsync(RegisterDeviceRequest request, CancellationToken ct = default);
    Task<VisionDeviceDto?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<List<VisionDeviceDto>> GetDevicesAsync(CancellationToken ct = default);
    Task<VisionDeviceDto> UpdateDeviceStatusAsync(Guid deviceId, DeviceStatus status, CancellationToken ct = default);

    // Detection ingestion
    Task<VisionEventDto> IngestDetectionsAsync(IngestDetectionRequest request, CancellationToken ct = default);

    // Events
    Task<List<VisionEventDto>> GetEventsAsync(DateTime? from = null, DateTime? to = null,
        CameraLocation? location = null, AlertSeverity? minSeverity = null,
        int limit = 50, CancellationToken ct = default);
    Task<VisionEventDto?> GetEventAsync(Guid eventId, CancellationToken ct = default);

    // Insurance card OCR
    Task<InsuranceCardScanDto> ScanInsuranceCardAsync(ScanInsuranceCardRequest request, CancellationToken ct = default);
    Task<List<InsuranceCardScanDto>> GetInsuranceScansAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default);

    // Consent recording
    Task<ConsentRecordingDto> StartConsentRecordingAsync(StartConsentRecordingRequest request, CancellationToken ct = default);
    Task<ConsentRecordingDto> CompleteConsentRecordingAsync(Guid recordingId, CancellationToken ct = default);
    Task<List<ConsentRecordingDto>> GetConsentRecordingsAsync(Guid? patientId = null, int limit = 20, CancellationToken ct = default);

    // Narcotics cabinet
    Task<CabinetAccessLogDto> LogCabinetAccessAsync(LogCabinetAccessRequest request, CancellationToken ct = default);
    Task<List<CabinetAccessLogDto>> GetCabinetAccessLogsAsync(DateTime? from = null, DateTime? to = null,
        AlertSeverity? minSeverity = null, int limit = 50, CancellationToken ct = default);

    // Clinical notes
    Task<ClinicalNoteDraftDto> GenerateClinicalNoteAsync(GenerateClinicalNoteRequest request, CancellationToken ct = default);
    Task<ClinicalNoteDraftDto> ApproveClinicalNoteAsync(Guid noteId, CancellationToken ct = default);
    Task<List<ClinicalNoteDraftDto>> GetClinicalNotesAsync(Guid? appointmentId = null, int limit = 20, CancellationToken ct = default);
}

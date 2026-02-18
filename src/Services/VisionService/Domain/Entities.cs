// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Contracts.Vision;

namespace VisionService.Domain;

// ════════════════════════════════════════════════════════════════════════════
//  VISION DEVICE
// ════════════════════════════════════════════════════════════════════════════

public class VisionDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public DeviceType Type { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Provisioning;
    public CameraLocation Location { get; set; }

    [MaxLength(200)]
    public string? LocationName { get; set; }

    public Guid? OperatoryId { get; set; }

    [MaxLength(45)]
    public string? IpAddress { get; set; }

    [MaxLength(17)]
    public string? MacAddress { get; set; }

    [MaxLength(100)]
    public string? FirmwareVersion { get; set; }

    // Capabilities
    public bool HasCamera { get; set; } = true;
    public bool CanRecord { get; set; } = true;
    public bool CanDetectObjects { get; set; } = true;
    public bool SupportsOcr { get; set; }

    [MaxLength(100)]
    public string? ActiveModelName { get; set; }

    [MaxLength(50)]
    public string? ActiveModelVersion { get; set; }

    // Timestamps
    public DateTime? LastHeartbeat { get; set; }
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Tenant
    public Guid TenantId { get; set; }

    // Navigation
    public List<VisionEvent> Events { get; set; } = new();
    public List<CabinetAccessLog> CabinetAccessLogs { get; set; } = new();
}

// ════════════════════════════════════════════════════════════════════════════
//  VISION EVENT (correlated detection)
// ════════════════════════════════════════════════════════════════════════════

public class VisionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public VisionEventType EventType { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Device
    public Guid DeviceId { get; set; }
    public VisionDevice Device { get; set; } = null!;

    // Context correlation (populated by context engine)
    public Guid? PatientId { get; set; }
    public Guid? AppointmentId { get; set; }
    public Guid? ProviderId { get; set; }

    // Detections (JSON serialized for flexibility)
    [MaxLength(8000)]
    public string? DetectionsJson { get; set; }

    // Alert
    public AlertSeverity? AlertSeverity { get; set; }
    public AlertType? AlertType { get; set; }

    [MaxLength(500)]
    public string? AlertMessage { get; set; }

    // Media (Azure Blob URLs)
    [MaxLength(2048)]
    public string? ImageUrl { get; set; }

    [MaxLength(2048)]
    public string? VideoUrl { get; set; }

    // Sync tracking
    public bool ProcessedByCorrelation { get; set; }
    public bool NotificationSent { get; set; }

    // Tenant
    public Guid TenantId { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  CABINET ACCESS LOG (Narcotics/Controlled Substances)
// ════════════════════════════════════════════════════════════════════════════

public class CabinetAccessLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Device
    public Guid DeviceId { get; set; }
    public VisionDevice Device { get; set; } = null!;

    // Who accessed
    public Guid? ProviderId { get; set; }

    [MaxLength(100)]
    public string? ProviderName { get; set; }

    [MaxLength(100)]
    public string? BadgeId { get; set; }

    public bool IdentityVerified { get; set; }

    // Access details
    public DateTime DoorOpenedAt { get; set; }
    public DateTime? DoorClosedAt { get; set; }
    public int DurationSeconds { get; set; }

    // Media
    [MaxLength(2048)]
    public string? ImageUrl { get; set; }

    [MaxLength(2048)]
    public string? VideoUrl { get; set; }

    // CDO cross-reference (populated by correlation engine)
    public Guid? ActivePrescriptionId { get; set; }

    [MaxLength(200)]
    public string? PrescriptionDrugName { get; set; }

    public Guid? ActiveAppointmentId { get; set; }

    [MaxLength(200)]
    public string? AppointmentPatientName { get; set; }

    // Compliance flags
    public bool HasActiveRx { get; set; }
    public bool HasActiveAppointment { get; set; }
    public bool TwoPersonWitnessVerified { get; set; }
    public bool IsAfterHours { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Info;

    [MaxLength(500)]
    public string? AlertMessage { get; set; }

    // Tenant
    public Guid TenantId { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  INSURANCE CARD SCAN
// ════════════════════════════════════════════════════════════════════════════

public class InsuranceCardScan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DeviceId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public double OcrConfidence { get; set; }

    // Extracted fields
    [MaxLength(200)] public string? PayerName { get; set; }
    [MaxLength(50)] public string? PayerId { get; set; }
    [MaxLength(50)] public string? MemberId { get; set; }
    [MaxLength(50)] public string? GroupNumber { get; set; }
    [MaxLength(200)] public string? SubscriberName { get; set; }
    [MaxLength(200)] public string? PlanName { get; set; }
    [MaxLength(20)] public string? RxBin { get; set; }
    [MaxLength(20)] public string? RxPcn { get; set; }
    [MaxLength(20)] public string? RxGroup { get; set; }
    [MaxLength(20)] public string? CopayAmount { get; set; }
    [MaxLength(20)] public string? PhoneNumber { get; set; }

    // Source image
    [MaxLength(2048)] public string? CardImageUrl { get; set; }
    [MaxLength(2048)] public string? CardImageBackUrl { get; set; }

    // CDO correlation
    public Guid? MatchedPatientId { get; set; }
    public bool EligibilityCheckTriggered { get; set; }
    [MaxLength(50)] public string? EligibilityResult { get; set; }

    public Guid TenantId { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  CONSENT RECORDING
// ════════════════════════════════════════════════════════════════════════════

public class ConsentRecording
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }
    public Guid? AppointmentId { get; set; }

    public ConsentStatus Status { get; set; } = ConsentStatus.NotStarted;

    [Required, MaxLength(100)]
    public string ConsentType { get; set; } = string.Empty;

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public int? DurationSeconds { get; set; }

    // Media
    [MaxLength(2048)] public string? VideoUrl { get; set; }
    [MaxLength(2048)] public string? ThumbnailUrl { get; set; }

    // Detection verification
    public bool PatientDetected { get; set; }
    public bool ConsentFormDetected { get; set; }
    public bool ProviderDetected { get; set; }

    public Guid DeviceId { get; set; }
    public Guid TenantId { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
//  CLINICAL NOTE DRAFT
// ════════════════════════════════════════════════════════════════════════════

public class ClinicalNoteDraft
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AppointmentId { get; set; }
    public Guid PatientId { get; set; }
    public Guid ProviderId { get; set; }

    [MaxLength(10)] public string? CdtCode { get; set; }
    [MaxLength(200)] public string? ProcedureName { get; set; }

    public DateTime ObservationStart { get; set; }
    public DateTime ObservationEnd { get; set; }

    // Observations (JSON array of ProcedureObservationDto)
    public string? ObservationsJson { get; set; }

    // Generated note
    public string? DraftNoteText { get; set; }
    public string? StructuredNoteJson { get; set; }

    // Provider review
    public bool ReviewedByProvider { get; set; }
    public bool ApprovedByProvider { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid TenantId { get; set; }
}

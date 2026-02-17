// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace CloudDentalOffice.Contracts.Prescriptions;

// ─── DTOs ───────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a prescription (maps to FHIR MedicationRequest).
/// </summary>
public record PrescriptionDto
{
    public Guid Id { get; init; }
    public Guid PatientId { get; init; }
    public Guid PrescriberId { get; init; }
    public string? TenantId { get; init; }

    // Medication
    public string DrugName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public string? NdcCode { get; init; }
    public string Strength { get; init; } = string.Empty;
    public string DoseForm { get; init; } = string.Empty; // tablet, capsule, liquid, etc.

    // Sig (directions)
    public string Directions { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string QuantityUnit { get; init; } = "EA"; // EA, ML, etc.
    public int Refills { get; init; }
    public int DaysSupply { get; init; }
    public bool DispenseAsWritten { get; init; }

    // Classification
    public DrugSchedule Schedule { get; init; } = DrugSchedule.NonControlled;
    public PrescriptionStatus Status { get; init; } = PrescriptionStatus.Draft;
    public PrescriptionIntent Intent { get; init; } = PrescriptionIntent.Order;

    // Pharmacy
    public string? PharmacyName { get; init; }
    public string? PharmacyNcpdpId { get; init; }
    public string? PharmacyAddress { get; init; }

    // Clinical context
    public string? DentalProcedureCode { get; init; }  // CDT code that prompted the Rx
    public string? DiagnosisCode { get; init; }          // ICD-10
    public string? ClinicalNotes { get; init; }

    // eRx tracking
    public string? DoseSpotPrescriptionId { get; init; }
    public string? SurescriptsMessageId { get; init; }
    public string? ErxStatus { get; init; }  // DoseSpot-specific status

    // FHIR
    public string? FhirMedicationRequestId { get; init; }

    // Audit
    public DateTime CreatedAt { get; init; }
    public DateTime? SentAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? CreatedBy { get; init; }
}

/// <summary>
/// Patient medication history entry (from DoseSpot/Surescripts).
/// </summary>
public record MedicationHistoryDto
{
    public string DrugName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public string Strength { get; init; } = string.Empty;
    public string? Directions { get; init; }
    public string? Prescriber { get; init; }
    public string? Pharmacy { get; init; }
    public DateTime? LastFillDate { get; init; }
    public string? Source { get; init; } // PBM, pharmacy, etc.
    public bool IsActive { get; init; }
}

/// <summary>
/// Patient allergy record for drug interaction checks.
/// </summary>
public record PatientAllergyDto
{
    public Guid Id { get; init; }
    public Guid PatientId { get; init; }
    public string AllergyName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public string? Reaction { get; init; }
    public AllergySeverity Severity { get; init; }
    public string? Source { get; init; }
}

/// <summary>
/// Drug interaction alert from DoseSpot safety checks.
/// </summary>
public record DrugInteractionAlertDto
{
    public string Severity { get; init; } = string.Empty; // high, moderate, low
    public string InteractionType { get; init; } = string.Empty; // drug-drug, drug-allergy, duplicate-therapy
    public string Drug1 { get; init; } = string.Empty;
    public string? Drug2 { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? ClinicalEffect { get; init; }
    public bool RequiresOverride { get; init; }
}

/// <summary>
/// Real-Time Prescription Benefit (RTPB) response.
/// </summary>
public record PrescriptionBenefitDto
{
    public string DrugName { get; init; } = string.Empty;
    public decimal? PatientCopay { get; init; }
    public decimal? PatientCoinsurance { get; init; }
    public string? FormularyStatus { get; init; }  // on-formulary, non-formulary, prior-auth-required
    public string? TierLevel { get; init; }
    public List<FormularyAlternativeDto> Alternatives { get; init; } = new();
    public bool PriorAuthRequired { get; init; }
}

public record FormularyAlternativeDto
{
    public string DrugName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public decimal? EstimatedCopay { get; init; }
    public string? TierLevel { get; init; }
}

/// <summary>
/// Pharmacy search result.
/// </summary>
public record PharmacyDto
{
    public string NcpdpId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string ZipCode { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Fax { get; init; }
    public double? DistanceMiles { get; init; }
    public bool AcceptsEpcs { get; init; }
    public bool Is24Hour { get; init; }
    public PharmacyType PharmacyType { get; init; }
}

/// <summary>
/// Prescriber (dentist) registration info for DoseSpot.
/// </summary>
public record PrescriberDto
{
    public Guid ProviderId { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Npi { get; init; } = string.Empty;
    public string DeaNumber { get; init; } = string.Empty;
    public string StateLicenseNumber { get; init; } = string.Empty;
    public string StateLicenseState { get; init; } = string.Empty;
    public string? DoseSpotClinicianId { get; init; }
    public bool EpcsEnabled { get; init; }
    public EpcsAuthMethod EpcsAuthMethod { get; init; } = EpcsAuthMethod.DoseSpotBuiltIn;
    public PrescriberStatus Status { get; init; } = PrescriberStatus.Pending;
}

/// <summary>
/// eRx notification from Surescripts/DoseSpot (refill requests, denials, etc.).
/// </summary>
public record ErxNotification
{
    public Guid Id { get; init; }
    public ErxNotificationType Type { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid? PrescriptionId { get; init; }
    public Guid? PatientId { get; init; }
    public DateTime Timestamp { get; init; }
    public bool Acknowledged { get; set; }
    public string? ActionUrl { get; init; }
}

// ─── Requests ───────────────────────────────────────────────────────────────

public record CreatePrescriptionRequest
{
    public Guid PatientId { get; set; }
    public Guid PrescriberId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string? RxNormCode { get; set; }
    public string? NdcCode { get; set; }
    public string Strength { get; set; } = string.Empty;
    public string DoseForm { get; set; } = string.Empty;
    public string Directions { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string QuantityUnit { get; set; } = "EA";
    public int Refills { get; set; }
    public int DaysSupply { get; set; }
    public bool DispenseAsWritten { get; set; }
    public DrugSchedule Schedule { get; set; }
    public string? PharmacyNcpdpId { get; set; }
    public string? DentalProcedureCode { get; set; }
    public string? DiagnosisCode { get; set; }
    public string? ClinicalNotes { get; set; }
}

public record SendPrescriptionRequest
{
    public Guid PrescriptionId { get; init; }
    public string? PharmacyNcpdpId { get; init; }  // override pharmacy at send time
    public bool OverrideInteractions { get; init; }
    public string? OverrideReason { get; init; }
}

public record RefillRequestResponse
{
    public Guid PrescriptionId { get; init; }
    public bool Approved { get; init; }
    public string? DenialReason { get; init; }
    public int? NewRefillCount { get; init; }
}

public record RegisterPrescriberRequest
{
    public Guid ProviderId { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Npi { get; init; } = string.Empty;
    public string DeaNumber { get; init; } = string.Empty;
    public string StateLicenseNumber { get; init; } = string.Empty;
    public string StateLicenseState { get; init; } = string.Empty;
    public bool EnableEpcs { get; init; }
    public EpcsAuthMethod PreferredEpcsAuthMethod { get; init; } = EpcsAuthMethod.DoseSpotBuiltIn;
}

public record PharmacySearchRequest
{
    public string? Name { get; init; }
    public string? ZipCode { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double RadiusMiles { get; init; } = 10;
    public PharmacyType? Type { get; init; }
}

public record GetMedicationHistoryRequest
{
    public Guid PatientId { get; init; }
    public bool IncludeInactive { get; init; }
    public bool RequirePatientConsent { get; init; } = true;
}

public record CheckBenefitsRequest
{
    public Guid PatientId { get; init; }
    public string DrugName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public string? PharmacyNcpdpId { get; init; }
}

// ─── Enums ──────────────────────────────────────────────────────────────────

public enum DrugSchedule
{
    NonControlled = 0,
    ScheduleII = 2,
    ScheduleIII = 3,
    ScheduleIV = 4,
    ScheduleV = 5
}

public enum PrescriptionStatus
{
    Draft,
    PendingSafetyCheck,
    ReadyToSend,
    Sent,
    PharmacyVerified,
    Dispensed,
    Cancelled,
    Denied,
    RefillRequested,
    Expired,
    Error
}

public enum PrescriptionIntent
{
    Order,
    Refill,
    Renewal
}

public enum AllergySeverity
{
    Mild,
    Moderate,
    Severe,
    LifeThreatening
}

public enum PharmacyType
{
    Retail,
    MailOrder,
    Specialty,
    Compounding,
    Hospital
}

public enum EpcsAuthMethod
{
    DoseSpotBuiltIn,   // DoseSpot handles EPCS auth internally
    Imprivata,          // Delegate to Imprivata Confirm ID / Enterprise Access Management
    ExternalIdp         // Other identity provider
}

public enum PrescriberStatus
{
    Pending,
    Active,
    IdentityProofingRequired,
    EpcsEnrollmentRequired,
    Suspended,
    Inactive
}

public enum ErxNotificationType
{
    RefillRequest,
    RefillDenied,
    RefillApproved,
    PrescriptionChange,
    PharmacyMessage,
    ClinicalAlert,
    SystemError
}

// ─── Service Interface ──────────────────────────────────────────────────────

/// <summary>
/// Contract for the Prescription microservice (eRx bounded context).
/// </summary>
public interface IPrescriptionService
{
    // Prescriptions
    Task<PrescriptionDto> CreatePrescriptionAsync(CreatePrescriptionRequest request, CancellationToken ct = default);
    Task<PrescriptionDto?> GetPrescriptionAsync(Guid id, CancellationToken ct = default);
    Task<List<PrescriptionDto>> GetPatientPrescriptionsAsync(Guid patientId, bool includeExpired = false, CancellationToken ct = default);
    Task<PrescriptionDto> SendPrescriptionAsync(SendPrescriptionRequest request, CancellationToken ct = default);
    Task<PrescriptionDto> CancelPrescriptionAsync(Guid id, string reason, CancellationToken ct = default);
    Task<RefillRequestResponse> RespondToRefillRequestAsync(Guid prescriptionId, RefillRequestResponse response, CancellationToken ct = default);

    // Safety checks
    Task<List<DrugInteractionAlertDto>> CheckInteractionsAsync(Guid patientId, string rxNormCode, CancellationToken ct = default);

    // Medication history
    Task<List<MedicationHistoryDto>> GetMedicationHistoryAsync(GetMedicationHistoryRequest request, CancellationToken ct = default);

    // Allergies
    Task<List<PatientAllergyDto>> GetPatientAllergiesAsync(Guid patientId, CancellationToken ct = default);
    Task<PatientAllergyDto> AddPatientAllergyAsync(Guid patientId, PatientAllergyDto allergy, CancellationToken ct = default);

    // Benefits (RTPB / Da Vinci Formulary)
    Task<PrescriptionBenefitDto> CheckBenefitsAsync(CheckBenefitsRequest request, CancellationToken ct = default);

    // Pharmacy
    Task<List<PharmacyDto>> SearchPharmaciesAsync(PharmacySearchRequest request, CancellationToken ct = default);

    // Prescriber management
    Task<PrescriberDto> RegisterPrescriberAsync(RegisterPrescriberRequest request, CancellationToken ct = default);
    Task<PrescriberDto?> GetPrescriberAsync(Guid providerId, CancellationToken ct = default);

    // DoseSpot UI integration
    Task<string> GetDoseSpotSsoUrlAsync(Guid providerId, Guid patientId, CancellationToken ct = default);
}

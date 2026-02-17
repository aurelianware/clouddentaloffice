// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Prescriptions;

namespace PrescriptionService.Adapters;

/// <summary>
/// Abstraction over the eRx gateway (DoseSpot, or future alternatives).
/// The PrescriptionService endpoints call this interface — never DoseSpot directly.
/// This allows feature-flagging between providers and simplifies testing.
/// </summary>
public interface IErxGateway
{
    /// <summary>Send a prescription electronically via Surescripts.</summary>
    Task<ErxSendResult> SendPrescriptionAsync(ErxPrescriptionPayload payload, CancellationToken ct = default);

    /// <summary>Cancel a previously sent prescription.</summary>
    Task<ErxCancelResult> CancelPrescriptionAsync(string erxPrescriptionId, string reason, CancellationToken ct = default);

    /// <summary>Run drug-drug and drug-allergy interaction checks.</summary>
    Task<List<DrugInteractionAlertDto>> CheckInteractionsAsync(ErxInteractionCheckPayload payload, CancellationToken ct = default);

    /// <summary>Retrieve medication history from PBMs/pharmacies (requires patient consent).</summary>
    Task<List<MedicationHistoryDto>> GetMedicationHistoryAsync(string erxPatientId, CancellationToken ct = default);

    /// <summary>Search pharmacies via Surescripts directory.</summary>
    Task<List<PharmacyDto>> SearchPharmaciesAsync(PharmacySearchRequest request, CancellationToken ct = default);

    /// <summary>Check real-time prescription benefits (RTPB).</summary>
    Task<PrescriptionBenefitDto> CheckBenefitsAsync(string erxPatientId, string rxNormCode, string? pharmacyNcpdpId, CancellationToken ct = default);

    /// <summary>Register a clinician (dentist) with the eRx platform.</summary>
    Task<ErxPrescriberRegistrationResult> RegisterPrescriberAsync(ErxPrescriberPayload payload, CancellationToken ct = default);

    /// <summary>Get SSO URL to launch the embedded eRx UI (DoseSpot iFrame).</summary>
    Task<string> GetSsoUrlAsync(string clinicianId, string? patientId, CancellationToken ct = default);

    /// <summary>Respond to a refill request from a pharmacy.</summary>
    Task<ErxRefillResult> RespondToRefillAsync(string erxPrescriptionId, bool approved, string? denialReason, int? newRefillCount, CancellationToken ct = default);

    /// <summary>Get pending notifications (refill requests, errors, etc.).</summary>
    Task<List<ErxNotification>> GetNotificationsAsync(string clinicianId, CancellationToken ct = default);
}

// ─── Gateway Payloads & Results ─────────────────────────────────────────────

public record ErxPrescriptionPayload
{
    // Patient
    public string ErxPatientId { get; init; } = string.Empty;
    public string PatientFirstName { get; init; } = string.Empty;
    public string PatientLastName { get; init; } = string.Empty;
    public DateTime PatientDateOfBirth { get; init; }
    public string? PatientGender { get; init; }
    public string? PatientAddress { get; init; }
    public string? PatientCity { get; init; }
    public string? PatientState { get; init; }
    public string? PatientZip { get; init; }
    public string? PatientPhone { get; init; }

    // Prescriber
    public string ErxClinicianId { get; init; } = string.Empty;

    // Medication
    public string DrugName { get; init; } = string.Empty;
    public string? RxNormCode { get; init; }
    public string? NdcCode { get; init; }
    public string Strength { get; init; } = string.Empty;
    public string DoseForm { get; init; } = string.Empty;
    public string Directions { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string QuantityUnit { get; init; } = "EA";
    public int Refills { get; init; }
    public int DaysSupply { get; init; }
    public bool DispenseAsWritten { get; init; }
    public int Schedule { get; init; }

    // Pharmacy
    public string PharmacyNcpdpId { get; init; } = string.Empty;

    // Notes
    public string? PharmacyNotes { get; init; }
}

public record ErxSendResult
{
    public bool Success { get; init; }
    public string? ErxPrescriptionId { get; init; }
    public string? SurescriptsMessageId { get; init; }
    public string? ErxStatus { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public List<DrugInteractionAlertDto> InteractionAlerts { get; init; } = new();
    public bool RequiresInteractionOverride { get; init; }
}

public record ErxCancelResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ErxInteractionCheckPayload
{
    public string ErxPatientId { get; init; } = string.Empty;
    public string RxNormCode { get; init; } = string.Empty;
    public List<string> CurrentMedicationRxNormCodes { get; init; } = new();
    public List<string> AllergyRxNormCodes { get; init; } = new();
}

public record ErxPrescriberPayload
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Npi { get; init; } = string.Empty;
    public string DeaNumber { get; init; } = string.Empty;
    public string StateLicenseNumber { get; init; } = string.Empty;
    public string StateLicenseState { get; init; } = string.Empty;
    public string ClinicName { get; init; } = string.Empty;
    public string ClinicAddress { get; init; } = string.Empty;
    public string ClinicCity { get; init; } = string.Empty;
    public string ClinicState { get; init; } = string.Empty;
    public string ClinicZip { get; init; } = string.Empty;
    public string ClinicPhone { get; init; } = string.Empty;
    public string ClinicFax { get; init; } = string.Empty;
    public bool EnableEpcs { get; init; }
}

public record ErxPrescriberRegistrationResult
{
    public bool Success { get; init; }
    public string? ClinicianId { get; init; }
    public string? ErrorMessage { get; init; }
    public bool RequiresIdentityProofing { get; init; }
}

public record ErxRefillResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
}

public record ErxNotification
{
    public string NotificationId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty; // RefillRequest, TransmissionError, PharmacyChange, etc.
    public string? PrescriptionId { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public bool Acknowledged { get; init; }
}

// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PrescriptionService.Domain;

// ─── Entities ───────────────────────────────────────────────────────────────

[Index(nameof(PatientId))]
[Index(nameof(PrescriberId))]
[Index(nameof(TenantId), nameof(Status))]
[Index(nameof(DoseSpotPrescriptionId))]
[Index(nameof(FhirMedicationRequestId))]
public class Prescription
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PatientId { get; set; }

    [Required]
    public Guid PrescriberId { get; set; }

    [Required, MaxLength(50)]
    public string TenantId { get; set; } = string.Empty;

    // ── Medication ──
    [Required, MaxLength(500)]
    public string DrugName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? RxNormCode { get; set; }

    [MaxLength(20)]
    public string? NdcCode { get; set; }

    [Required, MaxLength(100)]
    public string Strength { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string DoseForm { get; set; } = string.Empty;

    // ── Sig ──
    [Required, MaxLength(1000)]
    public string Directions { get; set; } = string.Empty;

    [Column(TypeName = "decimal(10,2)")]
    public decimal Quantity { get; set; }

    [MaxLength(10)]
    public string QuantityUnit { get; set; } = "EA";

    public int Refills { get; set; }
    public int DaysSupply { get; set; }
    public bool DispenseAsWritten { get; set; }

    // ── Classification ──
    public int Schedule { get; set; } // 0 = non-controlled, 2-5 = DEA schedule
    public string Status { get; set; } = "Draft";
    public string Intent { get; set; } = "Order";

    // ── Pharmacy ──
    [MaxLength(200)]
    public string? PharmacyName { get; set; }

    [MaxLength(20)]
    public string? PharmacyNcpdpId { get; set; }

    [MaxLength(500)]
    public string? PharmacyAddress { get; set; }

    // ── Clinical Context ──
    [MaxLength(10)]
    public string? DentalProcedureCode { get; set; }

    [MaxLength(20)]
    public string? DiagnosisCode { get; set; }

    [MaxLength(2000)]
    public string? ClinicalNotes { get; set; }

    // ── eRx Integration ──
    [MaxLength(100)]
    public string? DoseSpotPrescriptionId { get; set; }

    [MaxLength(100)]
    public string? SurescriptsMessageId { get; set; }

    [MaxLength(50)]
    public string? ErxStatus { get; set; }

    // ── FHIR ──
    [MaxLength(200)]
    public string? FhirMedicationRequestId { get; set; }

    // ── Audit ──
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    [MaxLength(100)]
    public string? CreatedBy { get; set; }

    // ── Navigation ──
    public ICollection<PrescriptionAuditEntry> AuditTrail { get; set; } = new List<PrescriptionAuditEntry>();
}

[Index(nameof(PatientId))]
[Index(nameof(TenantId))]
public class PatientAllergy
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PatientId { get; set; }

    [Required, MaxLength(50)]
    public string TenantId { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string AllergyName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? RxNormCode { get; set; }

    [MaxLength(500)]
    public string? Reaction { get; set; }

    [MaxLength(20)]
    public string Severity { get; set; } = "Moderate";

    [MaxLength(50)]
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(ProviderId))]
[Index(nameof(TenantId))]
[Index(nameof(Npi))]
public class Prescriber
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid ProviderId { get; set; }

    [Required, MaxLength(50)]
    public string TenantId { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required, MaxLength(10)]
    public string Npi { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string DeaNumber { get; set; } = string.Empty;

    [MaxLength(30)]
    public string StateLicenseNumber { get; set; } = string.Empty;

    [MaxLength(2)]
    public string StateLicenseState { get; set; } = string.Empty;

    // ── DoseSpot ──
    [MaxLength(50)]
    public string? DoseSpotClinicianId { get; set; }

    // ── EPCS ──
    public bool EpcsEnabled { get; set; }

    /// <summary>
    /// How EPCS 2FA is handled: DoseSpotBuiltIn, Imprivata, or ExternalIdp
    /// </summary>
    [MaxLength(30)]
    public string EpcsAuthMethod { get; set; } = "DoseSpotBuiltIn";

    public bool IdentityProofingComplete { get; set; }
    public DateTime? IdentityProofingDate { get; set; }

    [MaxLength(100)]
    public string? ImprivataUserId { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "Pending";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Immutable audit log for prescription lifecycle events.
/// Required for DEA EPCS compliance (21 CFR 1311).
/// </summary>
[Index(nameof(PrescriptionId))]
[Index(nameof(TenantId), nameof(Timestamp))]
public class PrescriptionAuditEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    public Guid PrescriptionId { get; set; }

    [Required, MaxLength(50)]
    public string TenantId { get; set; } = string.Empty;

    [Required, MaxLength(50)]
    public string Action { get; set; } = string.Empty; // Created, Sent, Cancelled, RefillApproved, etc.

    [MaxLength(100)]
    public string? PerformedBy { get; set; }

    [MaxLength(50)]
    public string? AuthenticationMethod { get; set; } // DoseSpot, Imprivata, etc.

    [MaxLength(2000)]
    public string? Details { get; set; }

    [MaxLength(50)]
    public string? PreviousStatus { get; set; }

    [MaxLength(50)]
    public string? NewStatus { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Navigation
    public Prescription Prescription { get; set; } = null!;
}

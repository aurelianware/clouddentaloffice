// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Prescriptions;
using Microsoft.EntityFrameworkCore;
using PrescriptionService.Adapters;
using PrescriptionService.Domain;

namespace PrescriptionService.Endpoints;

public static class PrescriptionEndpoints
{
    public static void MapPrescriptionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/prescriptions").WithTags("Prescriptions");
        var prescriberGroup = app.MapGroup("/api/prescribers").WithTags("Prescribers");
        var pharmacyGroup = app.MapGroup("/api/pharmacies").WithTags("Pharmacies");
        var allergyGroup = app.MapGroup("/api/patients/{patientId}/allergies").WithTags("Allergies");

        // ── Prescriptions ──────────────────────────────────────────────────

        group.MapPost("/", CreatePrescription)
            .WithName("CreatePrescription")
            .Produces<PrescriptionDto>(StatusCodes.Status201Created);

        group.MapGet("/{id:guid}", GetPrescription)
            .WithName("GetPrescription")
            .Produces<PrescriptionDto>()
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/patient/{patientId:guid}", GetPatientPrescriptions)
            .WithName("GetPatientPrescriptions")
            .Produces<List<PrescriptionDto>>();

        group.MapPost("/{id:guid}/send", SendPrescription)
            .WithName("SendPrescription")
            .Produces<PrescriptionDto>();

        group.MapPost("/{id:guid}/cancel", CancelPrescription)
            .WithName("CancelPrescription")
            .Produces<PrescriptionDto>();

        group.MapPost("/{id:guid}/refill-response", RespondToRefill)
            .WithName("RespondToRefill");

        // ── Safety & Benefits ──────────────────────────────────────────────

        group.MapPost("/check-interactions", CheckInteractions)
            .WithName("CheckInteractions")
            .Produces<List<DrugInteractionAlertDto>>();

        group.MapPost("/check-benefits", CheckBenefits)
            .WithName("CheckBenefits")
            .Produces<PrescriptionBenefitDto>();

        group.MapGet("/patient/{patientId:guid}/medication-history", GetMedicationHistory)
            .WithName("GetMedicationHistory")
            .Produces<List<MedicationHistoryDto>>();

        // ── DoseSpot SSO ───────────────────────────────────────────────────

        group.MapGet("/sso-url", GetDoseSpotSsoUrl)
            .WithName("GetDoseSpotSsoUrl")
            .Produces<string>();

        // ── Notifications ──────────────────────────────────────────────────

        group.MapGet("/notifications/{clinicianId}", GetNotifications)
            .WithName("GetNotifications")
            .Produces<List<CloudDentalOffice.Contracts.Prescriptions.ErxNotification>>();

        // ── Prescribers ────────────────────────────────────────────────────

        prescriberGroup.MapPost("/", RegisterPrescriber)
            .WithName("RegisterPrescriber")
            .Produces<PrescriberDto>(StatusCodes.Status201Created);

        prescriberGroup.MapGet("/{providerId:guid}", GetPrescriber)
            .WithName("GetPrescriber")
            .Produces<PrescriberDto>();

        prescriberGroup.MapGet("/{providerId:guid}/epcs-status", GetEpcsStatus)
            .WithName("GetEpcsStatus")
            .Produces<EpcsEnrollmentStatus>();

        // ── Pharmacies ─────────────────────────────────────────────────────

        pharmacyGroup.MapGet("/search", SearchPharmacies)
            .WithName("SearchPharmacies")
            .Produces<List<PharmacyDto>>();

        // ── Allergies ──────────────────────────────────────────────────────

        allergyGroup.MapGet("/", GetPatientAllergies)
            .WithName("GetPatientAllergies")
            .Produces<List<PatientAllergyDto>>();

        allergyGroup.MapPost("/", AddPatientAllergy)
            .WithName("AddPatientAllergy")
            .Produces<PatientAllergyDto>(StatusCodes.Status201Created);
    }

    // ─── Handlers ───────────────────────────────────────────────────────────

    private static async Task<IResult> CreatePrescription(
        CreatePrescriptionRequest request,
        PrescriptionDbContext db,
        CancellationToken ct)
    {
        var prescription = new Prescription
        {
            PatientId = request.PatientId,
            PrescriberId = request.PrescriberId,
            TenantId = "default", // TODO: resolve from auth context
            DrugName = request.DrugName,
            RxNormCode = request.RxNormCode,
            NdcCode = request.NdcCode,
            Strength = request.Strength,
            DoseForm = request.DoseForm,
            Directions = request.Directions,
            Quantity = request.Quantity,
            QuantityUnit = request.QuantityUnit,
            Refills = request.Refills,
            DaysSupply = request.DaysSupply,
            DispenseAsWritten = request.DispenseAsWritten,
            Schedule = (int)request.Schedule,
            PharmacyNcpdpId = request.PharmacyNcpdpId,
            DentalProcedureCode = request.DentalProcedureCode,
            DiagnosisCode = request.DiagnosisCode,
            ClinicalNotes = request.ClinicalNotes,
            Status = "Draft"
        };

        // Create audit entry
        prescription.AuditTrail.Add(new PrescriptionAuditEntry
        {
            PrescriptionId = prescription.Id,
            TenantId = prescription.TenantId,
            Action = "Created",
            NewStatus = "Draft",
            Details = $"Prescription created for {request.DrugName} {request.Strength}"
        });

        db.Prescriptions.Add(prescription);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/prescriptions/{prescription.Id}", MapToDto(prescription));
    }

    private static async Task<IResult> GetPrescription(
        Guid id, PrescriptionDbContext db, CancellationToken ct)
    {
        var prescription = await db.Prescriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        return prescription is null
            ? Results.NotFound()
            : Results.Ok(MapToDto(prescription));
    }

    private static async Task<IResult> GetPatientPrescriptions(
        Guid patientId, bool includeExpired, PrescriptionDbContext db, CancellationToken ct)
    {
        var query = db.Prescriptions
            .AsNoTracking()
            .Where(p => p.PatientId == patientId);

        if (!includeExpired)
            query = query.Where(p => p.Status != "Expired" && p.Status != "Cancelled");

        var prescriptions = await query
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(prescriptions.Select(MapToDto).ToList());
    }

    private static async Task<IResult> SendPrescription(
        Guid id,
        SendPrescriptionRequest request,
        PrescriptionDbContext db,
        IErxGateway erxGateway,
        EpcsAuthProviderFactory epcsFactory,
        CancellationToken ct)
    {
        var prescription = await db.Prescriptions
            .Include(p => p.AuditTrail)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription is null)
            return Results.NotFound();

        // For controlled substances, verify EPCS authentication
        if (prescription.Schedule >= 2)
        {
            var prescriber = await db.Prescribers
                .FirstOrDefaultAsync(p => p.ProviderId == prescription.PrescriberId, ct);

            if (prescriber != null)
            {
                var epcsProvider = epcsFactory.GetProvider(prescriber.EpcsAuthMethod);
                var isProofed = await epcsProvider.IsIdentityProofedAsync(
                    prescriber.ProviderId.ToString(), ct);

                if (!isProofed)
                {
                    return Results.BadRequest(new
                    {
                        Error = "EPCS identity proofing required",
                        Message = "Provider must complete identity proofing before prescribing controlled substances."
                    });
                }
            }
        }

        // Build the eRx payload
        // NOTE: In production, you'd look up patient demographics from PatientService
        var payload = new ErxPrescriptionPayload
        {
            ErxClinicianId = prescription.PrescriberId.ToString(), // mapped via Prescriber table
            DrugName = prescription.DrugName,
            RxNormCode = prescription.RxNormCode,
            NdcCode = prescription.NdcCode,
            Strength = prescription.Strength,
            DoseForm = prescription.DoseForm,
            Directions = prescription.Directions,
            Quantity = prescription.Quantity,
            QuantityUnit = prescription.QuantityUnit,
            Refills = prescription.Refills,
            DaysSupply = prescription.DaysSupply,
            DispenseAsWritten = prescription.DispenseAsWritten,
            Schedule = prescription.Schedule,
            PharmacyNcpdpId = request.PharmacyNcpdpId ?? prescription.PharmacyNcpdpId ?? string.Empty
        };

        var result = await erxGateway.SendPrescriptionAsync(payload, ct);

        if (result.RequiresInteractionOverride && !request.OverrideInteractions)
        {
            return Results.Conflict(new
            {
                Error = "Drug interactions detected",
                Interactions = result.InteractionAlerts,
                Message = "Set OverrideInteractions=true with an OverrideReason to proceed."
            });
        }

        if (!result.Success)
        {
            return Results.BadRequest(new
            {
                Error = result.ErrorMessage,
                ErrorCode = result.ErrorCode
            });
        }

        // Update prescription with eRx tracking info
        var previousStatus = prescription.Status;
        prescription.Status = "Sent";
        prescription.DoseSpotPrescriptionId = result.ErxPrescriptionId;
        prescription.SurescriptsMessageId = result.SurescriptsMessageId;
        prescription.ErxStatus = result.ErxStatus;
        prescription.SentAt = DateTime.UtcNow;
        prescription.UpdatedAt = DateTime.UtcNow;

        prescription.AuditTrail.Add(new PrescriptionAuditEntry
        {
            PrescriptionId = prescription.Id,
            TenantId = prescription.TenantId,
            Action = "Sent",
            PreviousStatus = previousStatus,
            NewStatus = "Sent",
            Details = $"Sent to pharmacy via Surescripts. MessageId: {result.SurescriptsMessageId}"
        });

        if (request.OverrideInteractions)
        {
            prescription.AuditTrail.Add(new PrescriptionAuditEntry
            {
                PrescriptionId = prescription.Id,
                TenantId = prescription.TenantId,
                Action = "InteractionOverride",
                Details = $"Interactions overridden. Reason: {request.OverrideReason}"
            });
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(MapToDto(prescription));
    }

    private static async Task<IResult> CancelPrescription(
        Guid id, string reason, PrescriptionDbContext db, IErxGateway erxGateway, CancellationToken ct)
    {
        var prescription = await db.Prescriptions
            .Include(p => p.AuditTrail)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription is null)
            return Results.NotFound();

        // Cancel in eRx system if already sent
        if (!string.IsNullOrEmpty(prescription.DoseSpotPrescriptionId))
        {
            var cancelResult = await erxGateway.CancelPrescriptionAsync(
                prescription.DoseSpotPrescriptionId, reason, ct);

            if (!cancelResult.Success)
                return Results.BadRequest(new { Error = cancelResult.ErrorMessage });
        }

        var previousStatus = prescription.Status;
        prescription.Status = "Cancelled";
        prescription.UpdatedAt = DateTime.UtcNow;

        prescription.AuditTrail.Add(new PrescriptionAuditEntry
        {
            PrescriptionId = prescription.Id,
            TenantId = prescription.TenantId,
            Action = "Cancelled",
            PreviousStatus = previousStatus,
            NewStatus = "Cancelled",
            Details = $"Reason: {reason}"
        });

        await db.SaveChangesAsync(ct);
        return Results.Ok(MapToDto(prescription));
    }

    private static async Task<IResult> RespondToRefill(
        Guid id, RefillRequestResponse response,
        PrescriptionDbContext db, IErxGateway erxGateway, CancellationToken ct)
    {
        var prescription = await db.Prescriptions
            .Include(p => p.AuditTrail)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (prescription is null)
            return Results.NotFound();

        if (!string.IsNullOrEmpty(prescription.DoseSpotPrescriptionId))
        {
            await erxGateway.RespondToRefillAsync(
                prescription.DoseSpotPrescriptionId,
                response.Approved,
                response.DenialReason,
                response.NewRefillCount, ct);
        }

        prescription.AuditTrail.Add(new PrescriptionAuditEntry
        {
            PrescriptionId = prescription.Id,
            TenantId = prescription.TenantId,
            Action = response.Approved ? "RefillApproved" : "RefillDenied",
            Details = response.Approved
                ? $"Refill approved. New refill count: {response.NewRefillCount}"
                : $"Refill denied. Reason: {response.DenialReason}"
        });

        prescription.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(MapToDto(prescription));
    }

    private static async Task<IResult> CheckInteractions(
        Guid patientId, string rxNormCode, IErxGateway erxGateway, CancellationToken ct)
    {
        var payload = new ErxInteractionCheckPayload
        {
            ErxPatientId = patientId.ToString(),
            RxNormCode = rxNormCode
        };

        var alerts = await erxGateway.CheckInteractionsAsync(payload, ct);
        return Results.Ok(alerts);
    }

    private static async Task<IResult> CheckBenefits(
        CheckBenefitsRequest request, IErxGateway erxGateway, CancellationToken ct)
    {
        var benefits = await erxGateway.CheckBenefitsAsync(
            request.PatientId.ToString(),
            request.RxNormCode ?? request.DrugName,
            request.PharmacyNcpdpId, ct);

        return Results.Ok(benefits);
    }

    private static async Task<IResult> GetMedicationHistory(
        Guid patientId, bool includeInactive, IErxGateway erxGateway, CancellationToken ct)
    {
        var history = await erxGateway.GetMedicationHistoryAsync(patientId.ToString(), ct);

        if (!includeInactive)
            history = history.Where(m => m.IsActive).ToList();

        return Results.Ok(history);
    }

    private static async Task<IResult> GetDoseSpotSsoUrl(
        Guid providerId, Guid? patientId, IErxGateway erxGateway,
        PrescriptionDbContext db, CancellationToken ct)
    {
        var prescriber = await db.Prescribers
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);

        if (prescriber?.DoseSpotClinicianId is null)
            return Results.BadRequest(new { Error = "Provider not registered with DoseSpot" });

        var url = await erxGateway.GetSsoUrlAsync(
            prescriber.DoseSpotClinicianId, patientId?.ToString(), ct);

        return Results.Ok(new { SsoUrl = url });
    }

    private static async Task<IResult> GetNotifications(
        string clinicianId, IErxGateway erxGateway, CancellationToken ct)
    {
        var notifications = await erxGateway.GetNotificationsAsync(clinicianId, ct);
        return Results.Ok(notifications);
    }

    private static async Task<IResult> RegisterPrescriber(
        RegisterPrescriberRequest request,
        PrescriptionDbContext db,
        IErxGateway erxGateway,
        CancellationToken ct)
    {
        // Register with DoseSpot
        var erxResult = await erxGateway.RegisterPrescriberAsync(new ErxPrescriberPayload
        {
            FirstName = request.FirstName,
            LastName = request.LastName,
            Npi = request.Npi,
            DeaNumber = request.DeaNumber,
            StateLicenseNumber = request.StateLicenseNumber,
            StateLicenseState = request.StateLicenseState,
            EnableEpcs = request.EnableEpcs
        }, ct);

        // Save local prescriber record
        var prescriber = new Prescriber
        {
            ProviderId = request.ProviderId,
            TenantId = "default", // TODO: resolve from auth context
            FirstName = request.FirstName,
            LastName = request.LastName,
            Npi = request.Npi,
            DeaNumber = request.DeaNumber,
            StateLicenseNumber = request.StateLicenseNumber,
            StateLicenseState = request.StateLicenseState,
            DoseSpotClinicianId = erxResult.ClinicianId,
            EpcsEnabled = request.EnableEpcs,
            EpcsAuthMethod = request.PreferredEpcsAuthMethod.ToString(),
            Status = erxResult.RequiresIdentityProofing
                ? "IdentityProofingRequired"
                : "Active"
        };

        db.Prescribers.Add(prescriber);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/prescribers/{prescriber.ProviderId}", MapPrescriberToDto(prescriber));
    }

    private static async Task<IResult> GetPrescriber(
        Guid providerId, PrescriptionDbContext db, CancellationToken ct)
    {
        var prescriber = await db.Prescribers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);

        return prescriber is null
            ? Results.NotFound()
            : Results.Ok(MapPrescriberToDto(prescriber));
    }

    private static async Task<IResult> GetEpcsStatus(
        Guid providerId,
        PrescriptionDbContext db,
        EpcsAuthProviderFactory epcsFactory,
        CancellationToken ct)
    {
        var prescriber = await db.Prescribers
            .FirstOrDefaultAsync(p => p.ProviderId == providerId, ct);

        if (prescriber is null)
            return Results.NotFound();

        var provider = epcsFactory.GetProvider(prescriber.EpcsAuthMethod);
        var status = await provider.GetEnrollmentStatusAsync(providerId.ToString(), ct);

        return Results.Ok(status);
    }

    private static async Task<IResult> SearchPharmacies(
        string? name, string? zipCode, double? lat, double? lng, double? radius,
        IErxGateway erxGateway, CancellationToken ct)
    {
        var request = new PharmacySearchRequest
        {
            Name = name,
            ZipCode = zipCode,
            Latitude = lat,
            Longitude = lng,
            RadiusMiles = radius ?? 10
        };

        var pharmacies = await erxGateway.SearchPharmaciesAsync(request, ct);
        return Results.Ok(pharmacies);
    }

    private static async Task<IResult> GetPatientAllergies(
        Guid patientId, PrescriptionDbContext db, CancellationToken ct)
    {
        var allergies = await db.PatientAllergies
            .AsNoTracking()
            .Where(a => a.PatientId == patientId)
            .ToListAsync(ct);

        return Results.Ok(allergies.Select(a => new PatientAllergyDto
        {
            Id = a.Id,
            PatientId = a.PatientId,
            AllergyName = a.AllergyName,
            RxNormCode = a.RxNormCode,
            Reaction = a.Reaction,
            Severity = Enum.TryParse<AllergySeverity>(a.Severity, out var sev) ? sev : AllergySeverity.Moderate,
            Source = a.Source
        }).ToList());
    }

    private static async Task<IResult> AddPatientAllergy(
        Guid patientId, PatientAllergyDto allergyDto,
        PrescriptionDbContext db, CancellationToken ct)
    {
        var allergy = new PatientAllergy
        {
            PatientId = patientId,
            TenantId = "default",
            AllergyName = allergyDto.AllergyName,
            RxNormCode = allergyDto.RxNormCode,
            Reaction = allergyDto.Reaction,
            Severity = allergyDto.Severity.ToString(),
            Source = allergyDto.Source
        };

        db.PatientAllergies.Add(allergy);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/patients/{patientId}/allergies", allergyDto with { Id = allergy.Id });
    }

    // ─── Mappers ────────────────────────────────────────────────────────────

    private static PrescriptionDto MapToDto(Prescription p) => new()
    {
        Id = p.Id,
        PatientId = p.PatientId,
        PrescriberId = p.PrescriberId,
        TenantId = p.TenantId,
        DrugName = p.DrugName,
        RxNormCode = p.RxNormCode,
        NdcCode = p.NdcCode,
        Strength = p.Strength,
        DoseForm = p.DoseForm,
        Directions = p.Directions,
        Quantity = p.Quantity,
        QuantityUnit = p.QuantityUnit,
        Refills = p.Refills,
        DaysSupply = p.DaysSupply,
        DispenseAsWritten = p.DispenseAsWritten,
        Schedule = (DrugSchedule)p.Schedule,
        Status = Enum.TryParse<PrescriptionStatus>(p.Status, out var status) ? status : PrescriptionStatus.Draft,
        Intent = Enum.TryParse<PrescriptionIntent>(p.Intent, out var intent) ? intent : PrescriptionIntent.Order,
        PharmacyName = p.PharmacyName,
        PharmacyNcpdpId = p.PharmacyNcpdpId,
        PharmacyAddress = p.PharmacyAddress,
        DentalProcedureCode = p.DentalProcedureCode,
        DiagnosisCode = p.DiagnosisCode,
        ClinicalNotes = p.ClinicalNotes,
        DoseSpotPrescriptionId = p.DoseSpotPrescriptionId,
        SurescriptsMessageId = p.SurescriptsMessageId,
        ErxStatus = p.ErxStatus,
        FhirMedicationRequestId = p.FhirMedicationRequestId,
        CreatedAt = p.CreatedAt,
        SentAt = p.SentAt,
        UpdatedAt = p.UpdatedAt,
        CreatedBy = p.CreatedBy
    };

    private static PrescriberDto MapPrescriberToDto(Prescriber p) => new()
    {
        ProviderId = p.ProviderId,
        FirstName = p.FirstName,
        LastName = p.LastName,
        Npi = p.Npi,
        DeaNumber = p.DeaNumber,
        StateLicenseNumber = p.StateLicenseNumber,
        StateLicenseState = p.StateLicenseState,
        DoseSpotClinicianId = p.DoseSpotClinicianId,
        EpcsEnabled = p.EpcsEnabled,
        EpcsAuthMethod = Enum.TryParse<EpcsAuthMethod>(p.EpcsAuthMethod, out var method)
            ? method : EpcsAuthMethod.DoseSpotBuiltIn,
        Status = Enum.TryParse<PrescriberStatus>(p.Status, out var status)
            ? status : PrescriberStatus.Pending
    };
}

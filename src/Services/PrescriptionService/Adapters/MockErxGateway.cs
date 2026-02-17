// Copyright (c) Aurelianware, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using CloudDentalOffice.Contracts.Prescriptions;
using Microsoft.Extensions.Logging;

namespace PrescriptionService.Adapters;

/// <summary>
/// Mock eRx gateway for development and testing.
/// Returns realistic responses without calling DoseSpot APIs.
/// Activated via configuration: "ErxProvider": "Mock"
/// </summary>
public class MockErxGateway : IErxGateway
{
    private readonly ILogger<MockErxGateway> _logger;
    private int _prescriptionCounter = 1000;

    public MockErxGateway(ILogger<MockErxGateway> logger)
    {
        _logger = logger;
    }

    public Task<ErxSendResult> SendPrescriptionAsync(
        ErxPrescriptionPayload payload, CancellationToken ct = default)
    {
        var rxId = $"MOCK-RX-{Interlocked.Increment(ref _prescriptionCounter)}";
        _logger.LogInformation("[Mock eRx] Sent prescription {RxId}: {Drug} for patient {Patient}",
            rxId, payload.DrugName, payload.ErxPatientId);

        // Simulate interaction alert for testing (Ibuprofen + Aspirin is a common dental scenario)
        var alerts = new List<DrugInteractionAlertDto>();
        if (payload.DrugName.Contains("Ibuprofen", StringComparison.OrdinalIgnoreCase))
        {
            alerts.Add(new DrugInteractionAlertDto
            {
                Severity = "moderate",
                InteractionType = "drug-drug",
                Drug1 = payload.DrugName,
                Drug2 = "Aspirin",
                Description = "Concurrent use of ibuprofen and aspirin may reduce the antiplatelet effect of aspirin.",
                ClinicalEffect = "Increased risk of cardiovascular events if aspirin is being used for cardioprotection.",
                RequiresOverride = false
            });
        }

        return Task.FromResult(new ErxSendResult
        {
            Success = true,
            ErxPrescriptionId = rxId,
            SurescriptsMessageId = $"MOCK-SS-{Guid.NewGuid():N}",
            ErxStatus = "Sent",
            InteractionAlerts = alerts
        });
    }

    public Task<ErxCancelResult> CancelPrescriptionAsync(
        string erxPrescriptionId, string reason, CancellationToken ct = default)
    {
        _logger.LogInformation("[Mock eRx] Cancelled prescription {RxId}: {Reason}", erxPrescriptionId, reason);
        return Task.FromResult(new ErxCancelResult { Success = true });
    }

    public Task<List<DrugInteractionAlertDto>> CheckInteractionsAsync(
        ErxInteractionCheckPayload payload, CancellationToken ct = default)
    {
        _logger.LogInformation("[Mock eRx] Checking interactions for {Drug}", payload.RxNormCode);
        return Task.FromResult(new List<DrugInteractionAlertDto>());
    }

    public Task<List<MedicationHistoryDto>> GetMedicationHistoryAsync(
        string erxPatientId, CancellationToken ct = default)
    {
        return Task.FromResult(new List<MedicationHistoryDto>
        {
            new()
            {
                DrugName = "Amoxicillin 500mg Capsule",
                RxNormCode = "308182",
                Strength = "500mg",
                Directions = "Take 1 capsule by mouth 3 times daily for 7 days",
                Prescriber = "Dr. Smith, DDS",
                Pharmacy = "CVS Pharmacy",
                LastFillDate = DateTime.UtcNow.AddDays(-30),
                Source = "Pharmacy",
                IsActive = false
            },
            new()
            {
                DrugName = "Ibuprofen 600mg Tablet",
                RxNormCode = "197806",
                Strength = "600mg",
                Directions = "Take 1 tablet by mouth every 6 hours as needed for pain",
                Prescriber = "Dr. Smith, DDS",
                Pharmacy = "Walgreens",
                LastFillDate = DateTime.UtcNow.AddDays(-14),
                Source = "PBM",
                IsActive = true
            }
        });
    }

    public Task<List<PharmacyDto>> SearchPharmaciesAsync(
        PharmacySearchRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new List<PharmacyDto>
        {
            new()
            {
                NcpdpId = "1234567",
                Name = "CVS Pharmacy #4521",
                Address = "123 Main Street",
                City = "Dallas",
                State = "TX",
                ZipCode = "75201",
                Phone = "(214) 555-0100",
                Fax = "(214) 555-0101",
                DistanceMiles = 0.8,
                AcceptsEpcs = true,
                PharmacyType = PharmacyType.Retail
            },
            new()
            {
                NcpdpId = "2345678",
                Name = "Walgreens #9012",
                Address = "456 Oak Avenue",
                City = "Dallas",
                State = "TX",
                ZipCode = "75202",
                Phone = "(214) 555-0200",
                DistanceMiles = 1.2,
                AcceptsEpcs = true,
                PharmacyType = PharmacyType.Retail
            },
            new()
            {
                NcpdpId = "3456789",
                Name = "PillPack by Amazon Pharmacy",
                Address = "PO Box 1234",
                City = "Manchester",
                State = "NH",
                ZipCode = "03101",
                AcceptsEpcs = false,
                PharmacyType = PharmacyType.MailOrder
            }
        });
    }

    public Task<PrescriptionBenefitDto> CheckBenefitsAsync(
        string erxPatientId, string rxNormCode, string? pharmacyNcpdpId,
        CancellationToken ct = default)
    {
        return Task.FromResult(new PrescriptionBenefitDto
        {
            DrugName = "Amoxicillin 500mg",
            PatientCopay = 10.00m,
            FormularyStatus = "on-formulary",
            TierLevel = "Tier 1 - Generic",
            PriorAuthRequired = false,
            Alternatives = new List<FormularyAlternativeDto>
            {
                new()
                {
                    DrugName = "Amoxicillin 250mg",
                    RxNormCode = "308181",
                    EstimatedCopay = 5.00m,
                    TierLevel = "Tier 1 - Generic"
                }
            }
        });
    }

    public Task<ErxPrescriberRegistrationResult> RegisterPrescriberAsync(
        ErxPrescriberPayload payload, CancellationToken ct = default)
    {
        var clinicianId = $"MOCK-CLN-{Guid.NewGuid():N[..8]}";
        _logger.LogInformation("[Mock eRx] Registered prescriber {Name} as {Id}",
            $"{payload.FirstName} {payload.LastName}", clinicianId);

        return Task.FromResult(new ErxPrescriberRegistrationResult
        {
            Success = true,
            ClinicianId = clinicianId,
            RequiresIdentityProofing = payload.EnableEpcs
        });
    }

    public Task<string> GetSsoUrlAsync(string clinicianId, string? patientId, CancellationToken ct = default)
    {
        var url = $"https://mock.dosespot.local/sso?clinician={clinicianId}";
        if (!string.IsNullOrEmpty(patientId))
            url += $"&patient={patientId}";

        return Task.FromResult(url);
    }

    public Task<ErxRefillResult> RespondToRefillAsync(
        string erxPrescriptionId, bool approved, string? denialReason, int? newRefillCount,
        CancellationToken ct = default)
    {
        _logger.LogInformation("[Mock eRx] Refill {Action} for {RxId}",
            approved ? "approved" : "denied", erxPrescriptionId);
        return Task.FromResult(new ErxRefillResult { Success = true });
    }

    public Task<List<ErxNotification>> GetNotificationsAsync(string clinicianId, CancellationToken ct = default)
    {
        return Task.FromResult(new List<ErxNotification>
        {
            new()
            {
                NotificationId = "MOCK-NOTIF-001",
                Type = "RefillRequest",
                PrescriptionId = "MOCK-RX-999",
                Message = "Refill request from CVS Pharmacy for Amoxicillin 500mg - Patient: John Doe",
                Timestamp = DateTime.UtcNow.AddHours(-2),
                Acknowledged = false
            }
        });
    }
}

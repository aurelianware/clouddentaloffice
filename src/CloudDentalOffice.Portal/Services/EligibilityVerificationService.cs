using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

/// <summary>What one eligibility check produced: the front-desk state, the payer's answer if any, and a staff message.</summary>
public sealed record EligibilityVerificationOutcome(
    EligibilityVerificationState State, EligibilityResult? Result, string? Message);

public interface IEligibilityVerificationService
{
    /// <summary>
    /// Builds and routes an eligibility check, classifies the answer, and records it
    /// against the coverage. Validation and availability problems come back as an
    /// outcome, not an exception. A failure to record never hides the payer's answer.
    /// </summary>
    Task<EligibilityVerificationOutcome> VerifyAsync(
        Patient patient, PatientInsurance? coverage, Provider? provider, DateOnly serviceDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EligibilityVerification>> GetHistoryAsync(
        int patientInsuranceId, int take = 10, CancellationToken cancellationToken = default);

    /// <summary>The payer's answer from the coverage's most recent check that confirmed dental coverage, or null.</summary>
    Task<EligibilityResult?> GetLatestVerifiedResultAsync(
        int patientInsuranceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records on a short-lived context of its own, so saving a check never flushes
/// other pending changes on the caller's (circuit-scoped) context.
/// </summary>
public sealed class EligibilityVerificationService(
    CloudDentalDbContext db,
    DbContextOptions<CloudDentalDbContext> dbOptions,
    IPayerTransactionRouter router,
    ITenantProvider tenantProvider,
    TimeProvider time,
    ILogger<EligibilityVerificationService> logger) : IEligibilityVerificationService
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const int MaxReason = 500;

    public async Task<EligibilityVerificationOutcome> VerifyAsync(
        Patient patient, PatientInsurance? coverage, Provider? provider, DateOnly serviceDate,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantProvider.TenantId;
        EligibilityVerificationOutcome outcome;
        try
        {
            var request = EligibilityRequestBuilder.Build(patient, coverage, provider, serviceDate, tenantId);
            var result = await router.CheckEligibilityAsync(request, cancellationToken);
            outcome = Classify(result, serviceDate);
        }
        catch (TreatmentEstimateValidationException ex)
        {
            outcome = new(EligibilityVerificationState.NeedsInfo, null, ex.Message);
        }
        catch (TreatmentEstimateUnavailableException ex)
        {
            outcome = new(EligibilityVerificationState.Unavailable, null, ex.Message);
        }

        // Only a saved coverage has somewhere to record the check.
        if (coverage is { PatientInsuranceId: > 0 })
            await RecordAsync(tenantId, coverage, serviceDate, outcome, cancellationToken);

        return outcome;
    }

    public async Task<IReadOnlyList<EligibilityVerification>> GetHistoryAsync(
        int patientInsuranceId, int take = 10, CancellationToken cancellationToken = default) =>
        await db.EligibilityVerifications.AsNoTracking()
            .Where(x => x.PatientInsuranceId == patientInsuranceId)
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(cancellationToken);

    public async Task<EligibilityResult?> GetLatestVerifiedResultAsync(
        int patientInsuranceId, CancellationToken cancellationToken = default)
    {
        var json = await db.EligibilityVerifications.AsNoTracking()
            .Where(x => x.PatientInsuranceId == patientInsuranceId &&
                        x.State == EligibilityVerificationState.Verified && x.ResultJson != null)
            .OrderByDescending(x => x.Id)
            .Select(x => x.ResultJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<EligibilityResult>(json, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored eligibility result for coverage {PatientInsuranceId} could not be read", patientInsuranceId);
            return null;
        }
    }

    /// <summary>Maps the normalized payer answer onto a front-desk state. The result's own messages stay on the result.</summary>
    public static EligibilityVerificationOutcome Classify(EligibilityResult result, DateOnly serviceDate) => result.CoverageStatus switch
    {
        CoverageStatus.Active when result.DentalCareNotCovered => new(EligibilityVerificationState.NotDental, result,
            "The plan is active but doesn't cover dental care. Ask the patient whether they have a separate dental plan."),
        CoverageStatus.Active => new(EligibilityVerificationState.Verified, result, null),
        CoverageStatus.Inactive => new(EligibilityVerificationState.Inactive, result,
            $"The payer reports this coverage inactive for {serviceDate:d}."),
        _ => new(EligibilityVerificationState.Unconfirmed, result,
            "The payer answered without confirming coverage. Verify by phone or the payer's portal.")
    };

    private async Task RecordAsync(string tenantId, PatientInsurance coverage, DateOnly serviceDate,
        EligibilityVerificationOutcome outcome, CancellationToken cancellationToken)
    {
        var checkedAt = (outcome.Result?.VerifiedAt is { } verified && verified != default ? verified : time.GetUtcNow()).ToUniversalTime();
        var verification = new EligibilityVerification
        {
            TenantId = tenantId,
            PatientInsuranceId = coverage.PatientInsuranceId,
            PatientId = coverage.PatientId,
            ServiceDate = serviceDate,
            State = outcome.State,
            Reason = Truncate(outcome.Message),
            CorrelationId = Truncate(outcome.Result?.CorrelationId, 64),
            Source = Truncate(outcome.Result?.Source, 64),
            CheckedAt = checkedAt,
            ResultJson = outcome.Result is null ? null : JsonSerializer.Serialize(outcome.Result, Json)
        };

        try
        {
            await using (var recorder = new CloudDentalDbContext(dbOptions, tenantProvider))
            {
                recorder.EligibilityVerifications.Add(verification);
                await recorder.SaveChangesAsync(cancellationToken);
                // The tenant query filter applies, so this only touches the caller's own coverage.
                await recorder.PatientInsurances
                    .Where(x => x.PatientInsuranceId == coverage.PatientInsuranceId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.LastVerifiedAt, checkedAt)
                        .SetProperty(x => x.LastVerificationState, outcome.State), cancellationToken);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            // The staff member still gets the payer's answer; only the history is lost.
            logger.LogError(ex, "Could not record eligibility check for coverage {PatientInsuranceId} (tenant {TenantId})",
                coverage.PatientInsuranceId, ClaimLifecycleMapper.SanitizeForLog(tenantId));
            return;
        }

        // Mirror the stored values onto the caller's object and any copy the caller's
        // context tracks, without marking them modified.
        coverage.LastVerifiedAt = checkedAt;
        coverage.LastVerificationState = outcome.State;
        var entry = db.ChangeTracker.Entries<PatientInsurance>()
            .FirstOrDefault(e => e.Entity.PatientInsuranceId == coverage.PatientInsuranceId);
        if (entry is not null)
        {
            SetUnchanged(entry.Property(x => x.LastVerifiedAt), checkedAt);
            SetUnchanged(entry.Property(x => x.LastVerificationState), outcome.State);
        }
    }

    private static void SetUnchanged<T>(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<PatientInsurance, T> property, T value)
    {
        property.CurrentValue = value;
        property.OriginalValue = value;
        property.IsModified = false;
    }

    private static string? Truncate(string? value, int max = MaxReason) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}

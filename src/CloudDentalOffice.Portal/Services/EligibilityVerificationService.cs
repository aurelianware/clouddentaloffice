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

    /// <summary>The newest check for this coverage/date, only if it confirms active dental coverage; a later failure blocks older answers.</summary>
    Task<EligibilityResult?> GetLatestVerifiedResultAsync(
        int patientInsuranceId, DateOnly? serviceDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Each patient's primary active coverage with its last check and, when that check
    /// verified dental coverage, the benefits it reported. Patients without an active
    /// coverage are left out. Two queries regardless of how many patients are asked about.
    /// </summary>
    Task<IReadOnlyDictionary<int, CoverageSnapshot>> GetCoverageSnapshotsAsync(
        IReadOnlyCollection<int> patientIds, CancellationToken cancellationToken = default);
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

    public async Task<IReadOnlyDictionary<int, CoverageSnapshot>> GetCoverageSnapshotsAsync(
        IReadOnlyCollection<int> patientIds, CancellationToken cancellationToken = default)
    {
        if (patientIds.Count == 0) return new Dictionary<int, CoverageSnapshot>();
        var ids = patientIds.Distinct().ToList();

        var coverages = (await db.PatientInsurances.AsNoTracking()
                .Where(x => ids.Contains(x.PatientId) && x.IsActive)
                .Select(x => new
                {
                    x.PatientId, x.PatientInsuranceId, x.SequenceNumber,
                    x.LastVerificationState, x.LastVerifiedAt, PayerName = x.InsurancePlan.PayerName
                })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.PatientId)
            .Select(g => g.OrderBy(x => x.SequenceNumber).ThenBy(x => x.PatientInsuranceId).First())
            .ToList();

        var coverageIds = coverages.Select(x => x.PatientInsuranceId).ToList();
        // Only the latest check counts: a later failed or inactive check retires earlier benefits.
        var latestChecks = await db.EligibilityVerifications.AsNoTracking()
            .Where(x => coverageIds.Contains(x.PatientInsuranceId))
            .GroupBy(x => x.PatientInsuranceId)
            .Select(g => g.Max(x => x.Id))
            .ToListAsync(cancellationToken);
        var verifiedJson = await db.EligibilityVerifications.AsNoTracking()
            .Where(x => latestChecks.Contains(x.Id) && x.State == EligibilityVerificationState.Verified && x.ResultJson != null)
            .Select(x => new { x.PatientInsuranceId, x.ResultJson })
            .ToDictionaryAsync(x => x.PatientInsuranceId, x => x.ResultJson!, cancellationToken);

        return coverages.ToDictionary(c => c.PatientId, c => new CoverageSnapshot(
            c.PatientId, c.PatientInsuranceId, c.PayerName, c.LastVerificationState, c.LastVerifiedAt,
            verifiedJson.TryGetValue(c.PatientInsuranceId, out var json) ? ReadBenefits(json, c.PatientInsuranceId) : null));
    }

    private DentalBenefitSummary? ReadBenefits(string json, int patientInsuranceId)
    {
        try
        {
            var result = JsonSerializer.Deserialize<EligibilityResult>(json, Json);
            return result is { CoverageStatus: CoverageStatus.Active, DentalCareNotCovered: false } ? result.BenefitSummary : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Stored eligibility result for coverage {PatientInsuranceId} could not be read", patientInsuranceId);
            return null;
        }
    }

    public async Task<EligibilityResult?> GetLatestVerifiedResultAsync(
        int patientInsuranceId, DateOnly? serviceDate = null, CancellationToken cancellationToken = default)
    {
        var latest = await db.EligibilityVerifications.AsNoTracking()
            .Where(x => x.PatientInsuranceId == patientInsuranceId && (serviceDate == null || x.ServiceDate == serviceDate))
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken);
        if (latest is not { State: EligibilityVerificationState.Verified, ResultJson: not null }) return null;
        var json = latest.ResultJson;
        try
        {
            var result = JsonSerializer.Deserialize<EligibilityResult>(json, Json);
            return result is { CoverageStatus: CoverageStatus.Active, DentalCareNotCovered: false } ? result : null;
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

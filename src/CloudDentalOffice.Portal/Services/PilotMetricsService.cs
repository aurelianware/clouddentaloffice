using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Auth;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class PilotMetricsOptions
{
    public const string SectionName = "PilotMetrics";

    /// <summary>Price per billable eligibility check used for the cost estimate (clearinghouse list price).</summary>
    [Range(0, 10)] public decimal EligibilityCheckPrice { get; set; } = 0.30m;
}

/// <summary>How the practice did over a period, from what the Portal already records.</summary>
public sealed record PilotMetricsReport(
    DateTime From,
    DateTime To,
    CoverageMetrics Coverage,
    EligibilityCheckMetrics Checks,
    IntakeMetrics Intake,
    ClaimMetrics Claims);

/// <summary>Appointments in the period that have already started.</summary>
public sealed record CoverageMetrics(
    int Appointments,
    int VerifiedBeforeVisit,
    int SelfPay,
    int NotDental,
    int Inactive,
    int NeedsInfo,
    int Unconfirmed,
    int Unavailable,
    int NotChecked)
{
    /// <summary>Share of appointments (self-pay excluded) whose coverage was verified before the visit.</summary>
    public decimal? VerifiedRate => Appointments - SelfPay is > 0 and var n ? (decimal)VerifiedBeforeVisit / n : null;

    /// <summary>Problems found before the visit: medical-only plans, inactive coverage, missing coverage.</summary>
    public int ProblemsCaught => NotDental + Inactive + NeedsInfo;
}

/// <param name="Billed">Answered checks the clearinghouse charges for (answers rejected with AAA 42, 79 or 80 are free).</param>
public sealed record EligibilityCheckMetrics(int Checks, int Answered, int Billed, decimal EstimatedCost);

public sealed record IntakeMetrics(int Sent, int Answered, int AnsweredWithPlan, int AnsweredNoInsurance);

public sealed record ClaimMetrics(
    int Submitted,
    int Rejected,
    int Paid,
    int PartiallyPaid,
    int Denied,
    decimal? AverageDaysToPayment)
{
    /// <summary>Share of submitted claims not rejected at submission.</summary>
    public decimal? AcceptedRate => Submitted > 0 ? (decimal)(Submitted - Rejected) / Submitted : null;
}

public interface IPilotMetricsService
{
    Task<PilotMetricsReport> GetAsync(int days, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pilot numbers for the current practice: coverage verified before visits, problems caught,
/// eligibility checks and their estimated cost, intake answers, and claim outcomes. Reads only;
/// every query goes through the tenant filter. Staff only.
/// </summary>
public sealed class PilotMetricsService(
    CloudDentalDbContext db,
    ITenantProvider tenantProvider,
    IOptions<PilotMetricsOptions> options,
    TimeProvider time) : IPilotMetricsService
{
    public async Task<PilotMetricsReport> GetAsync(int days, CancellationToken cancellationToken = default)
    {
        if (!StaffRoles.CanManagePayers(tenantProvider.User))
            throw new UnauthorizedAccessException("Practice staff access is required.");

        var to = time.GetUtcNow().UtcDateTime;
        var from = to.AddDays(-Math.Clamp(days, 1, 365));

        return new PilotMetricsReport(from, to,
            await CoverageAsync(from, to, cancellationToken),
            await ChecksAsync(from, to, cancellationToken),
            await IntakeAsync(from, to, cancellationToken),
            await ClaimsAsync(from, to, cancellationToken));
    }

    private async Task<CoverageMetrics> CoverageAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var rows = await db.CoverageVerifications.AsNoTracking()
            .Where(x => x.AppointmentStart >= from && x.AppointmentStart < to)
            .Select(x => new { x.State, x.LastCheckedAt, x.AppointmentStart, x.PatientInsuranceId })
            .ToListAsync(cancellationToken);

        int Count(EligibilityVerificationState state) => rows.Count(x => x.State == state);
        return new CoverageMetrics(
            Appointments: rows.Count,
            VerifiedBeforeVisit: rows.Count(x => x.State == EligibilityVerificationState.Verified &&
                                                 x.LastCheckedAt != null && x.LastCheckedAt <= x.AppointmentStart),
            SelfPay: Count(EligibilityVerificationState.SelfPay),
            NotDental: Count(EligibilityVerificationState.NotDental),
            Inactive: Count(EligibilityVerificationState.Inactive),
            // No coverage on file counts as needing information even before its first check.
            NeedsInfo: rows.Count(x => x.State == EligibilityVerificationState.NeedsInfo ||
                                       (x.State == null && x.PatientInsuranceId == null)),
            Unconfirmed: Count(EligibilityVerificationState.Unconfirmed),
            Unavailable: Count(EligibilityVerificationState.Unavailable),
            NotChecked: rows.Count(x => x.State == null && x.PatientInsuranceId != null));
    }

    /// <summary>How <see cref="EligibilityResult.Billable"/> = false appears in <c>EligibilityVerification.ResultJson</c>.</summary>
    internal const string NotBilledMarker = "\"billable\":false";

    private async Task<EligibilityCheckMetrics> ChecksAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var fromOffset = new DateTimeOffset(from, TimeSpan.Zero);
        var toOffset = new DateTimeOffset(to, TimeSpan.Zero);
        // SQLite can't compare DateTimeOffset in SQL, so the time window is applied in memory over two
        // projected columns. One row per check keeps this small.
        // The stored result marks answers the clearinghouse doesn't charge for; matching the text keeps
        // the projection to two small columns and a flag instead of every result document.
        var checks = (await db.EligibilityVerifications.AsNoTracking()
                .Select(x => new { x.Source, x.CheckedAt, NotBilled = x.ResultJson != null && x.ResultJson.Contains(NotBilledMarker) })
                .ToListAsync(cancellationToken))
            .Where(x => x.CheckedAt >= fromOffset && x.CheckedAt < toOffset)
            .ToList();
        // Checks stopped before sending were never answered or billed.
        var answered = checks.Count(x => x.Source != null);
        var billed = checks.Count(x => x.Source != null && !x.NotBilled);
        return new EligibilityCheckMetrics(checks.Count, answered, billed, billed * options.Value.EligibilityCheckPrice);
    }

    private async Task<IntakeMetrics> IntakeAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var rows = await db.CoverageIntakeRequests.AsNoTracking()
            .Where(x => x.SentAt != null && x.SentAt >= from && x.SentAt < to)
            .Select(x => new { x.AnsweredAt, x.Answer })
            .ToListAsync(cancellationToken);
        return new IntakeMetrics(
            Sent: rows.Count,
            Answered: rows.Count(x => x.AnsweredAt != null),
            AnsweredWithPlan: rows.Count(x => x.Answer == CoverageIntakeAnswer.Plan),
            AnsweredNoInsurance: rows.Count(x => x.Answer == CoverageIntakeAnswer.NoInsurance));
    }

    private async Task<ClaimMetrics> ClaimsAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var rows = await db.Claims.AsNoTracking()
            .Where(x => x.SubmittedDate != null && x.SubmittedDate >= from && x.SubmittedDate < to)
            .Select(x => new { x.Status, x.ServiceDateFrom, x.FinancialsPostedAt })
            .ToListAsync(cancellationToken);
        var paidDays = rows
            .Where(x => x.FinancialsPostedAt != null && (x.Status == "Paid" || x.Status == "PartiallyPaid"))
            .Select(x => (decimal)(x.FinancialsPostedAt!.Value - x.ServiceDateFrom).TotalDays)
            .ToList();
        return new ClaimMetrics(
            Submitted: rows.Count,
            Rejected: rows.Count(x => x.Status == "Rejected"),
            Paid: rows.Count(x => x.Status == "Paid"),
            PartiallyPaid: rows.Count(x => x.Status == "PartiallyPaid"),
            Denied: rows.Count(x => x.Status == "Denied"),
            AverageDaysToPayment: paidDays.Count > 0 ? Math.Round(paidDays.Average(), 1) : null);
    }
}

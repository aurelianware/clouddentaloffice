using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SchedStatus = CloudDentalOffice.Contracts.Scheduling.AppointmentStatus;

namespace CloudDentalOffice.Portal.Services;

public sealed class CoverageVerificationOptions
{
    public const string SectionName = "CoverageVerification";

    /// <summary>Off by default: every check is billed to the practice's clearinghouse account.</summary>
    public bool Enabled { get; set; }

    [Range(1, 1440)] public int PollIntervalMinutes { get; set; } = 15;

    /// <summary>How far ahead appointments are picked up. The first check runs as soon as one appears.</summary>
    [Range(1, 30)] public int HorizonDays { get; set; } = 14;

    /// <summary>Unavailable checks retried before the row waits for the next checkpoint.</summary>
    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;

    /// <summary>First retry delay after an Unavailable check; doubles with each attempt.</summary>
    [Range(1, 1440)] public int RetryMinutes { get; set; } = 30;

    /// <summary>A Verified answer at least this recent at T-72h is not re-checked then.</summary>
    [Range(0, 90)] public int VerifiedFreshDays { get; set; } = 7;

    /// <summary>Patients added this recently before the visit get a morning-of re-check.</summary>
    [Range(0, 365)] public int NewPatientDays { get; set; } = 30;

    [Range(1, 60)] public int LeaseMinutes { get; set; } = 5;

    /// <summary>The practice's time zone for service dates and "morning of". Defaults to the host's.</summary>
    public string? TimeZoneId { get; set; }

    public TimeZoneInfo ResolveTimeZone(TimeProvider time) =>
        string.IsNullOrWhiteSpace(TimeZoneId) ? time.LocalTimeZone : TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

// ── Schedule feed ──────────────────────────────────────────────────────────

public sealed record ScheduledAppointment(Guid Id, int PatientId, int ProviderId, DateTime StartUtc, SchedStatus Status);

/// <param name="Truncated">The service capped the answer, so absent appointments may still exist.</param>
public sealed record ScheduledAppointmentWindow(IReadOnlyList<ScheduledAppointment> Appointments, bool Truncated);

public interface IScheduledAppointmentFeed
{
    /// <summary>The tenant's appointments starting in [from, to], read with a token for exactly that tenant.</summary>
    Task<ScheduledAppointmentWindow> GetAsync(string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);
}

/// <summary>
/// Reads appointments from the SchedulingService for a background worker. There is
/// no signed-in user, so each request carries a short-lived token minted for the
/// explicit tenant with no roles, which the scheduling tenant policy accepts.
/// </summary>
public sealed class SchedulingAppointmentFeed(HttpClient http, IConfiguration configuration) : IScheduledAppointmentFeed
{
    /// <summary>The scheduling list endpoint returns at most this many appointments.</summary>
    internal const int PageLimit = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ScheduledAppointmentWindow> GetAsync(string tenantId, DateTime fromUtc, DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var url = $"/api/appointments?from={Uri.EscapeDataString(fromUtc.ToString("O"))}&to={Uri.EscapeDataString(toUtc.ToString("O"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        SchedulingTenantAuthorizationHandler.AddTenantToken(request, tenantId, [], configuration);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dtos = await response.Content.ReadFromJsonAsync<List<AppointmentDto>>(Json, cancellationToken) ?? [];
        var appointments = dtos.Select(x => new ScheduledAppointment(x.Id, x.PatientId, x.ProviderId,
            x.StartTime.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(x.StartTime, DateTimeKind.Utc) : x.StartTime.ToUniversalTime(),
            x.Status)).ToList();
        return new(appointments, dtos.Count >= PageLimit);
    }
}

// ── Sweep (one tenant) ─────────────────────────────────────────────────────

public sealed record CoverageSweepResult(bool ScheduleRead, int Checked);

public interface ICoverageVerificationSweep
{
    /// <summary>Syncs the tenant's upcoming appointments and runs the checks that are due. Runs under a pinned tenant.</summary>
    Task<CoverageSweepResult> RunAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Brings one appointment up to date with its coverage and checks it: always when
    /// <paramref name="force"/> is set, otherwise only if a check is due (for example the
    /// coverage was just edited). Null if the row isn't this tenant's.
    /// </summary>
    Task<CoverageVerification?> CheckAsync(long id, bool force, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps one <see cref="CoverageVerification"/> per upcoming appointment and
/// decides when each needs a payer check:
/// <list type="bullet">
/// <item>when the appointment first appears, or its coverage is added, changed or edited;</item>
/// <item>at T-72h, unless a Verified answer is recent enough;</item>
/// <item>on the morning of the visit, for patients new to the practice;</item>
/// <item>after an Unavailable check, with backoff, up to <see cref="CoverageVerificationOptions.MaxAttempts"/>.</item>
/// </list>
/// "No dental coverage" and "inactive" answers wait for staff to change the coverage:
/// asking again would bill the practice for the same answer. Checks go through
/// <see cref="IEligibilityVerificationService"/>, so they are recorded exactly like manual ones.
/// </summary>
public sealed class CoverageVerificationSweep(
    CloudDentalDbContext db,
    IScheduledAppointmentFeed feed,
    IEligibilityVerificationService verification,
    ITenantProvider tenantProvider,
    IOptions<CoverageVerificationOptions> options,
    TimeProvider time,
    ILogger<CoverageVerificationSweep> logger) : ICoverageVerificationSweep
{
    public const string NoCoverageReason = "No dental coverage on file. Ask the patient for their dental plan.";
    public const string UnknownPatientReason = "The appointment's patient wasn't found in this practice's records.";

    private const int MaxReason = 500;

    // Appointments that still need coverage. Requested (unconfirmed web intake) and
    // Rescheduled (replaced by a new appointment) are not visits yet or anymore.
    private static readonly HashSet<SchedStatus> Upcoming =
        [SchedStatus.Scheduled, SchedStatus.Confirmed, SchedStatus.CheckedIn, SchedStatus.InProgress];

    private CoverageVerificationOptions Options => options.Value;

    public async Task<CoverageSweepResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var now = time.GetUtcNow().UtcDateTime;
        var zone = Options.ResolveTimeZone(time);
        var scheduleRead = await SyncScheduleAsync(tenantId, now, zone, cancellationToken);
        var checkedCount = await CheckDueAsync(now, zone, onlyId: null, force: false, cancellationToken);
        return new(scheduleRead, checkedCount);
    }

    public async Task<CoverageVerification?> CheckAsync(long id, bool force, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        var now = time.GetUtcNow().UtcDateTime;
        await CheckDueAsync(now, Options.ResolveTimeZone(time), onlyId: id, force, cancellationToken);
        return await db.CoverageVerifications.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    private string RequireTenant()
    {
        var tenantId = tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Coverage verification must run for an explicit tenant.");
        return tenantId;
    }

    // ── Schedule sync ──

    /// <returns>False when the schedule couldn't be read; existing rows are still checked.</returns>
    private async Task<bool> SyncScheduleAsync(string tenantId, DateTime now, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var horizonEnd = now.AddDays(Options.HorizonDays);
        var appointments = new Dictionary<Guid, ScheduledAppointment>();
        var truncated = false;
        // One day at a time keeps each answer under the service's page limit.
        for (var from = now; from < horizonEnd; from = from.AddDays(1))
        {
            var to = from.AddDays(1) < horizonEnd ? from.AddDays(1) : horizonEnd;
            ScheduledAppointmentWindow window;
            try
            {
                window = await feed.GetAsync(tenantId, from, to, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not read the schedule for tenant {TenantId}; checking known appointments only.",
                    ClaimLifecycleMapper.SanitizeForLog(tenantId));
                return false;
            }
            truncated |= window.Truncated;
            foreach (var appointment in window.Appointments)
                appointments[appointment.Id] = appointment;
        }

        var ids = appointments.Keys.ToList();
        var rows = await db.CoverageVerifications
            .Where(x => x.ClosedAt == null || ids.Contains(x.AppointmentId))
            .ToListAsync(cancellationToken);
        var byAppointment = rows.ToDictionary(x => x.AppointmentId);
        var todayStart = LocalDayStart(now, zone);

        foreach (var appointment in appointments.Values)
        {
            byAppointment.TryGetValue(appointment.Id, out var row);
            if (!Upcoming.Contains(appointment.Status))
            {
                if (row is { ClosedAt: null }) Close(row, now);
                continue;
            }

            if (row is null)
            {
                db.CoverageVerifications.Add(new CoverageVerification
                {
                    TenantId = tenantId,
                    AppointmentId = appointment.Id,
                    PatientId = appointment.PatientId,
                    ProviderId = appointment.ProviderId,
                    AppointmentStart = appointment.StartUtc,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                continue;
            }

            if (row.PatientId != appointment.PatientId)
            {
                // Booked for someone else now: nothing known about their coverage yet.
                row.PatientId = appointment.PatientId;
                Reset(row);
            }
            row.ProviderId = appointment.ProviderId;
            row.AppointmentStart = appointment.StartUtc;
            row.ClosedAt = null;
            Touch(row, now);
        }

        foreach (var row in rows.Where(x => x.ClosedAt is null && !appointments.ContainsKey(x.AppointmentId)))
        {
            // Earlier today's appointments aren't in the feed (it starts at now), so
            // only a start inside the window read proves the appointment is gone.
            var past = row.AppointmentStart < todayStart;
            var gone = !truncated && row.AppointmentStart >= now && row.AppointmentStart <= horizonEnd;
            if (past || gone) Close(row, now);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // Another replica added the same appointment first; the next sweep picks it up.
            logger.LogInformation(ex, "Schedule sync for tenant {TenantId} raced another instance.",
                ClaimLifecycleMapper.SanitizeForLog(tenantId));
            db.ChangeTracker.Clear();
            return false;
        }
    }

    // ── Checks ──

    private async Task<int> CheckDueAsync(DateTime now, TimeZoneInfo zone, long? onlyId, bool force, CancellationToken cancellationToken)
    {
        var todayStart = LocalDayStart(now, zone);
        var query = db.CoverageVerifications.Where(x => x.ClosedAt == null && x.AppointmentStart >= todayStart);
        if (onlyId is { } id) query = db.CoverageVerifications.Where(x => x.Id == id);
        var rows = await query.OrderBy(x => x.AppointmentStart).ToListAsync(cancellationToken);
        if (rows.Count == 0) return 0;

        var patientIds = rows.Select(x => x.PatientId).Distinct().ToList();
        // Untracked: saving rows must never write patient or coverage entities.
        var patients = await db.Patients.AsNoTracking()
            .Include(p => p.Insurances).ThenInclude(i => i.InsurancePlan)
            .Where(p => patientIds.Contains(p.PatientId))
            .ToDictionaryAsync(p => p.PatientId, cancellationToken);
        var providers = await db.Providers.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);

        var checkedCount = 0;
        foreach (var row in rows)
        {
            if (!patients.TryGetValue(row.PatientId, out var patient))
            {
                SetState(row, EligibilityVerificationState.NeedsInfo, UnknownPatientReason, now);
                continue;
            }

            var coverage = patient.PrimaryInsurance;
            await ReconcileAsync(row, coverage, now, cancellationToken);
            if (coverage is null || !(force || IsDue(row, patient, now, zone))) continue;
            if (!await TryLeaseAsync(row, now, cancellationToken)) continue;

            var before = coverage.LastVerifiedAt;
            var outcome = await verification.VerifyAsync(
                patient, coverage, ProviderFor(row, providers), ServiceDate(row, zone), cancellationToken);
            Apply(row, outcome, coverage.LastVerifiedAt != before ? coverage.LastVerifiedAt : null, now);
            checkedCount++;
            // Save after each check so an answer the practice paid for is never lost.
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return checkedCount;
    }

    /// <summary>Brings the row in line with the patient's current primary coverage and its latest answer.</summary>
    private async Task ReconcileAsync(CoverageVerification row, PatientInsurance? coverage, DateTime now, CancellationToken cancellationToken)
    {
        if (coverage is null)
        {
            if (row.PatientInsuranceId is not null) Reset(row);
            row.PatientInsuranceId = null;
            SetState(row, EligibilityVerificationState.NeedsInfo, NoCoverageReason, now);
            return;
        }

        if (row.PatientInsuranceId != coverage.PatientInsuranceId)
        {
            Reset(row);
            row.PatientInsuranceId = coverage.PatientInsuranceId;
            Touch(row, now);
        }

        var verifiedAt = coverage.LastVerifiedAt?.UtcDateTime;
        if (verifiedAt is { } at && (row.LastCheckedAt is null || at > row.LastCheckedAt))
        {
            // A newer answer for this coverage: a manual check, or another appointment's.
            var reason = await db.EligibilityVerifications.AsNoTracking()
                .Where(x => x.PatientInsuranceId == coverage.PatientInsuranceId)
                .OrderByDescending(x => x.Id)
                .Select(x => x.Reason)
                .FirstOrDefaultAsync(cancellationToken);
            row.State = coverage.LastVerificationState;
            row.Reason = reason;
            row.LastCheckedAt = at;
            row.Attempts = 0;
            row.NextCheckAt = null;
            Touch(row, now);
        }
        else if (verifiedAt is null && row.LastCheckedAt is { } last && coverage.ModifiedDate > last)
        {
            // Edited since the last check (editing clears the coverage's answer): ask again.
            Reset(row);
            Touch(row, now);
        }
    }

    private bool IsDue(CoverageVerification row, Patient patient, DateTime now, TimeZoneInfo zone)
    {
        if (row.State is null) return true;

        var last = row.LastCheckedAt ?? DateTime.MinValue;
        var threeDaysOut = row.AppointmentStart.AddHours(-72);
        var morningOf = LocalDayStart(row.AppointmentStart, zone);
        var created = patient.CreatedDate.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(patient.CreatedDate, DateTimeKind.Utc) : patient.CreatedDate.ToUniversalTime();
        var newPatient = created >= row.AppointmentStart.AddDays(-Options.NewPatientDays);
        bool Passed(DateTime checkpoint) => now >= checkpoint && last < checkpoint;

        return row.State switch
        {
            EligibilityVerificationState.Unavailable when row.Attempts < Options.MaxAttempts =>
                row.NextCheckAt is null || row.NextCheckAt <= now,
            EligibilityVerificationState.Unavailable => Passed(threeDaysOut) || Passed(morningOf),
            EligibilityVerificationState.Verified =>
                (now >= threeDaysOut && last < threeDaysOut.AddDays(-Options.VerifiedFreshDays)) ||
                (newPatient && Passed(morningOf)),
            EligibilityVerificationState.NeedsInfo or EligibilityVerificationState.Unconfirmed =>
                Passed(threeDaysOut) || (newPatient && Passed(morningOf)),
            _ => false
        };
    }

    /// <summary>Claims the row for this instance, so two replicas never check the same appointment at once.</summary>
    private async Task<bool> TryLeaseAsync(CoverageVerification row, DateTime now, CancellationToken cancellationToken)
    {
        var until = now.AddMinutes(Options.LeaseMinutes);
        var claimed = await db.CoverageVerifications
            .Where(x => x.Id == row.Id && (x.LockedUntil == null || x.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedUntil, until), cancellationToken);
        if (claimed != 1) return false;
        // Match the tracked copy to the database, so releasing the lease is saved.
        db.Entry(row).Property(x => x.LockedUntil).OriginalValue = until;
        row.LockedUntil = until;
        return true;
    }

    private void Apply(CoverageVerification row, EligibilityVerificationOutcome outcome, DateTimeOffset? recordedAt, DateTime now)
    {
        row.State = outcome.State;
        row.Reason = Truncate(outcome.Message);
        // The time stamped on the coverage when recording worked, so the row doesn't later
        // mistake its own check for a newer one.
        row.LastCheckedAt = recordedAt?.UtcDateTime ?? now;
        if (outcome.State == EligibilityVerificationState.Unavailable)
        {
            row.Attempts++;
            var delay = Options.RetryMinutes * Math.Pow(2, Math.Max(0, row.Attempts - 1));
            row.NextCheckAt = now.AddMinutes(Math.Min(delay, TimeSpan.FromDays(1).TotalMinutes));
        }
        else
        {
            row.Attempts = 0;
            row.NextCheckAt = null;
        }
        row.LockedUntil = null;
        Touch(row, now);
    }

    private static Provider? ProviderFor(CoverageVerification row, IReadOnlyList<Provider> providers) =>
        providers.FirstOrDefault(p => p.ProviderId == row.ProviderId && !string.IsNullOrWhiteSpace(p.NPI))
        ?? providers.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.NPI))
        ?? providers.FirstOrDefault(p => p.ProviderId == row.ProviderId);

    private static void SetState(CoverageVerification row, EligibilityVerificationState state, string reason, DateTime now)
    {
        if (row.State == state && row.Reason == reason) return;
        row.State = state;
        row.Reason = reason;
        row.Attempts = 0;
        row.NextCheckAt = null;
        Touch(row, now);
    }

    private static void Reset(CoverageVerification row)
    {
        row.State = null;
        row.Reason = null;
        row.LastCheckedAt = null;
        row.Attempts = 0;
        row.NextCheckAt = null;
    }

    private static void Close(CoverageVerification row, DateTime now)
    {
        row.ClosedAt = now;
        Touch(row, now);
    }

    private static void Touch(CoverageVerification row, DateTime now) => row.UpdatedAt = now;

    private static DateOnly ServiceDate(CoverageVerification row, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(row.AppointmentStart, DateTimeKind.Utc), zone));

    /// <summary>The UTC instant of local midnight on the day <paramref name="utc"/> falls on.</summary>
    public static DateTime LocalDayStart(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Date;
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).UtcDateTime;
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= MaxReason ? value : value[..MaxReason];
}

// ── Runner and worker ──────────────────────────────────────────────────────

public interface ICoverageVerificationRunner
{
    /// <summary>Sweeps every practice with an active clearinghouse connection, each in its own pinned scope.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken = default);
}

public sealed class CoverageVerificationRunner(IServiceProvider services, ILogger<CoverageVerificationRunner> logger)
    : ICoverageVerificationRunner
{
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        List<string> tenants;
        await using (var scope = services.CreateAsyncScope())
        {
            // Only practices that can check eligibility; each opts in by connecting a clearinghouse.
            tenants = await scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>()
                .TenantClearinghouseConnections.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Status == ClearinghouseConnectionStatus.Active && x.TenantId != "")
                .Select(x => x.TenantId).Distinct().ToListAsync(cancellationToken);
        }

        foreach (var tenantId in tenants)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<PinnedTenantScope>().Pin(tenantId);
                var result = await scope.ServiceProvider.GetRequiredService<ICoverageVerificationSweep>().RunAsync(cancellationToken);
                if (result.Checked > 0)
                    logger.LogInformation("Coverage verification ran {Checked} checks for tenant {TenantId}.",
                        result.Checked, ClaimLifecycleMapper.SanitizeForLog(tenantId));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One practice's failure never stops the others.
                logger.LogError(ex, "Coverage verification failed for tenant {TenantId} with {FailureKind}.",
                    ClaimLifecycleMapper.SanitizeForLog(tenantId), ex.GetType().Name);
            }
        }
    }
}

public sealed class CoverageVerificationWorker(
    ICoverageVerificationRunner runner, IOptions<CoverageVerificationOptions> options,
    ILogger<CoverageVerificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Coverage verification worker is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.PollIntervalMinutes));
        do
        {
            try { await runner.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Coverage verification sweep failed with {FailureKind}.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

// ── Staff queue ────────────────────────────────────────────────────────────

public sealed record CoverageQueueItem(
    long Id, DateTime AppointmentStart, int PatientId, string PatientName, string? PlanName,
    EligibilityVerificationState? State, string? Reason, DateTime? LastCheckedAt, int Attempts);

public interface ICoverageVerificationQueue
{
    /// <summary>Open appointments from the start of today through <paramref name="days"/> days ahead.</summary>
    Task<IReadOnlyList<CoverageQueueItem>> GetAsync(int days, bool includeVerified, CancellationToken cancellationToken = default);

    /// <summary>Checks one appointment now, the same way the worker would.</summary>
    Task CheckNowAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Picks up coverage changes for one appointment; checks only if that makes a check due.</summary>
    Task RefreshAsync(long id, CancellationToken cancellationToken = default);
}

public sealed class CoverageVerificationQueue(
    CloudDentalDbContext db,
    IServiceProvider services,
    ITenantProvider tenantProvider,
    IOptions<CoverageVerificationOptions> options,
    TimeProvider time) : ICoverageVerificationQueue
{
    public async Task<IReadOnlyList<CoverageQueueItem>> GetAsync(int days, bool includeVerified,
        CancellationToken cancellationToken = default)
    {
        var zone = options.Value.ResolveTimeZone(time);
        var from = CoverageVerificationSweep.LocalDayStart(time.GetUtcNow().UtcDateTime, zone);
        var to = from.AddDays(Math.Clamp(days, 1, 30));
        var rows = await db.CoverageVerifications.AsNoTracking()
            .Where(x => x.ClosedAt == null && x.AppointmentStart >= from && x.AppointmentStart < to)
            .Where(x => includeVerified || x.State == null || x.State != EligibilityVerificationState.Verified)
            .OrderBy(x => x.AppointmentStart)
            .ToListAsync(cancellationToken);

        var patientIds = rows.Select(x => x.PatientId).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking()
            .Where(p => patientIds.Contains(p.PatientId))
            .Select(p => new { p.PatientId, p.FirstName, p.LastName })
            .ToDictionaryAsync(p => p.PatientId, cancellationToken);
        var coverageIds = rows.Where(x => x.PatientInsuranceId != null).Select(x => x.PatientInsuranceId!.Value).Distinct().ToList();
        var plans = await db.PatientInsurances.AsNoTracking()
            .Where(c => coverageIds.Contains(c.PatientInsuranceId))
            .Select(c => new { c.PatientInsuranceId, c.InsurancePlan.PayerName })
            .ToDictionaryAsync(c => c.PatientInsuranceId, c => c.PayerName, cancellationToken);

        return rows.Select(x => new CoverageQueueItem(
            x.Id, x.AppointmentStart, x.PatientId,
            patients.TryGetValue(x.PatientId, out var p) ? $"{p.FirstName} {p.LastName}" : $"Patient #{x.PatientId}",
            x.PatientInsuranceId is { } c && plans.TryGetValue(c, out var plan) ? plan : null,
            x.State, x.Reason, x.LastCheckedAt, x.Attempts)).ToList();
    }

    public Task CheckNowAsync(long id, CancellationToken cancellationToken = default) =>
        CheckAsync(id, force: true, cancellationToken);

    public Task RefreshAsync(long id, CancellationToken cancellationToken = default) =>
        CheckAsync(id, force: false, cancellationToken);

    private async Task CheckAsync(long id, bool force, CancellationToken cancellationToken)
    {
        // A scope of its own, pinned to the signed-in practice: the same code path as
        // the worker, and nothing pending on the page's context gets saved with it.
        var tenantId = tenantProvider.TenantId;
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<PinnedTenantScope>().Pin(tenantId);
        await scope.ServiceProvider.GetRequiredService<ICoverageVerificationSweep>().CheckAsync(id, force, cancellationToken);
    }
}

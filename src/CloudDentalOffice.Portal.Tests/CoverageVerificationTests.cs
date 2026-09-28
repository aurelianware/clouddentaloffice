using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedStatus = CloudDentalOffice.Contracts.Scheduling.AppointmentStatus;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// The coverage verification worker keeps one row per upcoming appointment and
/// checks coverage when it appears, at T-72h, on the morning of for new patients,
/// and after failures, without billing the practice for answers it already has.
/// Synthetic data only.
/// </summary>
public sealed class CoverageVerificationTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const int PatientId = 101;
    private const int PlanId = 11;
    private const int CoverageId = 501;
    private const int ProviderId = 7;

    // Noon UTC, so the UTC practice day and the appointment days are unambiguous.
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CloudDentalDbContext> _options;
    private readonly MutableClock _clock = new(Start);
    private readonly FakeFeed _feed = new();
    private readonly FakeRouter _router = new();
    private readonly CoverageVerificationOptions _settings = new() { Enabled = true, TimeZoneId = "UTC" };

    public CoverageVerificationTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        using var db = Db(Tenant);
        db.Database.EnsureCreated();
        // Created long before the appointments below: an established patient.
        db.Patients.Add(Patient(PatientId, Tenant, created: Start.UtcDateTime.AddYears(-2)));
        db.InsurancePlans.Add(new InsurancePlan
        {
            InsurancePlanId = PlanId, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true
        });
        db.PatientInsurances.Add(Coverage(CoverageId, PatientId));
        db.Providers.Add(new Provider
        {
            ProviderId = ProviderId, TenantId = Tenant, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist", IsActive = true
        });
        db.SaveChanges();
        _router.Result = Result(CoverageStatus.Active);
    }

    public void Dispose() => _connection.Dispose();

    // ── Booking and repeat sweeps ──────────────────────────────────────────

    [Fact]
    public async Task New_appointment_is_tracked_and_checked_once()
    {
        var appointment = _feed.Add(Start.AddDays(5));

        var result = await Sweep();

        Assert.True(result.ScheduleRead);
        Assert.Equal(1, result.Checked);
        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.Verified, row.State);
        Assert.Equal(CoverageId, row.PatientInsuranceId);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(0, row.Attempts);

        await using var db = Db(Tenant);
        var coverage = await db.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId);
        Assert.Equal(coverage.LastVerifiedAt!.Value.UtcDateTime, row.LastCheckedAt);
        Assert.Null(coverage.VerificationLockedUntil);
        Assert.Single(await db.EligibilityVerifications.ToListAsync());

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(0, (await Sweep()).Checked);
        Assert.Equal(1, _router.Calls);
    }

    [Fact]
    public async Task Service_date_is_the_practice_local_date()
    {
        _settings.TimeZoneId = "America/Phoenix";
        // 01:00 UTC on the 10th is 18:00 on the 9th in Arizona.
        _feed.Add(new DateTimeOffset(2026, 10, 10, 1, 0, 0, TimeSpan.Zero));

        await Sweep();

        Assert.Equal(new DateOnly(2026, 10, 9), Assert.Single(_router.Requests).ServiceDate);
    }

    [Fact]
    public async Task Two_appointments_on_one_coverage_share_one_check()
    {
        var first = _feed.Add(Start.AddDays(4));
        var second = _feed.Add(Start.AddDays(6));

        await Sweep();

        Assert.Equal(1, _router.Calls);
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(first)).State);
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(second)).State);
    }

    [Fact]
    public async Task Appointments_beyond_the_horizon_are_not_read()
    {
        _feed.Add(Start.AddDays(_settings.HorizonDays + 2));

        await Sweep();

        Assert.Equal(0, _router.Calls);
        await using var db = Db(Tenant);
        Assert.Empty(await db.CoverageVerifications.ToListAsync());
    }

    // ── No coverage, coverage changes, manual checks ───────────────────────

    [Fact]
    public async Task Patient_without_coverage_needs_info_until_coverage_is_added()
    {
        await using (var db = Db(Tenant))
        {
            db.Patients.Add(Patient(102, Tenant, created: Start.UtcDateTime));
            await db.SaveChangesAsync();
        }
        var appointment = _feed.Add(Start.AddDays(5), patientId: 102);

        await Sweep();

        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.NeedsInfo, row.State);
        Assert.Equal(CoverageVerificationSweep.NoCoverageReason, row.Reason);
        Assert.Null(row.PatientInsuranceId);
        Assert.Equal(0, _router.Calls);

        await using (var db = Db(Tenant))
        {
            db.PatientInsurances.Add(Coverage(502, 102));
            await db.SaveChangesAsync();
        }
        _clock.Advance(TimeSpan.FromMinutes(15));
        await Sweep();

        row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.Verified, row.State);
        Assert.Equal(502, row.PatientInsuranceId);
        Assert.Equal(1, _router.Calls);
    }

    [Fact]
    public async Task Not_dental_waits_for_the_coverage_to_change()
    {
        _router.Result = Result(CoverageStatus.Active) with { DentalCareNotCovered = true };
        var appointment = _feed.Add(Start.AddDays(5));
        await Sweep();
        Assert.Equal(EligibilityVerificationState.NotDental, (await Row(appointment)).State);

        // Past T-72h: asking again would bill the practice for the same answer.
        _clock.Advance(TimeSpan.FromDays(3));
        await Sweep();
        Assert.Equal(1, _router.Calls);

        // Staff edit the coverage, which clears its last answer.
        _router.Result = Result(CoverageStatus.Active);
        await EditCoverage();
        await Sweep();

        Assert.Equal(2, _router.Calls);
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(appointment)).State);
    }

    [Fact]
    public async Task A_manual_check_for_another_month_is_not_adopted()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        await Sweep();

        // Checked from the treatment-plan screen for a visit next month.
        _clock.Advance(TimeSpan.FromHours(1));
        await ManualCheck(CoverageStatus.Inactive, new DateOnly(2026, 11, 20));
        await Sweep();

        Assert.Equal(EligibilityVerificationState.Verified, (await Row(appointment)).State);
        Assert.Equal(2, _router.Calls);
    }

    [Fact]
    public async Task A_newer_manual_check_is_adopted_without_calling_the_payer()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        await Sweep();

        // A staff member checks from the insurance dialog an hour later.
        _clock.Advance(TimeSpan.FromHours(1));
        await ManualCheck(CoverageStatus.Inactive, DateOnly.FromDateTime(Start.UtcDateTime.AddDays(5)));
        Assert.Equal(2, _router.Calls);

        await Sweep();

        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.Inactive, row.State);
        Assert.Contains("inactive", row.Reason);
        Assert.Equal(2, _router.Calls);
    }

    // ── Checkpoints ────────────────────────────────────────────────────────

    [Fact]
    public async Task Verified_long_before_the_visit_is_rechecked_at_T_minus_72h()
    {
        var appointment = _feed.Add(Start.AddDays(12));
        await Sweep();

        _clock.Advance(TimeSpan.FromDays(8.5)); // 3.5 days out
        await Sweep();
        Assert.Equal(1, _router.Calls);

        _clock.Advance(TimeSpan.FromDays(0.5) + TimeSpan.FromMinutes(1)); // just past T-72h
        await Sweep();
        Assert.Equal(2, _router.Calls);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await Sweep();
        Assert.Equal(2, _router.Calls);
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(appointment)).State);
    }

    [Fact]
    public async Task Recent_verified_answer_skips_the_T_minus_72h_check()
    {
        _feed.Add(Start.AddDays(5));
        await Sweep();

        _clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromMinutes(1));
        await Sweep();

        Assert.Equal(1, _router.Calls);
    }

    [Fact]
    public async Task New_patient_is_rechecked_on_the_morning_of_the_visit()
    {
        await using (var db = Db(Tenant))
        {
            db.Patients.Add(Patient(103, Tenant, created: Start.UtcDateTime));
            db.PatientInsurances.Add(Coverage(503, 103));
            await db.SaveChangesAsync();
        }
        _feed.Add(Start.AddDays(2), patientId: 103);
        _feed.Add(Start.AddDays(2).AddHours(1)); // established patient, same day
        await Sweep();
        Assert.Equal(2, _router.Calls);

        _clock.Advance(TimeSpan.FromDays(1.5) + TimeSpan.FromMinutes(1)); // just past midnight of the visit day
        await Sweep();

        Assert.Equal(3, _router.Calls);
        Assert.Equal("MBR103", _router.Requests[^1].MemberId);
    }

    // ── Failures ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Unavailable_retries_with_backoff_then_waits_for_the_next_checkpoint()
    {
        _router.Throw = new TreatmentEstimateUnavailableException("The clearinghouse did not respond.");
        var appointment = _feed.Add(Start.AddDays(10));

        await Sweep();
        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.Unavailable, row.State);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(Start.UtcDateTime.AddMinutes(30), row.NextCheckAt);

        _clock.Advance(TimeSpan.FromMinutes(15));
        await Sweep();
        Assert.Equal(1, _router.Calls);

        _clock.Advance(TimeSpan.FromMinutes(16)); // first retry due at +30m
        await Sweep();
        Assert.Equal(2, _router.Calls);

        _clock.Advance(TimeSpan.FromMinutes(61)); // second retry due 60m after that
        await Sweep();
        Assert.Equal(3, _router.Calls);
        Assert.Equal(3, (await Row(appointment)).Attempts);

        _clock.Advance(TimeSpan.FromDays(1));
        await Sweep();
        Assert.Equal(3, _router.Calls);

        _router.Throw = null;
        _clock.Advance(TimeSpan.FromDays(6)); // past T-72h
        await Sweep();
        Assert.Equal(4, _router.Calls);
        row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.Verified, row.State);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task A_coverage_leased_by_another_instance_is_not_checked()
    {
        await using (var db = Db(Tenant))
        {
            var coverage = await db.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId);
            coverage.VerificationLockedUntil = Start.UtcDateTime.AddMinutes(3);
            await db.SaveChangesAsync();
        }
        var appointment = _feed.Add(Start.AddDays(5));

        await Sweep();
        Assert.Equal(0, _router.Calls);
        Assert.Null((await Row(appointment)).State);

        // The other instance's lease expires without being released.
        _clock.Advance(TimeSpan.FromMinutes(5));
        await Sweep();
        Assert.Equal(1, _router.Calls);
    }

    [Fact]
    public async Task A_result_arriving_after_the_appointment_closed_is_not_applied()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        // Another instance closes the row while the payer call is in flight.
        _router.During = () =>
        {
            using var db = Db(Tenant);
            db.CoverageVerifications.Single().ClosedAt = Start.UtcDateTime;
            db.SaveChanges();
        };

        await Sweep();

        var row = await Row(appointment);
        Assert.NotNull(row.ClosedAt);
        Assert.Null(row.State);
        await using var check = Db(Tenant);
        Assert.Null((await check.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId)).VerificationLockedUntil);
    }

    [Fact]
    public async Task Known_appointments_are_still_checked_when_the_schedule_cannot_be_read()
    {
        var appointment = _feed.Add(Start.AddDays(5));
        _router.Throw = new TreatmentEstimateUnavailableException("down");
        await Sweep();
        _router.Throw = null;
        _feed.Fail = true;

        _clock.Advance(TimeSpan.FromMinutes(31));
        var result = await Sweep();

        Assert.False(result.ScheduleRead);
        Assert.Equal(EligibilityVerificationState.Verified, (await Row(appointment)).State);
        Assert.Null((await Row(appointment)).ClosedAt);
    }

    // ── Schedule changes ───────────────────────────────────────────────────

    [Fact]
    public async Task Cancelled_and_removed_appointments_are_closed()
    {
        var cancelled = _feed.Add(Start.AddDays(3));
        var removed = _feed.Add(Start.AddDays(4));
        var kept = _feed.Add(Start.AddDays(5));
        await Sweep();

        _feed.SetStatus(cancelled, SchedStatus.Cancelled);
        _feed.Remove(removed);
        await Sweep();

        Assert.NotNull((await Row(cancelled)).ClosedAt);
        Assert.NotNull((await Row(removed)).ClosedAt);
        Assert.Null((await Row(kept)).ClosedAt);
    }

    [Fact]
    public async Task Appointments_close_once_they_have_started()
    {
        var appointment = _feed.Add(Start.AddHours(2));
        await Sweep();
        Assert.Null((await Row(appointment)).ClosedAt);

        _clock.Advance(TimeSpan.FromHours(3));
        await Sweep();

        Assert.NotNull((await Row(appointment)).ClosedAt);
    }

    [Fact]
    public async Task A_forced_check_never_calls_the_payer_for_a_closed_appointment()
    {
        var appointment = _feed.Add(Start.AddDays(3));
        await Sweep();
        _feed.SetStatus(appointment, SchedStatus.Cancelled);
        await Sweep();
        var id = (await Row(appointment)).Id;

        await using var db = Db(Tenant);
        var sweep = NewSweep(db, Tenant);
        var row = await sweep.CheckAsync(id, force: true);

        Assert.NotNull(row!.ClosedAt);

        Assert.Equal(1, _router.Calls);
    }

    [Fact]
    public async Task Only_the_appointments_own_provider_is_sent()
    {
        await using (var db = Db(Tenant))
        {
            db.Providers.Add(new Provider { ProviderId = 8, TenantId = Tenant, NPI = "", FirstName = "Ari", LastName = "Hygienist", IsActive = true });
            await db.SaveChangesAsync();
        }
        var appointment = _feed.Add(Start.AddDays(5), providerId: 8);

        await Sweep();

        var row = await Row(appointment);
        Assert.Equal(EligibilityVerificationState.NeedsInfo, row.State);
        Assert.Contains("NPI", row.Reason);
        Assert.Equal(0, _router.Calls);
    }

    [Fact]
    public async Task A_truncated_schedule_never_closes_missing_appointments()
    {
        var appointment = _feed.Add(Start.AddDays(3));
        await Sweep();

        _feed.Remove(appointment);
        _feed.Truncated = true;
        await Sweep();

        Assert.Null((await Row(appointment)).ClosedAt);
    }

    [Fact]
    public async Task A_rescheduled_appointment_keeps_its_row_and_moves()
    {
        var appointment = _feed.Add(Start.AddDays(3));
        await Sweep();

        _feed.Move(appointment, Start.AddDays(6));
        await Sweep();

        var row = await Row(appointment);
        Assert.Null(row.ClosedAt);
        Assert.Equal(Start.UtcDateTime.AddDays(6), row.AppointmentStart);
        Assert.Equal(1, _router.Calls);
    }

    // ── Tenancy ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Runner_sweeps_only_connected_practices_each_under_its_own_tenant()
    {
        await using (var db = Db(Tenant))
        {
            db.TenantClearinghouseConnections.AddRange(
                new TenantClearinghouseConnection { TenantId = Tenant, Status = ClearinghouseConnectionStatus.Active, KeyReference = $"stedi-apikey-{Tenant}" },
                new TenantClearinghouseConnection { TenantId = OtherTenant, Status = ClearinghouseConnectionStatus.Pending, KeyReference = $"stedi-apikey-{OtherTenant}" });
            await db.SaveChangesAsync();
        }
        _feed.Add(Start.AddDays(5));

        var services = new ServiceCollection();
        services.AddLogging();
        PinnedTenantScope.Register<DefaultTenantProvider>(services);
        services.AddSingleton(_options);
        services.AddScoped(sp => new CloudDentalDbContext(_options, sp.GetRequiredService<ITenantProvider>()));
        services.AddSingleton<IScheduledAppointmentFeed>(_feed);
        services.AddSingleton<IPayerTransactionRouter>(_router);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton(Options.Create(_settings));
        services.AddScoped<IEligibilityVerificationService, EligibilityVerificationService>();
        services.AddScoped<ICoverageVerificationSweep, CoverageVerificationSweep>();
        services.AddSingleton<ICoverageVerificationRunner, CoverageVerificationRunner>();
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ICoverageVerificationRunner>().RunOnceAsync();

        Assert.Equal([Tenant], _feed.Tenants.Distinct());
        Assert.Equal(Tenant, Assert.Single(_router.Requests).TenantId);
        await using var check = Db(Tenant);
        Assert.Equal(Tenant, (await check.CoverageVerifications.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Rows_are_invisible_to_other_tenants_and_need_an_explicit_tenant()
    {
        _feed.Add(Start.AddDays(5));
        await Sweep();

        await using var other = Db(OtherTenant);
        Assert.Empty(await other.CoverageVerifications.ToListAsync());

        other.CoverageVerifications.Add(new CoverageVerification { AppointmentId = Guid.NewGuid(), PatientId = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.SaveChangesAsync());
    }

    [Fact]
    public void A_scope_cannot_be_repinned_to_another_tenant()
    {
        var scope = new PinnedTenantScope();
        scope.Pin(Tenant);
        scope.Pin(Tenant);
        Assert.Throws<InvalidOperationException>(() => scope.Pin(OtherTenant));
        Assert.Throws<ArgumentException>(() => new PinnedTenantScope().Pin(" "));
    }

    // ── Queue ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Queue_lists_unverified_appointments_in_range()
    {
        await using (var db = Db(Tenant))
        {
            db.Patients.Add(Patient(102, Tenant, created: Start.UtcDateTime));
            await db.SaveChangesAsync();
        }
        _feed.Add(Start.AddDays(1));                  // verified
        _feed.Add(Start.AddDays(2), patientId: 102);  // no coverage
        _feed.Add(Start.AddDays(6), patientId: 102);  // outside 3 days
        await Sweep();

        await using var queueDb = Db(Tenant);
        var queue = new CoverageVerificationQueue(queueDb, new ServiceCollection().BuildServiceProvider(),
            new FixedTenantProvider(Tenant, User("Staff")), Options.Create(_settings), _clock);

        var item = Assert.Single(await queue.GetAsync(3, includeVerified: false));
        Assert.Equal("Rowan Vale", item.PatientName);
        Assert.Equal(EligibilityVerificationState.NeedsInfo, item.State);
        Assert.Null(item.PlanName);

        var all = await queue.GetAsync(3, includeVerified: true);
        Assert.Equal(2, all.Count);
        Assert.Equal("Delta Dental Arizona", all[0].PlanName);
    }

    [Fact]
    public async Task Queue_refuses_non_staff()
    {
        await using var db = Db(Tenant);
        var queue = new CoverageVerificationQueue(db, new ServiceCollection().BuildServiceProvider(),
            new FixedTenantProvider(Tenant, User("Patient")), Options.Create(_settings), _clock);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.GetAsync(3, includeVerified: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => queue.CheckNowAsync(1));
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<CoverageSweepResult> Sweep()
    {
        await using var db = Db(Tenant);
        return await NewSweep(db, Tenant).RunAsync();
    }

    private CoverageVerificationSweep NewSweep(CloudDentalDbContext db, string tenantId)
    {
        var tenant = new FixedTenantProvider(tenantId);
        var verification = new EligibilityVerificationService(db, _options, _router, tenant, _clock,
            NullLogger<EligibilityVerificationService>.Instance);
        return new CoverageVerificationSweep(db, _feed, verification, tenant, Options.Create(_settings), _clock,
            NullLogger<CoverageVerificationSweep>.Instance);
    }

    /// <summary>A staff check from the insurance dialog.</summary>
    private async Task ManualCheck(CoverageStatus status, DateOnly serviceDate)
    {
        await using var db = Db(Tenant);
        var service = new EligibilityVerificationService(db, _options, _router, new FixedTenantProvider(Tenant), _clock,
            NullLogger<EligibilityVerificationService>.Instance);
        _router.Result = Result(status);
        var patient = await db.Patients.Include(p => p.Insurances).ThenInclude(i => i.InsurancePlan).SingleAsync(p => p.PatientId == PatientId);
        await service.VerifyAsync(patient, patient.PrimaryInsurance, await db.Providers.SingleAsync(p => p.ProviderId == ProviderId), serviceDate);
    }

    private static ClaimsPrincipal User(string role) =>
        new(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, role)], "test"));

    private async Task<CoverageVerification> Row(Guid appointmentId)
    {
        await using var db = Db(Tenant);
        return await db.CoverageVerifications.AsNoTracking().SingleAsync(x => x.AppointmentId == appointmentId);
    }

    private async Task EditCoverage()
    {
        _clock.Advance(TimeSpan.FromMinutes(5));
        await using var db = Db(Tenant);
        var coverage = await db.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId);
        coverage.GroupNumber = "G-2";
        coverage.LastVerifiedAt = null;
        coverage.LastVerificationState = null;
        coverage.ModifiedDate = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
    }

    private CloudDentalDbContext Db(string tenant) => new(_options, new FixedTenantProvider(tenant));

    // Stamped at the clock's time, so the coverage's last answer follows the test clock.
    private static EligibilityResult Result(CoverageStatus status) => new()
    {
        CorrelationId = "corr-1", CoverageStatus = status, Source = "Clearinghouse"
    };

    private static Patient Patient(int id, string tenant, DateTime created) => new()
    {
        PatientId = id, TenantId = tenant, FirstName = id == PatientId ? "Quinn" : "Rowan", LastName = id == PatientId ? "Harlow" : "Vale",
        DateOfBirth = new DateTime(1990, 3, 14), Gender = "U", Status = "Active", CreatedDate = created
    };

    private static PatientInsurance Coverage(int id, int patientId) => new()
    {
        PatientInsuranceId = id, TenantId = Tenant, PatientId = patientId, InsurancePlanId = PlanId,
        MemberId = patientId == PatientId ? "MBR123" : $"MBR{patientId}", SequenceNumber = 1, RelationshipToSubscriber = "Self",
        EffectiveDate = new DateTime(2026, 1, 1), IsActive = true
    };

    private sealed class FakeFeed : IScheduledAppointmentFeed
    {
        private readonly Dictionary<Guid, ScheduledAppointment> _appointments = [];
        public bool Fail { get; set; }
        public bool Truncated { get; set; }
        public List<string> Tenants { get; } = [];

        public Guid Add(DateTimeOffset start, int patientId = PatientId, int providerId = ProviderId)
        {
            var id = Guid.NewGuid();
            _appointments[id] = new(id, patientId, providerId, start.UtcDateTime, SchedStatus.Scheduled);
            return id;
        }

        public void SetStatus(Guid id, SchedStatus status) => _appointments[id] = _appointments[id] with { Status = status };
        public void Move(Guid id, DateTimeOffset start) => _appointments[id] = _appointments[id] with { StartUtc = start.UtcDateTime };
        public void Remove(Guid id) => _appointments.Remove(id);

        public Task<ScheduledAppointmentWindow> GetAsync(string tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        {
            Tenants.Add(tenantId);
            if (Fail) throw new HttpRequestException("Scheduling is unavailable.");
            var inWindow = _appointments.Values.Where(a => a.StartUtc >= fromUtc && a.StartUtc <= toUtc).ToList();
            return Task.FromResult(new ScheduledAppointmentWindow(inWindow, Truncated));
        }
    }

    private sealed class FakeRouter : IPayerTransactionRouter
    {
        public EligibilityResult? Result { get; set; }
        public Exception? Throw { get; set; }
        public Action? During { get; set; }
        public int Calls => Requests.Count;
        public List<NormalizedEligibilityRequest> Requests { get; } = [];

        public Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            During?.Invoke();
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result ?? throw new InvalidOperationException("No result configured."));
        }

        public Task<RoutedTreatmentEstimate> GetEstimateAsync(PayerEstimateRoutingRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user = null) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => user;
    }
}

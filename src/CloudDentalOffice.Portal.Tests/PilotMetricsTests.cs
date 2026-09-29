using System.Security.Claims;
using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PilotMetricsTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const int PatientId = 101;
    private const int CoverageId = 501;
    private const int ProviderId = 7;

    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CloudDentalDbContext> _options;

    public PilotMetricsTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        using var db = Db(Tenant);
        db.Database.EnsureCreated();
        db.Patients.Add(new Patient
        {
            PatientId = PatientId, TenantId = Tenant, FirstName = "Ana", LastName = "Reyes",
            DateOfBirth = new DateTime(1980, 1, 1), Gender = "F", Status = "Active"
        });
        db.InsurancePlans.Add(new InsurancePlan { InsurancePlanId = 11, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true });
        db.PatientInsurances.Add(new PatientInsurance
        {
            PatientInsuranceId = CoverageId, TenantId = Tenant, PatientId = PatientId, InsurancePlanId = 11,
            MemberId = "M1", SequenceNumber = 1, EffectiveDate = new DateTime(2026, 1, 1), IsActive = true
        });
        db.Providers.Add(new Provider { ProviderId = ProviderId, TenantId = Tenant, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist", IsActive = true });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Coverage_counts_only_started_appointments_in_the_period_and_this_practice()
    {
        var visit = Now.UtcDateTime.AddDays(-3);
        await using (var db = Db(Tenant))
        {
            db.CoverageVerifications.AddRange(
                Row(visit, EligibilityVerificationState.Verified, checkedAt: visit.AddDays(-2)),
                Row(visit, EligibilityVerificationState.Verified, checkedAt: visit.AddHours(1)),   // verified only after the visit
                Row(visit, EligibilityVerificationState.NotDental),
                Row(visit, EligibilityVerificationState.Inactive),
                Row(visit, EligibilityVerificationState.NeedsInfo),
                Row(visit, state: null, coverage: null),                                          // no coverage on file yet
                Row(visit, state: null),                                                          // coverage, never checked
                Row(visit, EligibilityVerificationState.Unconfirmed),
                Row(visit, EligibilityVerificationState.Unavailable),
                Row(visit, EligibilityVerificationState.SelfPay),
                Row(Now.UtcDateTime.AddDays(-45), EligibilityVerificationState.Verified, checkedAt: Now.UtcDateTime.AddDays(-46)), // before the period
                Row(Now.UtcDateTime.AddDays(2), EligibilityVerificationState.Verified, checkedAt: Now.UtcDateTime)); // not started yet
            await db.SaveChangesAsync();
        }
        await using (var other = Db(OtherTenant))
        {
            var row = Row(visit, EligibilityVerificationState.NotDental);
            row.TenantId = OtherTenant;
            other.CoverageVerifications.Add(row);
            await other.SaveChangesAsync();
        }

        var coverage = (await Service().GetAsync(30)).Coverage;

        Assert.Equal(10, coverage.Appointments);
        Assert.Equal(1, coverage.VerifiedBeforeVisit);
        Assert.Equal(1, coverage.SelfPay);
        Assert.Equal(1, coverage.NotDental);
        Assert.Equal(1, coverage.Inactive);
        Assert.Equal(2, coverage.NeedsInfo);
        Assert.Equal(1, coverage.Unconfirmed);
        Assert.Equal(1, coverage.Unavailable);
        Assert.Equal(1, coverage.NotChecked);
        Assert.Equal(4, coverage.ProblemsCaught);
        Assert.Equal(1m / 9m, coverage.VerifiedRate);
    }

    [Fact]
    public async Task Checks_count_the_period_and_estimate_cost_for_answered_checks_only()
    {
        await using (var db = Db(Tenant))
        {
            db.EligibilityVerifications.AddRange(
                Check(Now.AddDays(-1), source: "Clearinghouse"),
                Check(Now.AddDays(-2), source: "Clearinghouse"),
                Check(Now.AddDays(-3), source: null),              // stopped before sending
                Check(Now.AddDays(-4), source: "Clearinghouse", payerErrorCodes: ["72"]), // payer rejection, still billed
                Check(Now.AddDays(-5), source: "Clearinghouse", payerErrorCodes: ["42"]), // payer unavailable, not billed
                Check(Now.AddDays(-6), source: "Clearinghouse", payerErrorCodes: ["80"]), // no payer response, not billed
                Check(Now.AddDays(-40), source: "Clearinghouse")); // before the period
            await db.SaveChangesAsync();
        }

        var checks = (await Service(price: 0.25m).GetAsync(30)).Checks;

        Assert.Equal(6, checks.Checks);
        Assert.Equal(5, checks.Answered);
        Assert.Equal(3, checks.Billed);
        Assert.Equal(0.75m, checks.EstimatedCost);
    }

    [Fact]
    public async Task Claims_report_outcomes_and_days_to_payment()
    {
        var service = Now.UtcDateTime.AddDays(-20);
        await using (var db = Db(Tenant))
        {
            db.Claims.AddRange(
                Claim("C1", "Paid", submitted: service.AddDays(1), service: service, posted: service.AddDays(14)),
                Claim("C2", "PartiallyPaid", submitted: service.AddDays(1), service: service, posted: service.AddDays(10)),
                Claim("C3", "Denied", submitted: service.AddDays(1), service: service),
                Claim("C4", "Rejected", submitted: service.AddDays(1), service: service),
                Claim("C5", "Draft", submitted: null, service: service));                // never submitted
            await db.SaveChangesAsync();
        }

        var claims = (await Service().GetAsync(30)).Claims;

        Assert.Equal(4, claims.Submitted);
        Assert.Equal(1, claims.Rejected);
        Assert.Equal(1, claims.Paid);
        Assert.Equal(1, claims.PartiallyPaid);
        Assert.Equal(1, claims.Denied);
        Assert.Equal(0.75m, claims.AcceptedRate);
        Assert.Equal(12.0m, claims.AverageDaysToPayment);
    }

    [Fact]
    public async Task Intake_counts_links_sent_in_the_period_and_their_answers()
    {
        await using (var db = Db(Tenant))
        {
            var row = Row(Now.UtcDateTime.AddDays(5), EligibilityVerificationState.NeedsInfo, coverage: null);
            db.CoverageVerifications.Add(row);
            await db.SaveChangesAsync();
            db.CoverageIntakeRequests.AddRange(
                Intake(row.Id, sent: Now.UtcDateTime.AddDays(-2), answered: CoverageIntakeAnswer.Plan),
                Intake(row.Id, sent: Now.UtcDateTime.AddDays(-3), answered: CoverageIntakeAnswer.NoInsurance),
                Intake(row.Id, sent: Now.UtcDateTime.AddDays(-4), answered: null),
                Intake(row.Id, sent: null, answered: null));                                   // not sent yet
            await db.SaveChangesAsync();
        }

        var intake = (await Service().GetAsync(30)).Intake;

        Assert.Equal(3, intake.Sent);
        Assert.Equal(2, intake.Answered);
        Assert.Equal(1, intake.AnsweredWithPlan);
        Assert.Equal(1, intake.AnsweredNoInsurance);
    }

    [Fact]
    public async Task Only_practice_staff_can_read_the_metrics()
    {
        var patientUser = new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, "Patient")], "test"));
        await using var db = Db(Tenant, patientUser);
        var service = new PilotMetricsService(db, new FixedTenantProvider(Tenant, patientUser),
            Options.Create(new PilotMetricsOptions()), new FixedClock(Now));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(30));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static readonly ClaimsPrincipal Staff =
        new(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, "Staff")], "test"));

    private PilotMetricsService Service(decimal price = 0.30m) =>
        new(Db(Tenant), new FixedTenantProvider(Tenant, Staff),
            Options.Create(new PilotMetricsOptions { EligibilityCheckPrice = price }), new FixedClock(Now));

    private CloudDentalDbContext Db(string tenant, ClaimsPrincipal? user = null) =>
        new(_options, new FixedTenantProvider(tenant, user ?? Staff));

    private static CoverageVerification Row(DateTime start, EligibilityVerificationState? state,
        DateTime? checkedAt = null, int? coverage = CoverageId) => new()
    {
        TenantId = Tenant, AppointmentId = Guid.NewGuid(), PatientId = PatientId, ProviderId = ProviderId,
        AppointmentStart = start, PatientInsuranceId = coverage, State = state,
        LastCheckedAt = checkedAt ?? (state is null ? null : start.AddDays(-1)),
        CreatedAt = start.AddDays(-7), UpdatedAt = start.AddDays(-1)
    };

    private static EligibilityVerification Check(DateTimeOffset checkedAt, string? source, string[]? payerErrorCodes = null) => new()
    {
        TenantId = Tenant, PatientInsuranceId = CoverageId, PatientId = PatientId,
        ServiceDate = DateOnly.FromDateTime(checkedAt.UtcDateTime), CheckedAt = checkedAt, Source = source,
        State = source is null ? EligibilityVerificationState.NeedsInfo : EligibilityVerificationState.Verified,
        // Serialized the way EligibilityVerificationService records it (web defaults).
        ResultJson = source is null ? null : JsonSerializer.Serialize(new EligibilityResult
        {
            CorrelationId = "c", Source = source, VerifiedAt = checkedAt, PayerErrorCodes = payerErrorCodes ?? []
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
    };

    private static CloudDentalOffice.Portal.Models.Claim Claim(string number, string status, DateTime? submitted, DateTime service, DateTime? posted = null) => new()
    {
        TenantId = Tenant, ClaimNumber = number, PatientId = PatientId, ProviderId = ProviderId,
        PatientInsuranceId = CoverageId, ServiceDateFrom = service, Status = status, TotalChargeAmount = 100m,
        SubmittedDate = submitted, FinancialsPostedAt = posted
    };

    private static CoverageIntakeRequest Intake(long verificationId, DateTime? sent, CoverageIntakeAnswer? answered) => new()
    {
        Id = Guid.NewGuid(), TenantId = Tenant, PatientId = PatientId, CoverageVerificationId = verificationId,
        RecipientEmail = "patient@example.com", Status = CoverageIntakeStatus.Pending,
        CreatedAt = Now.UtcDateTime.AddDays(-10), ExpiresAt = Now.UtcDateTime.AddDays(10), SentAt = sent,
        AnsweredAt = answered is null ? null : sent?.AddHours(3), Answer = answered
    };

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => user;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

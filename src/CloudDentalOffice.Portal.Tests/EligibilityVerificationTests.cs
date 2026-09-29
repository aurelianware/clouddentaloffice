using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Stedi;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Eligibility checks are recorded against the coverage and classified for the
/// front desk; multi-service benefit lines keep every service type. Synthetic data only.
/// </summary>
public sealed class EligibilityVerificationTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const int PatientId = 101;
    private const int PlanId = 11;
    private const int CoverageId = 501;
    private const int OtherTenantCoverageId = 601;
    private static readonly DateOnly ServiceDate = DateOnly.FromDateTime(DateTime.Today.AddDays(7));

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly FakeRouter _router = new();
    private readonly EligibilityVerificationService _service;

    public EligibilityVerificationTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        var tenant = new FixedTenantProvider(Tenant);
        _db = new CloudDentalDbContext(options, tenant);
        _db.Database.EnsureCreated();

        _db.Patients.AddRange(Patient(PatientId, Tenant), Patient(202, OtherTenant));
        _db.InsurancePlans.AddRange(
            new InsurancePlan { InsurancePlanId = PlanId, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true },
            new InsurancePlan { InsurancePlanId = 21, TenantId = OtherTenant, PayerId = "52133", PayerName = "Other practice plan", IsActive = true });
        _db.PatientInsurances.AddRange(
            Coverage(CoverageId, PatientId, PlanId, Tenant),
            Coverage(OtherTenantCoverageId, 202, 21, OtherTenant));
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        _service = new EligibilityVerificationService(_db, options, _router, tenant, TimeProvider.System,
            NullLogger<EligibilityVerificationService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Recording ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Verified_check_is_recorded_and_stamped_on_the_coverage()
    {
        _router.Result = Result(CoverageStatus.Active) with { PlanName = "Dental PPO", AnnualMaximumRemaining = 1250m };
        var coverage = await LoadCoverage();

        var outcome = await _service.VerifyAsync(await LoadPatient(), coverage, Provider(), ServiceDate);

        Assert.Equal(EligibilityVerificationState.Verified, outcome.State);
        Assert.Same(_router.Result, outcome.Result);
        Assert.Null(outcome.Message);

        var row = Assert.Single(await _service.GetHistoryAsync(CoverageId));
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal(PatientId, row.PatientId);
        Assert.Equal(ServiceDate, row.ServiceDate);
        Assert.Equal(EligibilityVerificationState.Verified, row.State);
        Assert.Equal("corr-1", row.CorrelationId);
        Assert.Equal("Clearinghouse", row.Source);
        Assert.Contains("Dental PPO", row.ResultJson);
        Assert.Contains("\"coverageStatus\":\"Active\"", row.ResultJson);

        _db.ChangeTracker.Clear();
        var stored = await _db.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId);
        Assert.Equal(EligibilityVerificationState.Verified, stored.LastVerificationState);
        Assert.NotNull(stored.LastVerifiedAt);
        Assert.Equal(stored.LastVerifiedAt, coverage.LastVerifiedAt);
    }

    [Fact]
    public async Task Latest_verified_result_comes_back_with_its_benefit_summary()
    {
        var summary = new DentalBenefitSummary
        {
            Preventive = new CategoryCoverage(1m, true, false, []),
            Basic = new CategoryCoverage(0.8m, true, false, []),
            Major = CategoryCoverage.NotReported,
            Orthodontics = CategoryCoverage.NotReported
        };
        _router.Result = Result(CoverageStatus.Active) with { BenefitSummary = summary };
        await _service.VerifyAsync(await LoadPatient(), await LoadCoverage(), Provider(), ServiceDate);
        // A later check that didn't verify coverage doesn't replace the benefits staff can use.
        _router.Result = Result(CoverageStatus.Inactive);
        await _service.VerifyAsync(await LoadPatient(), await LoadCoverage(), Provider(), ServiceDate);

        var latest = await _service.GetLatestVerifiedResultAsync(CoverageId);

        Assert.Equal(0.8m, latest?.BenefitSummary?.Basic.PlanPaysPercent);
        Assert.Null(await _service.GetLatestVerifiedResultAsync(OtherTenantCoverageId));
    }

    [Fact]
    public async Task Recording_does_not_save_other_pending_edits_to_the_coverage()
    {
        _router.Result = Result(CoverageStatus.Active);
        var coverage = await LoadCoverage();
        coverage.MemberId = "UNSAVED-EDIT";

        await _service.VerifyAsync(await LoadPatient(), coverage, Provider(), ServiceDate);

        _db.ChangeTracker.Clear();
        var stored = await _db.PatientInsurances.SingleAsync(x => x.PatientInsuranceId == CoverageId);
        Assert.Equal("MBR123", stored.MemberId);
    }

    [Fact]
    public async Task Missing_information_is_recorded_as_needs_info_without_calling_the_payer()
    {
        var outcome = await _service.VerifyAsync(await LoadPatient(), await LoadCoverage(),
            new Provider { ProviderId = 1, NPI = "", FirstName = "Dana", LastName = "Dentist" }, ServiceDate);

        Assert.Equal(EligibilityVerificationState.NeedsInfo, outcome.State);
        Assert.Null(outcome.Result);
        Assert.Contains("NPI", outcome.Message);
        Assert.Equal(0, _router.Calls);
        var row = Assert.Single(await _service.GetHistoryAsync(CoverageId));
        Assert.Equal(EligibilityVerificationState.NeedsInfo, row.State);
        Assert.Null(row.ResultJson);
    }

    [Fact]
    public async Task Unavailable_clearinghouse_is_recorded_for_retry()
    {
        _router.Throw = new TreatmentEstimateUnavailableException("Eligibility checks are temporarily unavailable. Try again in a minute.");

        var outcome = await _service.VerifyAsync(await LoadPatient(), await LoadCoverage(), Provider(), ServiceDate);

        Assert.Equal(EligibilityVerificationState.Unavailable, outcome.State);
        Assert.Equal(_router.Throw.Message, outcome.Message);
        Assert.Equal(EligibilityVerificationState.Unavailable, Assert.Single(await _service.GetHistoryAsync(CoverageId)).State);
    }

    [Fact]
    public async Task No_coverage_selected_is_not_recorded()
    {
        var outcome = await _service.VerifyAsync(await LoadPatient(), null, Provider(), ServiceDate);

        Assert.Equal(EligibilityVerificationState.NeedsInfo, outcome.State);
        Assert.Empty(await _db.EligibilityVerifications.ToListAsync());
    }

    [Fact]
    public async Task A_failure_to_record_still_returns_the_payer_answer()
    {
        _router.Result = Result(CoverageStatus.Active);
        // Not in the database, so the history row violates its foreign key.
        var unsaved = Coverage(9999, PatientId, PlanId, Tenant);
        unsaved.InsurancePlan = await _db.InsurancePlans.SingleAsync(p => p.InsurancePlanId == PlanId);

        var outcome = await _service.VerifyAsync(await LoadPatient(), unsaved, Provider(), ServiceDate);

        Assert.Equal(EligibilityVerificationState.Verified, outcome.State);
        Assert.Same(_router.Result, outcome.Result);
        Assert.Empty(await _db.EligibilityVerifications.ToListAsync());
    }

    [Fact]
    public async Task History_is_limited_to_the_callers_practice()
    {
        _db.EligibilityVerifications.Add(new EligibilityVerification
        {
            TenantId = OtherTenant, PatientInsuranceId = OtherTenantCoverageId, PatientId = 202,
            ServiceDate = ServiceDate, State = EligibilityVerificationState.Verified, CheckedAt = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync();

        Assert.Empty(await _service.GetHistoryAsync(OtherTenantCoverageId));
    }

    [Fact]
    public void Verification_without_a_tenant_is_refused()
    {
        _db.EligibilityVerifications.Add(new EligibilityVerification
        {
            PatientInsuranceId = CoverageId, PatientId = PatientId, ServiceDate = ServiceDate, CheckedAt = DateTimeOffset.UtcNow
        });

        Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());
    }

    [Fact]
    public async Task Editing_what_was_verified_clears_the_last_verification()
    {
        _router.Result = Result(CoverageStatus.Active);
        await _service.VerifyAsync(await LoadPatient(), await LoadCoverage(), Provider(), ServiceDate);
        _db.ChangeTracker.Clear();
        var coverages = new PatientCoverageService(_db, new FixedTenantProvider(Tenant), TimeProvider.System,
            NullLogger<PatientCoverageService>.Instance);
        var input = new CoverageInput { InsurancePlanId = PlanId, MemberId = "MBR123", SequenceNumber = 1, EffectiveDate = new DateTime(2026, 1, 1) };

        var unchanged = await coverages.UpdateCoverageAsync(CoverageId, input with { EffectiveDate = new DateTime(2026, 2, 1) });
        Assert.Equal(EligibilityVerificationState.Verified, unchanged.LastVerificationState);

        var changed = await coverages.UpdateCoverageAsync(CoverageId, input with { MemberId = "MBR999" });
        Assert.Null(changed.LastVerificationState);
        Assert.Null(changed.LastVerifiedAt);
        // The history itself is kept.
        Assert.Single(await _service.GetHistoryAsync(CoverageId));
    }

    // ── Classification ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(CoverageStatus.Active, false, EligibilityVerificationState.Verified)]
    [InlineData(CoverageStatus.Active, true, EligibilityVerificationState.NotDental)]
    [InlineData(CoverageStatus.Inactive, false, EligibilityVerificationState.Inactive)]
    [InlineData(CoverageStatus.Unknown, false, EligibilityVerificationState.Unconfirmed)]
    public void Payer_answers_map_to_front_desk_states(CoverageStatus status, bool dentalNotCovered, EligibilityVerificationState expected)
    {
        var outcome = EligibilityVerificationService.Classify(
            Result(status) with { DentalCareNotCovered = dentalNotCovered }, ServiceDate);

        Assert.Equal(expected, outcome.State);
        Assert.Equal(expected == EligibilityVerificationState.Verified, outcome.Message is null);
    }

    // ── Multi-service benefit lines ────────────────────────────────────────

    [Fact]
    public async Task A_benefit_line_for_several_services_applies_to_each()
    {
        var result = await DirectCheck("""
            {
              "planStatus": [ { "statusCode": "1" } ],
              "benefitsInformation": [
                { "code": "1", "serviceTypeCodes": ["35"] },
                { "code": "A", "serviceTypeCodes": ["24", "25", "26"],
                  "serviceTypes": ["Periodontics", "Restorative", "Endodontics"],
                  "benefitPercent": "0.2", "inPlanNetworkIndicatorCode": "Y" }
              ]
            }
            """);

        var coinsurance = result.Benefits.Where(b => b.Coinsurance is not null).ToList();
        Assert.Equal(["24", "25", "26"], coinsurance.Select(b => b.ServiceTypeCode));
        Assert.All(coinsurance, b => Assert.Equal(0.2m, b.Coinsurance));
        Assert.Equal("Coinsurance · Endodontics", coinsurance[2].Description);
    }

    [Fact]
    public async Task Service_names_fall_back_to_the_code_table_when_not_parallel()
    {
        var result = await DirectCheck("""
            {
              "planStatus": [ { "statusCode": "1" } ],
              "benefitsInformation": [
                { "code": "A", "serviceTypeCodes": ["41", "23", "41", " "], "serviceTypes": ["Routine (Preventive) Dental"], "benefitPercent": "0" }
              ]
            }
            """);

        var lines = result.Benefits.Where(b => b.ServiceTypeCode is "41" or "23").ToList();
        Assert.Equal(["41", "23"], lines.Select(b => b.ServiceTypeCode));
        Assert.Equal("Coinsurance · Diagnostic Dental", lines[1].Description);
    }

    [Fact]
    public async Task Dental_listed_after_medical_on_a_non_covered_line_is_still_flagged()
    {
        // Before fan-out only the first code (30) was kept, hiding the dental exclusion.
        var result = await DirectCheck("""
            {
              "planStatus": [ { "statusCode": "1" } ],
              "benefitsInformation": [
                { "code": "1", "serviceTypeCodes": ["30"] },
                { "code": "I", "serviceTypeCodes": ["30", "35"] }
              ]
            }
            """);

        Assert.True(result.DentalCareNotCovered);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Task<Patient> LoadPatient() => _db.Patients.SingleAsync(p => p.PatientId == PatientId);

    private Task<PatientInsurance> LoadCoverage() =>
        _db.PatientInsurances.Include(x => x.InsurancePlan).SingleAsync(x => x.PatientInsuranceId == CoverageId);

    private static EligibilityResult Result(CoverageStatus status) => new()
    {
        CorrelationId = "corr-1", CoverageStatus = status, Source = "Clearinghouse", VerifiedAt = DateTimeOffset.UtcNow
    };

    private static Provider Provider() => new() { ProviderId = 1, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist" };

    private static Patient Patient(int id, string tenant) => new()
    {
        PatientId = id, TenantId = tenant, FirstName = "Quinn", LastName = "Harlow",
        DateOfBirth = new DateTime(1990, 3, 14), Gender = "U", Status = "Active"
    };

    private static PatientInsurance Coverage(int id, int patientId, int planId, string tenant) => new()
    {
        PatientInsuranceId = id, TenantId = tenant, PatientId = patientId, InsurancePlanId = planId,
        MemberId = "MBR123", SequenceNumber = 1, RelationshipToSubscriber = "Self",
        EffectiveDate = new DateTime(2026, 1, 1), IsActive = true
    };

    private static async Task<EligibilityResult> DirectCheck(string stediJson)
    {
        var client = new StediEligibilityClient(
            new HttpClient(new StediCredentialHandler(new FixedCredentials()) { InnerHandler = new Respond(stediJson) }),
            Options.Create(new StediOptions()), NullLogger<StediEligibilityClient>.Instance);
        return await client.CheckAsync(new NormalizedEligibilityRequest
        {
            TenantId = "practice-a", PayerId = "60054", MemberId = "MBR123",
            SubscriberFirstName = "Quinn", SubscriberLastName = "Harlow", SubscriberDateOfBirth = new DateOnly(1990, 3, 14),
            ProviderNpi = "1999999984", ProviderFirstName = "Dana", ProviderLastName = "Dentist",
            ServiceDate = DateOnly.FromDateTime(DateTime.Today)
        });
    }

    private sealed class FakeRouter : IPayerTransactionRouter
    {
        public EligibilityResult? Result { get; set; }
        public Exception? Throw { get; set; }
        public int Calls { get; private set; }

        public Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result ?? throw new InvalidOperationException("No result configured."));
        }

        public Task<RoutedTreatmentEstimate> GetEstimateAsync(PayerEstimateRoutingRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedCredentials : IStediCredentialProvider
    {
        public Task<StediCredential> GetAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StediCredential(tenantId, ClearinghouseConnectionMode.Integrated, "test-key"));

        public void Invalidate(string tenantId) { }
    }

    private sealed class Respond(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
}

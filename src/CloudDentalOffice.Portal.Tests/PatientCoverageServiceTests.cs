using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>Patient coverage entered by staff. Synthetic data only.</summary>
public sealed class PatientCoverageServiceTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const int PatientId = 101;
    private const int OtherTenantPatientId = 202;
    private const int PlanId = 11;
    private const int InactivePlanId = 12;
    private const int OtherTenantPlanId = 21;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly PatientCoverageService _service;

    public PatientCoverageServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        var tenant = new FixedTenantProvider(Tenant);
        _db = new CloudDentalDbContext(options, tenant);
        _db.Database.EnsureCreated();

        _db.Patients.AddRange(
            Patient(PatientId, Tenant),
            Patient(OtherTenantPatientId, OtherTenant));
        _db.InsurancePlans.AddRange(
            new InsurancePlan { InsurancePlanId = PlanId, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true },
            new InsurancePlan { InsurancePlanId = InactivePlanId, TenantId = Tenant, PayerId = "99999", PayerName = "Retired Plan", IsActive = false },
            new InsurancePlan { InsurancePlanId = OtherTenantPlanId, TenantId = OtherTenant, PayerId = "52133", PayerName = "Other practice plan", IsActive = true });
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        _service = new PatientCoverageService(_db, tenant, TimeProvider.System, NullLogger<PatientCoverageService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Adds_self_coverage_that_eligibility_can_use()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = "  MBR123  ", GroupNumber = " GRP9 " });

        var coverage = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        Assert.Equal(saved.PatientInsuranceId, coverage.PatientInsuranceId);
        Assert.Equal(Tenant, coverage.TenantId);
        Assert.Equal("MBR123", coverage.MemberId);
        Assert.Equal("GRP9", coverage.GroupNumber);
        Assert.Equal(1, coverage.SequenceNumber);
        Assert.Equal("Self", coverage.RelationshipToSubscriber);
        Assert.True(coverage.IsActive);
        Assert.Equal("86027", coverage.InsurancePlan.PayerId);

        var patient = await _db.Patients.Include(p => p.Insurances).ThenInclude(i => i.InsurancePlan).SingleAsync(p => p.PatientId == PatientId);
        Assert.Equal(PlanId, patient.PrimaryInsurance!.InsurancePlanId);
    }

    [Fact]
    public async Task Self_coverage_clears_any_policyholder_fields()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with
        {
            SubscriberFirstName = "Stale", SubscriberLastName = "Name", SubscriberDateOfBirth = new DateTime(1960, 1, 1)
        });

        Assert.Null(saved.SubscriberFirstName);
        Assert.Null(saved.SubscriberLastName);
        Assert.Null(saved.SubscriberDateOfBirth);
    }

    [Fact]
    public async Task Dependent_coverage_requires_the_policyholder()
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { RelationshipToSubscriber = "Child", SubscriberFirstName = "Rowan" }));

        Assert.Contains("policyholder's first name, last name and date of birth", error.Message);
        Assert.Empty(await _service.GetCoveragesAsync(PatientId));
    }

    [Fact]
    public async Task Dependent_coverage_is_saved_with_the_policyholder()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with
        {
            RelationshipToSubscriber = "child",
            SubscriberFirstName = "Rowan",
            SubscriberLastName = "Harlow",
            SubscriberDateOfBirth = new DateTime(1975, 7, 2)
        });

        Assert.Equal("Child", saved.RelationshipToSubscriber);
        Assert.Equal("Rowan", saved.SubscriberFirstName);
        Assert.Equal(new DateTime(1975, 7, 2), saved.SubscriberDateOfBirth!.Value.Date);
    }

    [Fact]
    public async Task Saved_dependent_coverage_builds_an_eligibility_request()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with
        {
            RelationshipToSubscriber = "Spouse",
            SubscriberFirstName = "Rowan",
            SubscriberLastName = "Harlow",
            SubscriberDateOfBirth = new DateTime(1975, 7, 2)
        });
        var coverage = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        var patient = await _db.Patients.SingleAsync(p => p.PatientId == PatientId);
        var provider = new Provider { ProviderId = 5, TenantId = Tenant, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist" };

        var request = EligibilityRequestBuilder.Build(patient, coverage, provider, DateOnly.FromDateTime(DateTime.Today), Tenant);

        Assert.Equal("86027", request.PayerId);
        Assert.Equal("MBR123", request.MemberId);
        Assert.Equal("Rowan", request.SubscriberFirstName);
        Assert.NotNull(request.Dependent);
        Assert.Equal(EligibilityRelationship.Spouse, request.Dependent!.Relationship);
        Assert.Equal(saved.PatientInsuranceId, coverage.PatientInsuranceId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Member_id_is_required(string memberId)
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = memberId }));

        Assert.Equal("Member ID is required.", error.Message);
    }

    [Fact]
    public async Task Member_id_longer_than_the_column_is_rejected_without_echoing_it()
    {
        var tooLong = new string('7', 51);

        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = tooLong }));

        Assert.Contains("50 characters", error.Message);
        Assert.DoesNotContain(tooLong, error.Message);
    }

    [Theory]
    [InlineData(InactivePlanId)]
    [InlineData(OtherTenantPlanId)]
    [InlineData(999)]
    public async Task Plan_must_be_active_and_belong_to_the_practice(int planId)
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { InsurancePlanId = planId }));

        Assert.Contains("Claims → Payers", error.Message);
    }

    [Fact]
    public async Task Another_practices_patient_cannot_be_given_coverage()
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(OtherTenantPatientId, SelfInput()));

        Assert.Equal("This patient was not found.", error.Message);
        Assert.False(await _db.PatientInsurances.IgnoreQueryFilters().AnyAsync());
    }

    [Fact]
    public async Task Only_one_active_primary_but_a_secondary_is_allowed()
    {
        await _service.AddCoverageAsync(PatientId, SelfInput());

        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = "MBR999" }));
        Assert.Contains("already has active primary coverage", error.Message);

        await _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = "MBR999", SequenceNumber = 2 });
        Assert.Equal(2, (await _service.GetCoveragesAsync(PatientId)).Count(c => c.IsActive));
    }

    [Fact]
    public async Task Deactivated_primary_frees_the_slot()
    {
        var first = await _service.AddCoverageAsync(PatientId, SelfInput());

        await _service.DeactivateCoverageAsync(first.PatientInsuranceId);
        await _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = "MBR999" });

        var coverages = await _service.GetCoveragesAsync(PatientId);
        var old = coverages.Single(c => c.PatientInsuranceId == first.PatientInsuranceId);
        Assert.False(old.IsActive);
        Assert.NotNull(old.TerminationDate);
        Assert.Single(coverages, c => c.IsActive && c.SequenceNumber == 1);
    }

    [Fact]
    public async Task Update_changes_the_coverage_and_keeps_its_own_slot()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput());

        await _service.UpdateCoverageAsync(saved.PatientInsuranceId, SelfInput() with { MemberId = "MBR456", GroupNumber = "G2" });

        var coverage = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        Assert.Equal("MBR456", coverage.MemberId);
        Assert.Equal("G2", coverage.GroupNumber);
        Assert.NotNull(coverage.ModifiedDate);
    }

    [Fact]
    public async Task Termination_before_effective_date_is_rejected()
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with
            {
                EffectiveDate = new DateTime(2026, 5, 1),
                TerminationDate = new DateTime(2026, 4, 30)
            }));

        Assert.Contains("termination date", error.Message);
    }

    [Fact]
    public async Task Past_termination_date_saves_as_inactive()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with
        {
            EffectiveDate = new DateTime(2024, 1, 1),
            TerminationDate = new DateTime(2025, 1, 1)
        });

        Assert.False(saved.IsActive);
    }

    [Fact]
    public async Task Past_coverage_can_be_recorded_while_a_primary_is_active()
    {
        await _service.AddCoverageAsync(PatientId, SelfInput());

        var history = await _service.AddCoverageAsync(PatientId, SelfInput() with
        {
            MemberId = "OLD123",
            EffectiveDate = new DateTime(2023, 1, 1),
            TerminationDate = new DateTime(2024, 12, 31)
        });

        Assert.False(history.IsActive);
        Assert.Single(await _service.GetCoveragesAsync(PatientId), c => c.IsActive);
    }

    [Fact]
    public async Task Editing_coverage_deactivated_today_keeps_it_inactive()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput());
        await _service.DeactivateCoverageAsync(saved.PatientInsuranceId);
        var deactivated = Assert.Single(await _service.GetCoveragesAsync(PatientId));

        await _service.UpdateCoverageAsync(saved.PatientInsuranceId, SelfInput() with
        {
            GroupNumber = "G9",
            TerminationDate = deactivated.TerminationDate
        });

        var edited = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        Assert.False(edited.IsActive);
        Assert.Equal("G9", edited.GroupNumber);
    }

    [Fact]
    public async Task Deactivating_future_dated_coverage_ends_it_today()
    {
        var saved = await _service.AddCoverageAsync(PatientId, SelfInput() with { TerminationDate = DateTime.Today.AddYears(1) });

        await _service.DeactivateCoverageAsync(saved.PatientInsuranceId);

        var coverage = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        Assert.False(coverage.IsActive);
        Assert.Equal(DateTime.Today, coverage.TerminationDate!.Value.Date);
    }

    [Fact]
    public async Task Clearing_the_termination_date_reactivates_only_into_a_free_slot()
    {
        var first = await _service.AddCoverageAsync(PatientId, SelfInput());
        await _service.DeactivateCoverageAsync(first.PatientInsuranceId);
        await _service.AddCoverageAsync(PatientId, SelfInput() with { MemberId = "MBR999" });

        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.UpdateCoverageAsync(first.PatientInsuranceId, SelfInput()));
        Assert.Contains("already has active primary coverage", error.Message);

        await _service.UpdateCoverageAsync(first.PatientInsuranceId, SelfInput() with { SequenceNumber = 2 });
        Assert.True((await _service.GetCoveragesAsync(PatientId)).Single(c => c.PatientInsuranceId == first.PatientInsuranceId).IsActive);
    }

    [Fact]
    public async Task Database_rejects_a_second_active_primary()
    {
        await _service.AddCoverageAsync(PatientId, SelfInput());
        _db.PatientInsurances.Add(new PatientInsurance
        {
            TenantId = Tenant, PatientId = PatientId, InsurancePlanId = PlanId, MemberId = "RACE",
            SequenceNumber = 1, IsActive = true, EffectiveDate = new DateTime(2026, 1, 1), CreatedDate = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_saves_for_the_same_slot_leave_one_and_explain_the_other()
    {
        // Another staff member saves an active primary after this request's
        // check passes but before it commits.
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new CompetingSaveInterceptor(_connection))
            .Options;
        var tenant = new FixedTenantProvider(Tenant);
        await using var db = new CloudDentalDbContext(options, tenant);
        var service = new PatientCoverageService(db, tenant, TimeProvider.System, NullLogger<PatientCoverageService>.Instance);

        var error = await Assert.ThrowsAsync<CoverageValidationException>(() => service.AddCoverageAsync(PatientId, SelfInput()));

        Assert.Contains("already has active primary coverage", error.Message);
        var coverage = Assert.Single(await _service.GetCoveragesAsync(PatientId));
        Assert.Equal("OTHER-STAFF", coverage.MemberId);
        Assert.Empty(db.ChangeTracker.Entries<PatientInsurance>());
    }

    private sealed class CompetingSaveInterceptor(SqliteConnection connection) : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
                await using var other = new CloudDentalDbContext(options, new FixedTenantProvider(Tenant));
                other.PatientInsurances.Add(new PatientInsurance
                {
                    TenantId = Tenant, PatientId = PatientId, InsurancePlanId = PlanId, MemberId = "OTHER-STAFF",
                    SequenceNumber = 1, IsActive = true, EffectiveDate = new DateTime(2026, 1, 1), CreatedDate = DateTime.UtcNow
                });
                await other.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task Unknown_relationship_is_rejected()
    {
        var error = await Assert.ThrowsAsync<CoverageValidationException>(() =>
            _service.AddCoverageAsync(PatientId, SelfInput() with { RelationshipToSubscriber = "Cousin" }));

        Assert.Contains("relationship", error.Message);
    }

    private static CoverageInput SelfInput() => new()
    {
        InsurancePlanId = PlanId,
        MemberId = "MBR123",
        SequenceNumber = 1,
        RelationshipToSubscriber = "Self",
        EffectiveDate = new DateTime(2026, 1, 1)
    };

    private static Patient Patient(int id, string tenant) => new()
    {
        PatientId = id, TenantId = tenant, FirstName = "Quinn", LastName = "Harlow",
        DateOfBirth = new DateTime(1990, 3, 14), Gender = "U", Status = "Active"
    };

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
}

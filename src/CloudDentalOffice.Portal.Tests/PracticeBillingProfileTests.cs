using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// The practice's claim billing details are entered once, validated, kept per practice,
/// and carried on every 837D in place of placeholders. Synthetic data only.
/// </summary>
public sealed class PracticeBillingProfileTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private const string OtherTenant = "tenant-b";
    private const string PracticeNpi = "1245319599";
    private const string DentistNpi = "1234567893";
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<CloudDentalDbContext> _options;

    public PracticeBillingProfileTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        using var db = Db(Tenant);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void A_complete_profile_has_no_problems_once_normalized()
    {
        var profile = Valid();
        profile.Npi = " 1245319599 ";
        profile.TaxId = "86-1234567";
        profile.ZipCode = "85281-1234";
        profile.Phone = "+1 (480) 555-0142";
        profile.State = "az";

        PracticeBillingProfileRules.Normalize(profile);

        Assert.Empty(PracticeBillingProfileRules.Problems(profile));
        Assert.Equal("861234567", profile.TaxId);
        Assert.Equal("852811234", profile.ZipCode);
        Assert.Equal("4805550142", profile.Phone);
        Assert.Equal("AZ", profile.State);
    }

    [Theory]
    [InlineData("1234567890")] // fails the check digit
    [InlineData("123456789")]
    [InlineData("")]
    public void An_npi_that_fails_the_check_digit_is_refused(string npi)
    {
        var profile = Valid();
        profile.Npi = npi;
        Assert.Contains(PracticeBillingProfileRules.Problems(profile), p => p.Contains("NPI"));
    }

    [Fact]
    public void A_po_box_five_digit_zip_and_missing_fields_are_each_named()
    {
        var profile = Valid();
        profile.Address1 = "P.O. Box 100";
        profile.ZipCode = "85281";
        profile.LegalName = "";
        profile.TaxId = "000000000";

        var problems = PracticeBillingProfileRules.Problems(profile);

        Assert.Contains(problems, p => p.Contains("PO box"));
        Assert.Contains(problems, p => p.Contains("ZIP+4"));
        Assert.Contains(problems, p => p.Contains("legal name"));
        Assert.Contains(problems, p => p.Contains("EIN"));
        Assert.Single(PracticeBillingProfileRules.Problems(null));
    }

    [Fact]
    public async Task Staff_save_one_profile_per_practice_and_other_practices_cannot_see_it()
    {
        Assert.Empty(await Service(Tenant).SaveAsync(Valid()));
        var second = Valid();
        second.ContactName = "Billing Office";
        Assert.Empty(await Service(Tenant).SaveAsync(second));

        await using (var db = Db(Tenant))
        {
            var saved = Assert.Single(await db.PracticeBillingProfiles.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(Tenant, saved.TenantId);
            Assert.Equal("Billing Office", saved.ContactName);
            Assert.Equal("front@sunrise.test", saved.UpdatedBy);
            Assert.Equal(Now.UtcDateTime, saved.UpdatedAt);
        }
        Assert.Null(await Service(OtherTenant).GetAsync());
    }

    [Fact]
    public async Task An_invalid_profile_is_not_saved()
    {
        var profile = Valid();
        profile.Npi = "1234567890";

        var problems = await Service(Tenant).SaveAsync(profile);

        Assert.NotEmpty(problems);
        Assert.Null(await Service(Tenant).GetAsync());
    }

    [Fact]
    public async Task Someone_without_staff_access_cannot_read_or_save_it()
    {
        var patient = new ClaimsPrincipal(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, "Patient")], "test"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(Tenant, patient).GetAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(Tenant, patient).SaveAsync(Valid()));
    }

    [Fact]
    public async Task A_claim_is_not_built_until_the_profile_is_complete()
    {
        var claim = await SeedClaim();
        await using var db = Db(Tenant);
        var x12 = new EdiX12Service(db, NullLogger<EdiX12Service>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => x12.Generate837DTransactionAsync(claim));

        Assert.Contains("billing details", error.Message);
    }

    [Fact]
    public async Task The_837D_bills_as_the_practice_and_renders_as_the_dentist()
    {
        var claim = await SeedClaim();
        Assert.Empty(await Service(Tenant).SaveAsync(Valid()));
        await using var db = Db(Tenant);

        var x12 = await new EdiX12Service(db, NullLogger<EdiX12Service>.Instance).Generate837DTransactionAsync(claim);
        var segments = x12.Split("~\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("NM1*41*2*SUNRISE DENTAL PLLC*****46*861234567", segments);
        Assert.Contains("PER*IC*JORDAN LEE*TE*4805550142", segments);
        Assert.Contains("PRV*BI*PXC*1223G0001X", segments);
        Assert.Contains($"NM1*85*2*SUNRISE DENTAL PLLC*****XX*{PracticeNpi}", segments);
        Assert.Contains("N3*1200 E UNIVERSITY DR*STE 4", segments);
        Assert.Contains("N4*TEMPE*AZ*852811234", segments);
        Assert.Contains("REF*EI*861234567", segments);
        Assert.Contains($"NM1*82*1*VALE*AVERY*****XX*{DentistNpi}", segments);
        Assert.DoesNotContain(segments, s => s.Contains("DENTAL PLAZA") || s.Contains("5555551234") || s.Contains("123456789*"));

        // SE counts ST through SE, not the ISA and GS envelope.
        var st = Array.FindIndex(segments, s => s.StartsWith("ST*"));
        var se = Array.FindIndex(segments, s => s.StartsWith("SE*"));
        Assert.Equal((se - st + 1).ToString(), segments[se].Split('*')[1]);
    }

    [Fact]
    public async Task A_claim_whose_dentist_has_no_valid_npi_is_not_built()
    {
        var claim = await SeedClaim(dentistNpi: "");
        Assert.Empty(await Service(Tenant).SaveAsync(Valid()));
        await using var db = Db(Tenant);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new EdiX12Service(db, NullLogger<EdiX12Service>.Instance).Generate837DTransactionAsync(claim));

        Assert.Contains("Providers", error.Message);
    }

    private static PracticeBillingProfile Valid() => new()
    {
        LegalName = "Sunrise Dental PLLC",
        Npi = PracticeNpi,
        TaxId = "861234567",
        TaxonomyCode = "1223G0001X",
        Address1 = "1200 E University Dr",
        Address2 = "Ste 4",
        City = "Tempe",
        State = "AZ",
        ZipCode = "852811234",
        ContactName = "Jordan Lee",
        Phone = "4805550142"
    };

    private async Task<Models.Claim> SeedClaim(string dentistNpi = DentistNpi)
    {
        await using var db = Db(Tenant);
        db.Patients.Add(new Patient
        {
            PatientId = 41, TenantId = Tenant, FirstName = "Rowan", LastName = "Hale", DateOfBirth = new DateTime(1988, 5, 2),
            Gender = "Female", Status = "Active", Address1 = "10 Mill Ave", City = "Tempe", State = "AZ", ZipCode = "85281",
            CreatedDate = Now.UtcDateTime
        });
        db.Providers.Add(new Provider { ProviderId = 7, TenantId = Tenant, FirstName = "Avery", LastName = "Vale", NPI = dentistNpi });
        db.InsurancePlans.Add(new InsurancePlan { InsurancePlanId = 11, TenantId = Tenant, PayerId = "86027", PayerName = "Delta Dental Arizona", IsActive = true });
        db.PatientInsurances.Add(new PatientInsurance
        {
            PatientInsuranceId = 21, TenantId = Tenant, PatientId = 41, InsurancePlanId = 11, MemberId = "M100", SequenceNumber = 1,
            EffectiveDate = new DateTime(2026, 1, 1)
        });
        var claim = new Models.Claim
        {
            ClaimId = 31, TenantId = Tenant, ClaimNumber = "CLM-31", PatientId = 41, ProviderId = 7, PatientInsuranceId = 21,
            ServiceDateFrom = new DateTime(2026, 10, 20), TotalChargeAmount = 120m
        };
        claim.Procedures.Add(new ClaimProcedure
        {
            TenantId = Tenant, CDTCode = "D1110", Description = "Adult prophylaxis", ServiceDate = new DateTime(2026, 10, 20), ChargeAmount = 120m
        });
        db.Claims.Add(claim);
        await db.SaveChangesAsync();
        return claim;
    }

    private PracticeBillingProfileService Service(string tenantId, ClaimsPrincipal? user = null)
    {
        user ??= new ClaimsPrincipal(new ClaimsIdentity(
            [new System.Security.Claims.Claim(ClaimTypes.Role, "Staff"), new System.Security.Claims.Claim(ClaimTypes.Name, "front@sunrise.test")], "test"));
        var tenant = new FixedTenantProvider(tenantId, user);
        return new PracticeBillingProfileService(new CloudDentalDbContext(_options, tenant), tenant, new Clock(Now));
    }

    private CloudDentalDbContext Db(string tenantId) => new(_options, new FixedTenantProvider(tenantId));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedTenantProvider(string tenantId, ClaimsPrincipal? user = null) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => user;
    }
}

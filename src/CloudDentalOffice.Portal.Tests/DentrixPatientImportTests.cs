using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public class DentrixPatientImportTests
{
    private static readonly DateTime Today = new(2026, 10, 27);

    // Synthetic patients only. Real exports contain PHI and must never be committed.
    private const string Csv = """
        Patient List
        Printed 10/27/2026

        Chart Number,Last Name,First Name,MI,Birth Date,Gender,Home Phone,Email,Address 1,City,State,Zip,Status,SSN,Prim Ins Carrier Name,Prim Ins Payor ID,Prim Subscriber ID,Prim Group Number,Prim Relation,Prim Subscriber Name,Prim Subscriber Birth Date,Sec Ins Carrier Name,Sec Ins Payor ID,Sec Subscriber ID,Sec Relation
        TEST01,Testpatient,Alpha,Q,01/02/1980,Female,(555) 010-0001,alpha@example.com,1 Test St,Testville,ca,99999,Active,000-00-0000,Delta Dental of Test,DDTST,M100,G1,Self,"Testpatient, Alpha",01/02/1980,,,,
        TEST02,Testpatient,Beta,,03/04/2012,M,,,,,,,Active,,Delta Dental of Test,DDTST,M100,G1,Child,"Testpatient, Alpha Q",01/02/1980,Test Mutual,TMUT1,S200,Child
        TEST03,Noinsurance,Gamma,,5/6/1990,,,not-an-email,,,,,Non-Patient,,,,,,,,,,,,
        ,,Nolastname,,01/01/1990,F,,,,,,,,,,,,,,,,,,,
        TEST01,Duplicate,Chart,,01/01/1990,F,,,,,,,,,,,,,,,,,,,
        TEST04,Baddate,Delta,,13/45/1990,F,,,,,,,,,,,,,,,,,,,
        """;

    [Fact]
    public void ParsesReportHeaderAliasesAndSubscribersWithoutReadingSsn()
    {
        var preview = DentrixPatientCsv.Parse(Csv, Today);
        Assert.Equal(["TEST01", "TEST02", "TEST03"], preview.Patients.Select(p => p.ChartNumber));
        Assert.Equal(3, preview.Issues.Count(i => i.Message.StartsWith("Skipped")));
        Assert.Contains("SSN", preview.IgnoredColumns);

        var alpha = preview.Patients[0];
        Assert.Equal(("F", "CA", new DateTime(1980, 1, 2)), (alpha.Gender, alpha.State, alpha.DateOfBirth));
        var self = Assert.Single(alpha.Coverages);
        Assert.Equal(("DDTST", "M100", "Self"), (self.PayerId, self.MemberId, self.Relationship));
        Assert.Null(self.SubscriberFirstName);

        var beta = preview.Patients[1];
        Assert.Equal(2, beta.Coverages.Count);
        Assert.Equal(("Child", "Alpha", "Testpatient", new DateTime(1980, 1, 2)),
            (beta.Coverages[0].Relationship, beta.Coverages[0].SubscriberFirstName, beta.Coverages[0].SubscriberLastName, beta.Coverages[0].SubscriberDateOfBirth));

        var gamma = preview.Patients[2];
        Assert.Equal(("Inactive", "U", null), (gamma.Status, gamma.Gender, gamma.Email));
        Assert.Empty(gamma.Coverages);
    }

    [Theory]
    [InlineData("Name,DOB\nA,01/01/1990")]
    [InlineData("Chart Number,First Name,Birth Date\nC1,A,01/01/1990")]
    public void RejectsExportsWithoutRequiredColumns(string csv) =>
        Assert.Throws<FormatException>(() => DentrixPatientCsv.Parse(csv, Today));

    [Fact]
    public void TreatsADifferentSubscriberAsNotSelfWhenTheRelationIsMissing()
    {
        const string csv = "Chart Number,First Name,Last Name,Birth Date,Prim Ins Payor ID,Prim Subscriber ID,Prim Subscriber Name\n" +
                           "T1,Kid,Test,01/01/2015,DDTST,M1,\"Parent, Pat\"";
        Assert.Equal("Other", Assert.Single(DentrixPatientCsv.Parse(csv, Today).Patients[0].Coverages).Relationship);
    }

    [Fact]
    public async Task ImportIsRepeatableAdoptsExistingPatientsAndIsolatesPractices()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        var tenant = new Tenant("practice-a");
        await using (var db = new CloudDentalDbContext(options, tenant))
        {
            await db.Database.EnsureCreatedAsync();
            // Booked online before the import; should be linked, not duplicated.
            db.Patients.Add(new Patient { TenantId = "practice-a", FirstName = "Alpha", LastName = "TESTPATIENT", DateOfBirth = new DateTime(1980, 1, 2), Gender = "U", Email = "kept@example.com" });
            await db.SaveChangesAsync();
        }
        var service = Service(options, tenant);
        var preview = DentrixPatientCsv.Parse(Csv, Today);

        var first = await service.ImportAsync(preview);
        Assert.Equal((2, 1, 2, 1), (first.Created, first.Updated, first.CoveragesAdded, first.PlansCreated));
        // Beta's secondary lists a different subscriber without their name and birth date.
        Assert.Equal("TEST02", Assert.Single(first.Issues).ChartNumber);

        var again = await service.ImportAsync(preview);
        Assert.Equal((0, 3, 0, 0, 0, 0), (again.Created, again.Updated, again.CoveragesAdded, again.CoveragesUpdated, again.CoveragesReplaced, again.PlansCreated));

        var changed = DentrixPatientCsv.Parse(Csv.Replace("DDTST,M100,G1,Self", "DDTST,M999,G1,Self"), Today);
        var third = await service.ImportAsync(changed);
        Assert.Equal(1, third.CoveragesReplaced);

        await using (var db = new CloudDentalDbContext(options, tenant))
        {
            Assert.Equal(3, await db.Patients.CountAsync());
            var alpha = await db.Patients.Include(p => p.Insurances).SingleAsync(p => p.FirstName == "Alpha");
            Assert.Equal("alpha@example.com", alpha.Email);
            Assert.Equal("M999", Assert.Single(alpha.Insurances, i => i.IsActive).MemberId);
            Assert.Single(alpha.Insurances, i => !i.IsActive && i.MemberId == "M100");
            Assert.Equal(1, await db.InsurancePlans.CountAsync(p => p.PayerId == "DDTST"));
        }

        var other = new Tenant("practice-b");
        await using (var db = new CloudDentalDbContext(options, other))
            Assert.Empty(await db.Patients.ToListAsync());
        Assert.Equal(3, (await Service(options, other).ImportAsync(preview)).Created);
    }

    // Column layout of a Dentrix Ascend patient report; synthetic values.
    private const string AscendCsv = """
        Preferred Location,First Name,Last Name,Address 1,Address 2,City,State,Postal Code,Phone Numbers,Email Address,Date of Birth,Gender,Patient Status,Chart Number,First Visit Date,Last Visit,Last Visit Provider,Next Visit,Billing Type,Orthodontia Patient,Race,Ethnicity
        Test Dental PLLC,Nochart,Testone,1 Test St,,Testville,AZ,99999,"5550100001, 5550100002",one@example.com,1985-06-07,F,NEW,,,,,,Standard Billing,false,,
        Test Dental PLLC,Haschart,Testtwo,2 Test St,,Testville,AZ,99999-0001,5550100003,,1970-01-31,M,ACTIVE,AB1234C,2020-01-01,2026-10-01T15:00:00.000Z,Test Provider,,Standard Billing,false,,
        """;

    [Fact]
    public void ReadsAscendPatientReports()
    {
        var preview = DentrixPatientCsv.Parse(AscendCsv, Today);
        Assert.Empty(preview.Issues);
        var (nochart, haschart) = (preview.Patients[0], preview.Patients[1]);
        Assert.Null(nochart.ChartNumber);
        Assert.StartsWith(DentrixPatientCsv.NameKeyPrefix, nochart.NameKey);
        Assert.DoesNotContain("testone", nochart.NameKey);
        Assert.Equal(("Active", "5550100001", "5550100002", new DateTime(1985, 6, 7)),
            (nochart.Status, nochart.PrimaryPhone, nochart.SecondaryPhone, nochart.DateOfBirth));
        Assert.Equal(("AB1234C", "99999-0001"), (haschart.ChartNumber, haschart.ZipCode));
        Assert.Contains("Billing Type", preview.IgnoredColumns);
    }

    [Fact]
    public async Task PatientsWithoutChartNumbersKeepTheirRecordWhenOneIsAssigned()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        var tenant = new Tenant("practice-a");
        await using (var db = new CloudDentalDbContext(options, tenant)) await db.Database.EnsureCreatedAsync();
        var service = Service(options, tenant);
        Assert.Equal(2, (await service.ImportAsync(DentrixPatientCsv.Parse(AscendCsv, Today))).Created);
        Assert.Equal(2, (await service.ImportAsync(DentrixPatientCsv.Parse(AscendCsv, Today))).Updated);
        var assigned = DentrixPatientCsv.Parse(AscendCsv.Replace(",F,NEW,,", ",F,ACTIVE,ZZ9,"), Today);
        Assert.Equal((0, 2), (await service.ImportAsync(assigned)) is var r ? (r.Created, r.Updated) : default);
        await using (var db = new CloudDentalDbContext(options, tenant))
        {
            Assert.Equal(2, await db.Patients.CountAsync());
            Assert.Contains(await db.ImportedRecordLinks.Select(x => x.ExternalId).ToListAsync(), x => x == "ZZ9");
        }
    }

    [Fact]
    public async Task ReportsCoverageThatCannotBeVerified()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        var tenant = new Tenant("practice-a");
        await using (var db = new CloudDentalDbContext(options, tenant)) await db.Database.EnsureCreatedAsync();
        const string csv = "Chart Number,First Name,Last Name,Birth Date,Prim Ins Carrier Name,Prim Ins Payor ID,Prim Subscriber ID,Prim Relation\n" +
                           "T1,No,Payer,01/01/1990,Paper Carrier,,M1,Self\n" +
                           "T2,No,Subscriber,01/01/2010,Delta,DDTST,M2,Child";
        var result = await Service(options, tenant).ImportAsync(DentrixPatientCsv.Parse(csv, Today));
        Assert.Equal(2, result.Created);
        Assert.Equal(0, result.CoveragesAdded);
        Assert.Equal(["T1", "T2"], result.Issues.Select(i => i.ChartNumber));
    }

    [Fact]
    public async Task PatientCannotImportPatients()
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = Service(options, new Tenant("practice-a", "Patient"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ImportAsync(DentrixPatientCsv.Parse(Csv, Today)));
    }

    private static DentrixPatientImportService Service(DbContextOptions<CloudDentalDbContext> options, ITenantProvider tenant) =>
        new(options, tenant, TimeProvider.System, NullLogger<DentrixPatientImportService>.Instance);

    private sealed class Tenant(string id, string role = "Admin") : ITenantProvider
    {
        public string TenantId => id;
        public ClaimsPrincipal User => new(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, role)], "test"));
    }
}

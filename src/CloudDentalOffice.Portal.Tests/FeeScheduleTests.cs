using System.Security.Claims;
using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

public class FeeScheduleTests
{
    private const string Csv = """
        Fee Schedule,Practice standard
        Version,(past) - (future)
        Exported,2026-10-26 20:12

        Code,Description,Fee,No Write-off,Paid In Full,Active
        D7220,"Remove ST, Impacted Tooth",266,true,false,true
        ​09999,Deductible,50,true,false,true
        """;

    [Fact]
    public void ParsesMetadataQuotedDescriptionsAndCustomCodes()
    {
        var parsed = DentrixFeeScheduleCsv.Parse(Csv);
        Assert.Equal("Practice standard", parsed.Name);
        Assert.Equal(2, parsed.Entries.Count);
        Assert.Equal("Remove ST, Impacted Tooth", parsed.Entries[0].Description);
        Assert.Equal(266m, parsed.Entries[0].Fee);
        Assert.Equal("09999", parsed.Entries[1].Code);
        Assert.True(parsed.Entries[0].NoWriteOff);
        Assert.False(parsed.Entries[0].PaidInFull);
    }

    [Theory]
    [InlineData("266", "-1")]
    [InlineData("266", "1.123")]
    [InlineData("266", "NaN")]
    [InlineData("false,true", "maybe,true")]
    [InlineData("09999", "D7220")]
    public void RejectsInvalidFeesFlagsAndDuplicateCodes(string original, string replacement) =>
        Assert.Throws<FormatException>(() => DentrixFeeScheduleCsv.Parse(Csv.Replace(original, replacement)));

    [Fact]
    public async Task ImportIsRepeatableIsolatedAndOverlaysOnlyStandardFees()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(connection).Options;
        var tenant = new Tenant("practice-a");
        await using (var db = new CloudDentalDbContext(options, tenant)) await db.Database.EnsureCreatedAsync();
        var service = new FeeScheduleService(options, tenant);
        var standard = DentrixFeeScheduleCsv.Parse(Csv);
        var payer = DentrixFeeScheduleCsv.Parse(Csv.Replace("Practice standard", "Payer 2026").Replace("266", "139.79"));
        await service.ImportAsync([standard, payer], standard.Name);
        await service.ImportAsync([standard, payer], standard.Name);
        Assert.Equal(2, (await service.ListAsync()).Count);
        await using (var db = new CloudDentalDbContext(options, tenant))
        {
            var catalog = new ProcedureCodeServiceImpl(db, NullLogger<ProcedureCodeServiceImpl>.Instance);
            var code = Assert.Single(await catalog.SearchProcedureCodesAsync(" D7220 "));
            Assert.Equal(266m, code.DefaultFee);
            Assert.Null(await catalog.GetProcedureCodeByCodeAsync("09999"));
        }
        var other = new Tenant("practice-b");
        Assert.Empty(await new FeeScheduleService(options, other).ListAsync());
        await using (var db = new CloudDentalDbContext(options, other))
            Assert.Null(await new ProcedureCodeServiceImpl(db, NullLogger<ProcedureCodeServiceImpl>.Instance).GetProcedureCodeByCodeAsync("D7220"));
        await service.ImportAsync([payer], payer.Name);
        Assert.Single(await service.ListAsync(), x => x.IsPracticeDefault);
        await using (var db = new CloudDentalDbContext(options, tenant))
            Assert.Equal(139.79m, (await new ProcedureCodeServiceImpl(db, NullLogger<ProcedureCodeServiceImpl>.Instance).GetProcedureCodeByCodeAsync("D7220"))!.DefaultFee);
    }

    [Fact]
    public async Task PatientCannotImportFees()
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var service = new FeeScheduleService(options, new Tenant("practice-a", "Patient"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ImportAsync([DentrixFeeScheduleCsv.Parse(Csv)], null));
    }

    private sealed class Tenant(string id, string role = "Admin") : ITenantProvider
    {
        public string TenantId => id;
        public ClaimsPrincipal User => new(new ClaimsIdentity([new System.Security.Claims.Claim(ClaimTypes.Role, role)], "test"));
    }
}

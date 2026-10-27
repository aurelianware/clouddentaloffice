using System.Globalization;
using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Auth;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace CloudDentalOffice.Portal.Services;

public static class DentrixFeeScheduleCsv
{
    public static FeeSchedulePreview Parse(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        string? name = null;
        string[]? header = null;
        var entries = new List<FeeScheduleEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (!parser.EndOfData)
            {
                var row = parser.ReadFields() ?? [];
                if (row.Length == 0) continue;
                if (header is null)
                {
                    if (row[0] == "Fee Schedule" && row.Length == 2) name = row[1].Trim();
                    if (row.SequenceEqual(new[] { "Code", "Description", "Fee", "No Write-off", "Paid In Full", "Active" })) header = row;
                    continue;
                }
                if (row.Length != 6) throw new FormatException($"Row {parser.LineNumber}: expected six columns.");
                var code = row[0].Trim().TrimStart('\u200B').Trim();
                if (code.Length is < 1 or > 20 || code.Any(char.IsControl) || row[1].Length is < 1 or > 500)
                    throw new FormatException($"Row {parser.LineNumber}: invalid code or description.");
                if (!seen.Add(code)) throw new FormatException($"Duplicate procedure code: {code}.");
                if (!decimal.TryParse(row[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var fee)
                    || fee < 0 || fee > 99999999.99m || decimal.Round(fee, 2) != fee)
                    throw new FormatException($"Invalid fee for {code}.");
                if (!bool.TryParse(row[3], out var noWriteOff) || !bool.TryParse(row[4], out var paidInFull)
                    || !bool.TryParse(row[5], out var active)) throw new FormatException($"Invalid flags for {code}.");
                entries.Add(new(code, row[1], fee, noWriteOff, paidInFull, active));
                if (entries.Count > 10000) throw new FormatException("A schedule can contain at most 10,000 codes.");
            }
        }
        catch (MalformedLineException) { throw new FormatException("The CSV contains an invalid quoted row."); }
        if (header is null || string.IsNullOrWhiteSpace(name) || name.Length > 200 || entries.Count == 0)
            throw new FormatException("Expected a Dentrix fee schedule export with a name, six-column header and procedure rows.");
        return new(name, entries);
    }
}

public sealed class FeeScheduleService(DbContextOptions<CloudDentalDbContext> options, ITenantProvider tenant)
{
    private void RequireManager()
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId) || !StaffRoles.CanManagePayers(tenant.User))
            throw new UnauthorizedAccessException("Only practice payer managers can import fee schedules.");
    }

    public async Task<List<PracticeFeeSchedule>> ListAsync()
    {
        RequireManager();
        await using var db = new CloudDentalDbContext(options, tenant);
        return await db.PracticeFeeSchedules.AsNoTracking().OrderBy(x => x.Name).ToListAsync();
    }

    public async Task ImportAsync(IReadOnlyList<FeeSchedulePreview> previews, string? defaultName)
    {
        RequireManager();
        if (previews.Count is < 1 or > 20 || previews.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != previews.Count)
            throw new FormatException("Select 1–20 distinct fee schedules.");
        if (defaultName is not null && !previews.Any(x => x.Name == defaultName))
            throw new FormatException("The default must be one of the selected schedules.");
        await using var db = new CloudDentalDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var stored = await db.PracticeFeeSchedules.ToListAsync();
        if (defaultName is not null)
        {
            foreach (var item in stored) item.IsPracticeDefault = false;
            await db.SaveChangesAsync();
        }
        foreach (var preview in previews)
        {
            var item = stored.SingleOrDefault(x => x.Name.Equals(preview.Name, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new PracticeFeeSchedule { TenantId = tenant.TenantId, Name = preview.Name };
                db.PracticeFeeSchedules.Add(item);
            }
            item.EntriesJson = JsonSerializer.Serialize(preview.Entries);
            item.ImportedAt = DateTime.UtcNow;
            if (defaultName is not null) item.IsPracticeDefault = item.Name.Equals(defaultName, StringComparison.OrdinalIgnoreCase);
        }
        // The transaction includes clearing the old default and importing every schedule.
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}

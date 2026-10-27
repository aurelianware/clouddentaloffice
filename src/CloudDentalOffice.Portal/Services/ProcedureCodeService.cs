using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public class ProcedureCodeServiceImpl(CloudDentalDbContext context, ILogger<ProcedureCodeServiceImpl> logger) : IProcedureCodeService
{
    public async Task<List<ProcedureCode>> SearchProcedureCodesAsync(string searchTerm)
    {
        var codes = await GetAllProcedureCodesAsync();
        var term = searchTerm?.Trim() ?? "";
        return codes.Where(p => term.Length == 0 || p.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
            || p.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (p.AbbrDesc?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(p => p.Code.Equals(term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Code).Take(50).ToList();
    }

    public async Task<ProcedureCode?> GetProcedureCodeByCodeAsync(string code) =>
        (await GetAllProcedureCodesAsync()).FirstOrDefault(p => p.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    public async Task<List<ProcedureCode>> GetAllProcedureCodesAsync()
    {
        var codes = (await context.ProcedureCodes.AsNoTracking().Where(p => p.IsActive).ToListAsync())
            .ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);
        var schedule = await context.PracticeFeeSchedules.AsNoTracking().SingleOrDefaultAsync(x => x.IsPracticeDefault);
        if (schedule is not null)
        {
            foreach (var entry in JsonSerializer.Deserialize<List<FeeScheduleEntry>>(schedule.EntriesJson) ?? [])
            {
                // Keep custom billing codes in the schedule, outside CDT procedure selection.
                if (!System.Text.RegularExpressions.Regex.IsMatch(entry.Code, "^D[0-9]{4}$")) continue;
                if (!entry.Active) { codes.Remove(entry.Code); continue; }
                codes.TryGetValue(entry.Code, out var existing);
                codes[entry.Code] = new ProcedureCode
                {
                    ProcedureCodeId = existing?.ProcedureCodeId ?? 0,
                    Code = entry.Code, Description = entry.Description, DefaultFee = entry.Fee,
                    Category = existing?.Category, IsActive = true
                };
            }
        }
        return codes.Values.OrderBy(p => p.Code).ToList();
    }

    public async Task<ProcedureCode> CreateProcedureCodeAsync(ProcedureCode procedureCode)
    {
        procedureCode.CreatedDate = DateTime.UtcNow;
        context.ProcedureCodes.Add(procedureCode);
        await context.SaveChangesAsync();
        logger.LogInformation("Created procedure code {Code}", procedureCode.Code);
        return procedureCode;
    }
}

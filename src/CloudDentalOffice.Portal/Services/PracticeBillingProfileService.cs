using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Auth;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public interface IPracticeBillingProfileService
{
    /// <summary>The signed-in practice's profile, or null before it's been entered.</summary>
    Task<PracticeBillingProfile?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves the profile when it's valid; otherwise saves nothing and returns the problems.</summary>
    Task<IReadOnlyList<string>> SaveAsync(PracticeBillingProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Reads and saves the practice's claim billing details (one row per practice).</summary>
public sealed class PracticeBillingProfileService(
    CloudDentalDbContext db,
    ITenantProvider tenantProvider,
    TimeProvider time) : IPracticeBillingProfileService
{
    public async Task<PracticeBillingProfile?> GetAsync(CancellationToken cancellationToken = default)
    {
        RequireStaff();
        return await db.PracticeBillingProfiles.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SaveAsync(PracticeBillingProfile profile, CancellationToken cancellationToken = default)
    {
        RequireStaff();
        PracticeBillingProfileRules.Normalize(profile);
        var problems = PracticeBillingProfileRules.Problems(profile);
        if (problems.Count > 0)
            return problems;

        var tenantId = tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("No practice is signed in.");

        var row = await db.PracticeBillingProfiles.SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new PracticeBillingProfile { TenantId = tenantId };
            db.PracticeBillingProfiles.Add(row);
        }

        row.LegalName = profile.LegalName;
        row.Npi = profile.Npi;
        row.TaxId = profile.TaxId;
        row.TaxonomyCode = profile.TaxonomyCode;
        row.Address1 = profile.Address1;
        row.Address2 = profile.Address2;
        row.City = profile.City;
        row.State = profile.State;
        row.ZipCode = profile.ZipCode;
        row.ContactName = profile.ContactName;
        row.Phone = profile.Phone;
        row.UpdatedAt = time.GetUtcNow().UtcDateTime;
        var by = tenantProvider.User?.Identity?.Name;
        row.UpdatedBy = by is { Length: > 200 } ? by[..200] : by;

        await db.SaveChangesAsync(cancellationToken);
        return [];
    }

    private void RequireStaff()
    {
        if (!StaffRoles.CanManagePayers(tenantProvider.User))
            throw new UnauthorizedAccessException("Practice staff access is required.");
    }
}

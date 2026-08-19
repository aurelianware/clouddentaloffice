using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

public static class InitialTenantBootstrap
{
    public static async Task ApplyAsync(CloudDentalDbContext db, IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var section = configuration.GetSection("InitialTenant");
        if (!section.GetValue("Enabled", false)) return;

        var tenantId = section["TenantId"]?.Trim();
        var name = section["Name"]?.Trim();
        var domain = section["Domain"]?.Trim();
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64 || string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("InitialTenant requires a TenantId (max 64 characters) and Name.");

        if (!await db.Tenants.IgnoreQueryFilters().AnyAsync(t => t.TenantId == tenantId, cancellationToken))
        {
            db.Tenants.Add(new TenantRegistry
            {
                TenantId = tenantId,
                Name = name,
                Plan = section["Plan"] ?? "production",
                IsActive = true
            });
        }

        if (!await db.Organizations.IgnoreQueryFilters().AnyAsync(o => o.TenantId == tenantId, cancellationToken))
        {
            db.Organizations.Add(new Organization
            {
                TenantId = tenantId,
                Name = name,
                Domain = domain,
                Plan = section["Plan"] ?? "production",
                IsActive = true
            });
        }

        var review = section.GetSection("ReviewOutreach");
        if (!await db.ReviewOutreachSettings.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId, cancellationToken))
        {
            db.ReviewOutreachSettings.Add(new ReviewOutreachSettings
            {
                TenantId = tenantId,
                Enabled = review.GetValue("Enabled", false),
                DelayMinutes = Math.Max(0, review.GetValue("DelayMinutes", 240)),
                ReviewLandingPageUrl = review["ReviewLandingPageUrl"],
                GoogleReviewUrl = review["GoogleReviewUrl"],
                SenderName = review["SenderName"] ?? name
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

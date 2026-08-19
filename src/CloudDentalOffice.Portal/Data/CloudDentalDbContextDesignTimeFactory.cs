using System.Security.Claims;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (migrations) so they can build the
/// model without starting the Blazor host. Uses SQLite to match the migration
/// provider; no live database is opened when generating migrations.
/// </summary>
public sealed class CloudDentalDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CloudDentalDbContext>
{
    public CloudDentalDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseSqlite("Data Source=clouddental-design.db")
            .Options;
        return new CloudDentalDbContext(options, new DesignTimeTenantProvider());
    }

    private sealed class DesignTimeTenantProvider : ITenantProvider
    {
        public string TenantId => "design-time";
        public ClaimsPrincipal? User => null;
    }
}

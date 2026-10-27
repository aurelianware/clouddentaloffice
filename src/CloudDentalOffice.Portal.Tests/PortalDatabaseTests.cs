using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PortalDatabaseTests
{
    // A retrying execution strategy throws on every user-initiated transaction, which the billing services
    // and Stripe webhook processors depend on. Production runs PostgreSQL, so this must hold there.
    [Theory]
    [InlineData("PostgreSQL", "Host=localhost;Database=cdo;Username=cdo;Password=not-used")]
    [InlineData("SqlServer", "Server=localhost;Database=cdo;User Id=sa;Password=not-used;TrustServerCertificate=True")]
    [InlineData("Sqlite", "Data Source=:memory:")]
    public void Provider_allows_user_initiated_transactions(string provider, string connectionString)
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>();
        PortalDatabase.Configure(options, provider, connectionString, isDevelopment: false);
        using var db = new CloudDentalDbContext(options.Options, new DefaultTenantProvider());

        Assert.False(db.Database.CreateExecutionStrategy().RetriesOnFailure);
    }

    [Fact]
    public void Unknown_provider_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => PortalDatabase.Configure(
            new DbContextOptionsBuilder<CloudDentalDbContext>(), "Oracle", "x", isDevelopment: false));
    }
}

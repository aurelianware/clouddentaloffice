using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public sealed class InitialTenantBootstrapTests
{
    [Fact]
    public async Task CreatesOnlyConfiguredTenantAndIsIdempotent()
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseInMemoryDatabase($"bootstrap-{Guid.NewGuid()}").Options;
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(x => x.TenantId).Returns("third-set-smiles");
        await using var db = new CloudDentalDbContext(options, tenantProvider.Object);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InitialTenant:Enabled"] = "true",
            ["InitialTenant:TenantId"] = "third-set-smiles",
            ["InitialTenant:Name"] = "3rd Set Smiles",
            ["InitialTenant:Domain"] = "3rdsetsmiles.com"
        }).Build();

        await InitialTenantBootstrap.ApplyAsync(db, config);
        await InitialTenantBootstrap.ApplyAsync(db, config);

        var tenant = Assert.Single(await db.Tenants.IgnoreQueryFilters().ToListAsync());
        Assert.Equal("third-set-smiles", tenant.TenantId);
        Assert.Equal("3rd Set Smiles", tenant.Name);
        var organization = Assert.Single(await db.Organizations.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(tenant.TenantId, organization.TenantId);
        Assert.Empty(await db.Users.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await db.Patients.IgnoreQueryFilters().ToListAsync());
    }
}

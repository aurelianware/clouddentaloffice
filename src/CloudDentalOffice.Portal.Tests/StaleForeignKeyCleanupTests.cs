using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CloudDentalOffice.Portal.Tests;

public class StaleForeignKeyCleanupTests
{
    private static CloudDentalDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;

        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.Setup(p => p.TenantId).Returns("test-tenant");
        return new CloudDentalDbContext(options, tenantProvider.Object);
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("SqlServer")]
    [InlineData("")]
    public async Task ApplyAsync_SkipsNonPostgreSqlProviders_WithoutRunningRawSql(string provider)
    {
        using var context = NewContext();

        // The in-memory provider cannot execute raw SQL; reaching ExecuteSqlRaw
        // would throw. A clean completion proves the guard skipped those providers.
        var exception = await Record.ExceptionAsync(() =>
            StaleForeignKeyCleanup.ApplyAsync(context, provider, NullLogger.Instance));

        Assert.Null(exception);
    }
}

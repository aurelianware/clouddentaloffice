using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Design-time factory used by the EF Core tooling (<c>dotnet ef migrations …</c>,
/// <c>dotnet ef database update</c>). IntakeService runs on PostgreSQL in every
/// deployed environment (see infrastructure/azure/container-apps.bicep), so the
/// migration history is authored against the Npgsql provider.
///
/// The connection string here is a non-secret local placeholder. The tooling only
/// needs a provider to emit provider-specific DDL for <c>migrations add</c>/<c>script</c>;
/// commands that touch a database (<c>database update</c>) are given the real
/// connection string through the <c>ConnectionStrings__IntakeDb</c> environment
/// variable at run time. Never embed a production connection string in this file.
/// </summary>
public sealed class IntakeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IntakeDbContext>
{
    public IntakeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__IntakeDb")
            ?? "Host=localhost;Port=5432;Database=cdo_intake;Username=postgres";

        var options = new DbContextOptionsBuilder<IntakeDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsAssembly(typeof(IntakeDbContext).Assembly.FullName))
            .Options;

        return new IntakeDbContext(options);
    }
}

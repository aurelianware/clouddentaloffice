using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

public static class PortalDatabase
{
    /// <summary>
    /// Configures the Portal's database provider. No provider uses EF Core's retrying execution strategy:
    /// billing, refunds and the Stripe webhook processors run their work in explicit transactions, and a
    /// retrying strategy refuses every user-initiated transaction ("does not support user-initiated
    /// transactions"). A transient failure surfaces as an error instead; Service Bus redelivers webhook
    /// events and staff can repeat an action.
    /// </summary>
    public static void Configure(DbContextOptionsBuilder options, string provider, string connectionString,
        bool isDevelopment)
    {
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(connectionString);
            if (isDevelopment)
            {
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            }
        }
        else if (provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
            options.UseNpgsql(connectionString);
        else if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            options.UseSqlServer(connectionString);
        else
            throw new InvalidOperationException($"Unsupported database provider '{provider}'.");
    }
}

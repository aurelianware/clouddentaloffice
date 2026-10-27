using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>Matches AddPracticeFeeSchedules for PostgreSQL installations provisioned using EnsureCreated.</summary>
public static class FeeScheduleSchemaReconciliation
{
    public static async Task ApplyAsync(CloudDentalDbContext db, string provider, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "PracticeFeeSchedules" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" character varying(64) NOT NULL DEFAULT 'demo',
                "Name" character varying(200) NOT NULL,
                "IsPracticeDefault" boolean NOT NULL,
                "EntriesJson" text NOT NULL,
                "ImportedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PracticeFeeSchedules_TenantId"
                ON "PracticeFeeSchedules" ("TenantId") WHERE "IsPracticeDefault";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PracticeFeeSchedules_TenantId_Name"
                ON "PracticeFeeSchedules" ("TenantId", "Name");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.LogInformation("Fee schedule schema reconciliation completed");
    }
}

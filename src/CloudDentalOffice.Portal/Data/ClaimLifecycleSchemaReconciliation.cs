using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Adds claim-lifecycle columns to production databases originally provisioned
/// with EnsureCreated. EnsureCreated never alters an existing database.
/// </summary>
public static class ClaimLifecycleSchemaReconciliation
{
    public static async Task ApplyAsync(
        CloudDentalDbContext dbContext,
        string databaseProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(databaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
            return;

        const string sql = """
            ALTER TABLE "Claims" ADD COLUMN IF NOT EXISTS "CloudHealthOfficeClaimId" character varying(100);
            ALTER TABLE "Claims" ADD COLUMN IF NOT EXISTS "LifecycleStatus" character varying(50);
            ALTER TABLE "Claims" ADD COLUMN IF NOT EXISTS "LastIntelligenceAt" timestamp with time zone;
            ALTER TABLE "Claims" ADD COLUMN IF NOT EXISTS "FinancialsPostedAt" timestamp with time zone;
            CREATE INDEX IF NOT EXISTS "IX_Claims_TenantId_CloudHealthOfficeClaimId"
                ON "Claims" ("TenantId", "CloudHealthOfficeClaimId");
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.LogInformation("Claim lifecycle schema reconciliation completed");
    }
}

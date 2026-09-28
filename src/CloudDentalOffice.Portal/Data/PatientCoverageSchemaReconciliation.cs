using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Adds the active-coverage slot index to PostgreSQL databases provisioned with
/// EnsureCreated before it existed (EnsureCreated never alters an existing
/// database). Mirrors the AddActivePatientCoverageSlotIndex migration.
/// </summary>
public static class PatientCoverageSchemaReconciliation
{
    public static async Task ApplyAsync(
        CloudDentalDbContext dbContext,
        string databaseProvider,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(databaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        const string sql = """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PatientInsurances_TenantId_PatientId_SequenceNumber_Active"
                ON "PatientInsurances" ("TenantId", "PatientId", "SequenceNumber")
                WHERE "IsActive";
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.LogInformation("Patient coverage schema reconciliation completed");
    }
}

using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>Matches AddImportedRecordLinks for PostgreSQL installations provisioned using EnsureCreated.</summary>
public static class ImportedRecordLinkSchemaReconciliation
{
    public static async Task ApplyAsync(CloudDentalDbContext db, string provider, ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!provider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)) return;
        const string sql = """
            CREATE TABLE IF NOT EXISTS "ImportedRecordLinks" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "TenantId" character varying(64) NOT NULL DEFAULT 'demo',
                "Source" character varying(32) NOT NULL,
                "EntityType" character varying(32) NOT NULL,
                "ExternalId" character varying(64) NOT NULL,
                "LocalId" integer NOT NULL,
                "ImportedAt" timestamp with time zone NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_ImportedRecordLinks_TenantId"
                ON "ImportedRecordLinks" ("TenantId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImportedRecordLinks_TenantId_Source_EntityType_ExternalId"
                ON "ImportedRecordLinks" ("TenantId", "Source", "EntityType", "ExternalId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ImportedRecordLinks_TenantId_Source_EntityType_LocalId"
                ON "ImportedRecordLinks" ("TenantId", "Source", "EntityType", "LocalId");
            """;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.LogInformation("Imported record link schema reconciliation completed");
    }
}

using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Adds the coverage intake request table to PostgreSQL databases provisioned
/// with EnsureCreated before it existed. Mirrors the AddCoverageIntakeRequests
/// migration; column types and names match what Npgsql generates for the model.
/// </summary>
public static class CoverageIntakeSchemaReconciliation
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
            CREATE TABLE IF NOT EXISTS "CoverageIntakeRequests" (
                "Id" uuid NOT NULL,
                "TenantId" character varying(64) NOT NULL,
                "PatientId" integer NOT NULL,
                "CoverageVerificationId" bigint NOT NULL,
                "RecipientEmail" character varying(320) NOT NULL,
                "Status" character varying(16) NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "ExpiresAt" timestamp with time zone NOT NULL,
                "SentAt" timestamp with time zone,
                "ReminderSentAt" timestamp with time zone,
                "SendAttempts" integer NOT NULL,
                "LastAttemptAt" timestamp with time zone,
                "LastError" character varying(128),
                "AnsweredAt" timestamp with time zone,
                "Answer" character varying(16),
                "CarrierName" character varying(120),
                "MemberId" character varying(50),
                "GroupNumber" character varying(50),
                "RelationshipToSubscriber" character varying(20),
                "SubscriberFirstName" character varying(100),
                "SubscriberLastName" character varying(100),
                "SubscriberDateOfBirth" date,
                CONSTRAINT "PK_CoverageIntakeRequests" PRIMARY KEY ("Id")
            );

            CREATE INDEX IF NOT EXISTS "IX_CoverageIntakeRequests_TenantId_PatientId_CreatedAt"
                ON "CoverageIntakeRequests" ("TenantId", "PatientId", "CreatedAt");
            """;

        await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        logger.LogInformation("Coverage intake schema reconciliation completed");
    }
}

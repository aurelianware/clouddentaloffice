using Microsoft.EntityFrameworkCore;

public static class PatientAcquisitionSchema
{
    public static async Task EnsureAsync(SchedulingDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? "";
        var sql = provider.Contains("Npgsql", StringComparison.Ordinal) ? Postgres
            : provider.Contains("SqlServer", StringComparison.Ordinal) ? SqlServer : Sqlite;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "PatientAcquisitionEvents" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_PatientAcquisitionEvents" PRIMARY KEY, "EventId" TEXT NOT NULL,
          "TenantId" TEXT NOT NULL, "SessionId" TEXT NOT NULL, "EventType" INTEGER NOT NULL, "Source" TEXT NOT NULL,
          "Medium" TEXT NULL, "Campaign" TEXT NULL, "LandingPage" TEXT NULL, "AppointmentIntent" TEXT NOT NULL,
          "BookingRequestId" TEXT NULL, "AppointmentId" TEXT NULL, "LocationId" TEXT NULL, "ProviderId" INTEGER NULL,
          "OccurredAt" TEXT NOT NULL, "ReceivedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Event" ON "PatientAcquisitionEvents" ("TenantId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Type_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "EventType", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Source_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "Source", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Intent_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "AppointmentIntent", "OccurredAt");
        """;
    private const string Postgres = """
        CREATE TABLE IF NOT EXISTS "PatientAcquisitionEvents" (
          "Id" uuid NOT NULL CONSTRAINT "PK_PatientAcquisitionEvents" PRIMARY KEY, "EventId" uuid NOT NULL,
          "TenantId" varchar(64) NOT NULL, "SessionId" varchar(128) NOT NULL, "EventType" integer NOT NULL, "Source" varchar(40) NOT NULL,
          "Medium" varchar(40) NULL, "Campaign" varchar(120) NULL, "LandingPage" varchar(300) NULL, "AppointmentIntent" varchar(64) NOT NULL,
          "BookingRequestId" uuid NULL, "AppointmentId" uuid NULL, "LocationId" uuid NULL, "ProviderId" integer NULL,
          "OccurredAt" timestamp with time zone NOT NULL, "ReceivedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Event" ON "PatientAcquisitionEvents" ("TenantId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Type_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "EventType", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Source_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "Source", "OccurredAt");
        CREATE INDEX IF NOT EXISTS "IX_Acquisition_Tenant_Intent_Occurred" ON "PatientAcquisitionEvents" ("TenantId", "AppointmentIntent", "OccurredAt");
        """;
    private const string SqlServer = """
        IF OBJECT_ID(N'[PatientAcquisitionEvents]', N'U') IS NULL BEGIN
          CREATE TABLE [PatientAcquisitionEvents] ([Id] uniqueidentifier NOT NULL PRIMARY KEY, [EventId] uniqueidentifier NOT NULL,
          [TenantId] nvarchar(64) NOT NULL, [SessionId] nvarchar(128) NOT NULL, [EventType] int NOT NULL, [Source] nvarchar(40) NOT NULL,
          [Medium] nvarchar(40) NULL, [Campaign] nvarchar(120) NULL, [LandingPage] nvarchar(300) NULL, [AppointmentIntent] nvarchar(64) NOT NULL,
          [BookingRequestId] uniqueidentifier NULL, [AppointmentId] uniqueidentifier NULL, [LocationId] uniqueidentifier NULL, [ProviderId] int NULL,
          [OccurredAt] datetime2 NOT NULL, [ReceivedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_Acquisition_Tenant_Event] ON [PatientAcquisitionEvents] ([TenantId], [EventId]);
          CREATE INDEX [IX_Acquisition_Tenant_Occurred] ON [PatientAcquisitionEvents] ([TenantId], [OccurredAt]);
          CREATE INDEX [IX_Acquisition_Tenant_Type_Occurred] ON [PatientAcquisitionEvents] ([TenantId], [EventType], [OccurredAt]);
          CREATE INDEX [IX_Acquisition_Tenant_Source_Occurred] ON [PatientAcquisitionEvents] ([TenantId], [Source], [OccurredAt]);
          CREATE INDEX [IX_Acquisition_Tenant_Intent_Occurred] ON [PatientAcquisitionEvents] ([TenantId], [AppointmentIntent], [OccurredAt]); END;
        """;
}

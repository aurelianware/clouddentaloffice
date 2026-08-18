using Microsoft.EntityFrameworkCore;

/// <summary>
/// Additive schema bootstrap for existing SchedulingService databases. This
/// service uses EnsureCreated plus idempotent provider-specific DDL rather
/// than an EF migrations assembly.
/// </summary>
public static class SchedulingIntegrationSchema
{
    public static async Task EnsureAsync(SchedulingDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var sql = provider.Contains("Npgsql", StringComparison.Ordinal) ? PostgreSql
            : provider.Contains("SqlServer", StringComparison.Ordinal) ? SqlServer : Sqlite;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await EnsureMappingColumnsAsync(db, provider, cancellationToken);
    }

    private static async Task EnsureMappingColumnsAsync(
        SchedulingDbContext db, string provider, CancellationToken cancellationToken)
    {
        foreach (var column in new[] { "ExternalDisplayName", "IsActive" })
        {
            if (provider.Contains("Sqlite", StringComparison.Ordinal) &&
                await SqliteHasColumnAsync(db, "ExternalSchedulingResourceMappings", column, cancellationToken))
                continue;
            var definition = column == "ExternalDisplayName"
                ? provider.Contains("Npgsql", StringComparison.Ordinal) ? "varchar(300) NULL"
                    : provider.Contains("SqlServer", StringComparison.Ordinal) ? "nvarchar(300) NULL" : "TEXT NULL"
                : provider.Contains("Npgsql", StringComparison.Ordinal) ? "boolean NOT NULL DEFAULT TRUE"
                    : provider.Contains("SqlServer", StringComparison.Ordinal) ? "bit NOT NULL DEFAULT 1" : "INTEGER NOT NULL DEFAULT 1";
            var statement = provider.Contains("Npgsql", StringComparison.Ordinal)
                ? $"ALTER TABLE \"ExternalSchedulingResourceMappings\" ADD COLUMN IF NOT EXISTS \"{column}\" {definition};"
                : provider.Contains("SqlServer", StringComparison.Ordinal)
                    ? $"IF COL_LENGTH('ExternalSchedulingResourceMappings', '{column}') IS NULL ALTER TABLE [ExternalSchedulingResourceMappings] ADD [{column}] {definition};"
                    : $"ALTER TABLE \"ExternalSchedulingResourceMappings\" ADD COLUMN \"{column}\" {definition};";
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    private static async Task<bool> SqliteHasColumnAsync(
        SchedulingDbContext db, string table, string column, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = column;
            command.Parameters.Add(parameter);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "SchedulingIntegrationConfigurations" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_SchedulingIntegrationConfigurations" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "Channel" INTEGER NOT NULL, "Enabled" INTEGER NOT NULL,
          "Environment" TEXT NOT NULL, "CredentialReference" TEXT NULL,
          "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingIntegrationConfigurations_TenantId_Channel"
          ON "SchedulingIntegrationConfigurations" ("TenantId", "Channel");

        CREATE TABLE IF NOT EXISTS "ExternalSchedulingResourceMappings" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_ExternalSchedulingResourceMappings" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "Channel" INTEGER NOT NULL, "ResourceType" INTEGER NOT NULL,
          "InternalId" TEXT NOT NULL, "ExternalId" TEXT NOT NULL, "ExternalDisplayName" TEXT NULL,
          "IsActive" INTEGER NOT NULL DEFAULT 1, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalSchedulingResourceMappings_External"
          ON "ExternalSchedulingResourceMappings" ("TenantId", "Channel", "ResourceType", "ExternalId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalSchedulingResourceMappings_Internal"
          ON "ExternalSchedulingResourceMappings" ("TenantId", "Channel", "ResourceType", "InternalId");

        CREATE TABLE IF NOT EXISTS "SchedulingAppointmentTypes" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_SchedulingAppointmentTypes" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "AppointmentTypeId" TEXT NOT NULL, "DisplayName" TEXT NOT NULL,
          "DurationMinutes" INTEGER NOT NULL, "ProviderId" INTEGER NULL, "LocationId" TEXT NULL,
          "NewPatientAllowed" INTEGER NOT NULL, "ExistingPatientAllowed" INTEGER NOT NULL,
          "IsActive" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingAppointmentTypes_TenantId_AppointmentTypeId"
          ON "SchedulingAppointmentTypes" ("TenantId", "AppointmentTypeId");

        CREATE TABLE IF NOT EXISTS "ExternalAppointmentReferences" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_ExternalAppointmentReferences" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "AppointmentId" TEXT NOT NULL, "Channel" INTEGER NOT NULL,
          "ExternalAppointmentId" TEXT NOT NULL, "ExternalProviderId" TEXT NULL,
          "ExternalLocationId" TEXT NULL, "ExternalVisitReasonId" TEXT NULL,
          "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalAppointmentReferences_External"
          ON "ExternalAppointmentReferences" ("TenantId", "Channel", "ExternalAppointmentId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalAppointmentReferences_Appointment"
          ON "ExternalAppointmentReferences" ("TenantId", "AppointmentId", "Channel");

        CREATE TABLE IF NOT EXISTS "SchedulingIntegrationEvents" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_SchedulingIntegrationEvents" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "Channel" INTEGER NOT NULL, "ExternalEventId" TEXT NOT NULL,
          "Status" INTEGER NOT NULL, "AppointmentId" TEXT NULL, "FailureReason" TEXT NULL,
          "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingIntegrationEvents_TenantId_Channel_ExternalEventId"
          ON "SchedulingIntegrationEvents" ("TenantId", "Channel", "ExternalEventId");
        """;

    private const string PostgreSql = """
        CREATE TABLE IF NOT EXISTS "SchedulingIntegrationConfigurations" (
          "Id" uuid NOT NULL CONSTRAINT "PK_SchedulingIntegrationConfigurations" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "Channel" integer NOT NULL, "Enabled" boolean NOT NULL,
          "Environment" varchar(40) NOT NULL, "CredentialReference" varchar(512) NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingIntegrationConfigurations_TenantId_Channel"
          ON "SchedulingIntegrationConfigurations" ("TenantId", "Channel");

        CREATE TABLE IF NOT EXISTS "ExternalSchedulingResourceMappings" (
          "Id" uuid NOT NULL CONSTRAINT "PK_ExternalSchedulingResourceMappings" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "Channel" integer NOT NULL, "ResourceType" integer NOT NULL,
          "InternalId" varchar(128) NOT NULL, "ExternalId" varchar(256) NOT NULL,
          "ExternalDisplayName" varchar(300) NULL, "IsActive" boolean NOT NULL DEFAULT TRUE,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalSchedulingResourceMappings_External"
          ON "ExternalSchedulingResourceMappings" ("TenantId", "Channel", "ResourceType", "ExternalId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalSchedulingResourceMappings_Internal"
          ON "ExternalSchedulingResourceMappings" ("TenantId", "Channel", "ResourceType", "InternalId");

        CREATE TABLE IF NOT EXISTS "SchedulingAppointmentTypes" (
          "Id" uuid NOT NULL CONSTRAINT "PK_SchedulingAppointmentTypes" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "AppointmentTypeId" varchar(128) NOT NULL,
          "DisplayName" varchar(200) NOT NULL, "DurationMinutes" integer NOT NULL,
          "ProviderId" integer NULL, "LocationId" uuid NULL, "NewPatientAllowed" boolean NOT NULL,
          "ExistingPatientAllowed" boolean NOT NULL, "IsActive" boolean NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingAppointmentTypes_TenantId_AppointmentTypeId"
          ON "SchedulingAppointmentTypes" ("TenantId", "AppointmentTypeId");

        CREATE TABLE IF NOT EXISTS "ExternalAppointmentReferences" (
          "Id" uuid NOT NULL CONSTRAINT "PK_ExternalAppointmentReferences" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "AppointmentId" uuid NOT NULL, "Channel" integer NOT NULL,
          "ExternalAppointmentId" varchar(256) NOT NULL, "ExternalProviderId" varchar(256) NULL,
          "ExternalLocationId" varchar(256) NULL, "ExternalVisitReasonId" varchar(256) NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalAppointmentReferences_External"
          ON "ExternalAppointmentReferences" ("TenantId", "Channel", "ExternalAppointmentId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExternalAppointmentReferences_Appointment"
          ON "ExternalAppointmentReferences" ("TenantId", "AppointmentId", "Channel");

        CREATE TABLE IF NOT EXISTS "SchedulingIntegrationEvents" (
          "Id" uuid NOT NULL CONSTRAINT "PK_SchedulingIntegrationEvents" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "Channel" integer NOT NULL, "ExternalEventId" varchar(256) NOT NULL,
          "Status" integer NOT NULL, "AppointmentId" uuid NULL, "FailureReason" varchar(1000) NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SchedulingIntegrationEvents_TenantId_Channel_ExternalEventId"
          ON "SchedulingIntegrationEvents" ("TenantId", "Channel", "ExternalEventId");
        """;

    private const string SqlServer = """
        IF OBJECT_ID(N'[SchedulingIntegrationConfigurations]', N'U') IS NULL BEGIN
          CREATE TABLE [SchedulingIntegrationConfigurations] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SchedulingIntegrationConfigurations] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [Channel] int NOT NULL, [Enabled] bit NOT NULL,
            [Environment] nvarchar(40) NOT NULL, [CredentialReference] nvarchar(512) NULL,
            [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_SchedulingIntegrationConfigurations_TenantId_Channel]
            ON [SchedulingIntegrationConfigurations] ([TenantId], [Channel]); END;
        IF OBJECT_ID(N'[ExternalSchedulingResourceMappings]', N'U') IS NULL BEGIN
          CREATE TABLE [ExternalSchedulingResourceMappings] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ExternalSchedulingResourceMappings] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [Channel] int NOT NULL, [ResourceType] int NOT NULL,
            [InternalId] nvarchar(128) NOT NULL, [ExternalId] nvarchar(256) NOT NULL,
            [ExternalDisplayName] nvarchar(300) NULL, [IsActive] bit NOT NULL CONSTRAINT [DF_ExternalSchedulingResourceMappings_IsActive] DEFAULT 1,
            [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_ExternalSchedulingResourceMappings_External]
            ON [ExternalSchedulingResourceMappings] ([TenantId], [Channel], [ResourceType], [ExternalId]);
          CREATE UNIQUE INDEX [IX_ExternalSchedulingResourceMappings_Internal]
            ON [ExternalSchedulingResourceMappings] ([TenantId], [Channel], [ResourceType], [InternalId]); END;
        IF OBJECT_ID(N'[SchedulingAppointmentTypes]', N'U') IS NULL BEGIN
          CREATE TABLE [SchedulingAppointmentTypes] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SchedulingAppointmentTypes] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [AppointmentTypeId] nvarchar(128) NOT NULL,
            [DisplayName] nvarchar(200) NOT NULL, [DurationMinutes] int NOT NULL,
            [ProviderId] int NULL, [LocationId] uniqueidentifier NULL, [NewPatientAllowed] bit NOT NULL,
            [ExistingPatientAllowed] bit NOT NULL, [IsActive] bit NOT NULL,
            [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_SchedulingAppointmentTypes_TenantId_AppointmentTypeId]
            ON [SchedulingAppointmentTypes] ([TenantId], [AppointmentTypeId]); END;
        IF OBJECT_ID(N'[ExternalAppointmentReferences]', N'U') IS NULL BEGIN
          CREATE TABLE [ExternalAppointmentReferences] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ExternalAppointmentReferences] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [AppointmentId] uniqueidentifier NOT NULL, [Channel] int NOT NULL,
            [ExternalAppointmentId] nvarchar(256) NOT NULL, [ExternalProviderId] nvarchar(256) NULL,
            [ExternalLocationId] nvarchar(256) NULL, [ExternalVisitReasonId] nvarchar(256) NULL,
            [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_ExternalAppointmentReferences_External]
            ON [ExternalAppointmentReferences] ([TenantId], [Channel], [ExternalAppointmentId]);
          CREATE UNIQUE INDEX [IX_ExternalAppointmentReferences_Appointment]
            ON [ExternalAppointmentReferences] ([TenantId], [AppointmentId], [Channel]); END;
        IF OBJECT_ID(N'[SchedulingIntegrationEvents]', N'U') IS NULL BEGIN
          CREATE TABLE [SchedulingIntegrationEvents] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SchedulingIntegrationEvents] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [Channel] int NOT NULL, [ExternalEventId] nvarchar(256) NOT NULL,
            [Status] int NOT NULL, [AppointmentId] uniqueidentifier NULL, [FailureReason] nvarchar(1000) NULL,
            [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE UNIQUE INDEX [IX_SchedulingIntegrationEvents_TenantId_Channel_ExternalEventId]
            ON [SchedulingIntegrationEvents] ([TenantId], [Channel], [ExternalEventId]); END;
        """;
}

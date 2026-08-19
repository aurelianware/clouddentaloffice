using Microsoft.EntityFrameworkCore;

/// <summary>
/// Additive availability schema bootstrap. Existing Appointment rows are
/// assigned to the repository's legacy "default" tenant so collision queries
/// remain deterministic after upgrade.
/// </summary>
public static class SchedulingAvailabilitySchema
{
    public static async Task EnsureAsync(SchedulingDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var sql = provider.Contains("Npgsql", StringComparison.Ordinal) ? PostgreSql
            : provider.Contains("SqlServer", StringComparison.Ordinal) ? SqlServer : Sqlite;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);

        await EnsureColumnAsync(db, provider, "SchedulingIntegrationConfigurations", "TimeZoneId",
            "TEXT NOT NULL DEFAULT 'UTC'", "varchar(100) NOT NULL DEFAULT 'UTC'", "nvarchar(100) NOT NULL DEFAULT 'UTC'", cancellationToken);
        await EnsureColumnAsync(db, provider, "SchedulingIntegrationConfigurations", "MinimumBookingLeadMinutes",
            "INTEGER NOT NULL DEFAULT 0", "integer NOT NULL DEFAULT 0", "int NOT NULL DEFAULT 0", cancellationToken);
        await EnsureColumnAsync(db, provider, "SchedulingIntegrationConfigurations", "MaximumBookingHorizonDays",
            "INTEGER NOT NULL DEFAULT 90", "integer NOT NULL DEFAULT 90", "int NOT NULL DEFAULT 90", cancellationToken);
        await EnsureColumnAsync(db, provider, "Appointments", "TenantId",
            "TEXT NOT NULL DEFAULT 'default'", "varchar(64) NOT NULL DEFAULT 'default'", "nvarchar(64) NOT NULL DEFAULT 'default'", cancellationToken);
        await EnsureColumnAsync(db, provider, "Appointments", "AppointmentTypeId",
            "TEXT NULL", "varchar(128) NULL", "nvarchar(128) NULL", cancellationToken);
        await EnsureColumnAsync(db, provider, "Appointments", "ReasonForVisit",
            "TEXT NULL", "varchar(200) NULL", "nvarchar(200) NULL", cancellationToken);
        var tenantBackfill = provider.Contains("Npgsql", StringComparison.Ordinal)
            ? "UPDATE \"Appointments\" a SET \"TenantId\" = b.\"TenantId\" FROM \"BookingRequests\" b WHERE b.\"ApprovedAppointmentId\" = a.\"Id\" AND a.\"TenantId\" = 'default';"
            : provider.Contains("SqlServer", StringComparison.Ordinal)
                ? "UPDATE a SET [TenantId] = b.[TenantId] FROM [Appointments] a INNER JOIN [BookingRequests] b ON b.[ApprovedAppointmentId] = a.[Id] WHERE a.[TenantId] = 'default';"
                : "UPDATE \"Appointments\" SET \"TenantId\" = (SELECT b.\"TenantId\" FROM \"BookingRequests\" b WHERE b.\"ApprovedAppointmentId\" = \"Appointments\".\"Id\") WHERE \"TenantId\" = 'default' AND EXISTS (SELECT 1 FROM \"BookingRequests\" b WHERE b.\"ApprovedAppointmentId\" = \"Appointments\".\"Id\");";
        await db.Database.ExecuteSqlRawAsync(tenantBackfill, cancellationToken);
        var appointmentIndex = provider.Contains("Npgsql", StringComparison.Ordinal)
            ? "CREATE INDEX IF NOT EXISTS \"IX_Appointments_Tenant_Provider_Start_End\" ON \"Appointments\" (\"TenantId\", \"ProviderId\", \"StartTime\", \"EndTime\");"
            : provider.Contains("SqlServer", StringComparison.Ordinal)
                ? "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Appointments_Tenant_Provider_Start_End') CREATE INDEX [IX_Appointments_Tenant_Provider_Start_End] ON [Appointments] ([TenantId], [ProviderId], [StartTime], [EndTime]);"
                : "CREATE INDEX IF NOT EXISTS \"IX_Appointments_Tenant_Provider_Start_End\" ON \"Appointments\" (\"TenantId\", \"ProviderId\", \"StartTime\", \"EndTime\");";
        await db.Database.ExecuteSqlRawAsync(appointmentIndex, cancellationToken);
    }

    private static async Task EnsureColumnAsync(SchedulingDbContext db, string provider, string table, string column,
        string sqlite, string postgres, string sqlServer, CancellationToken cancellationToken)
    {
        if (provider.Contains("Sqlite", StringComparison.Ordinal) &&
            await SqliteHasColumnAsync(db, table, column, cancellationToken)) return;
        var definition = provider.Contains("Npgsql", StringComparison.Ordinal) ? postgres
            : provider.Contains("SqlServer", StringComparison.Ordinal) ? sqlServer : sqlite;
        var statement = provider.Contains("Npgsql", StringComparison.Ordinal)
            ? $"ALTER TABLE \"{table}\" ADD COLUMN IF NOT EXISTS \"{column}\" {definition};"
            : provider.Contains("SqlServer", StringComparison.Ordinal)
                ? $"IF COL_LENGTH('{table}', '{column}') IS NULL ALTER TABLE [{table}] ADD [{column}] {definition};"
                : $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
        await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
    }

    private static async Task<bool> SqliteHasColumnAsync(SchedulingDbContext db, string table, string column,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = column;
            command.Parameters.Add(parameter);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "SchedulingProviderWorkingHours" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_SchedulingProviderWorkingHours" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "ProviderId" INTEGER NOT NULL, "LocationId" TEXT NOT NULL,
          "DayOfWeek" INTEGER NOT NULL, "StartLocal" TEXT NOT NULL, "EndLocal" TEXT NOT NULL,
          "IsActive" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_SchedulingProviderWorkingHours_Tenant_Provider_Location_Day"
          ON "SchedulingProviderWorkingHours" ("TenantId", "ProviderId", "LocationId", "DayOfWeek");
        CREATE TABLE IF NOT EXISTS "SchedulingBlockedTimes" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_SchedulingBlockedTimes" PRIMARY KEY,
          "TenantId" TEXT NOT NULL, "ProviderId" INTEGER NULL, "LocationId" TEXT NULL,
          "StartUtc" TEXT NOT NULL, "EndUtc" TEXT NOT NULL, "Reason" TEXT NULL,
          "IsActive" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_SchedulingBlockedTimes_Tenant_Start_End"
          ON "SchedulingBlockedTimes" ("TenantId", "StartUtc", "EndUtc");
        """;

    private const string PostgreSql = """
        CREATE TABLE IF NOT EXISTS "SchedulingProviderWorkingHours" (
          "Id" uuid NOT NULL CONSTRAINT "PK_SchedulingProviderWorkingHours" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "ProviderId" integer NOT NULL, "LocationId" uuid NOT NULL,
          "DayOfWeek" integer NOT NULL, "StartLocal" time without time zone NOT NULL,
          "EndLocal" time without time zone NOT NULL, "IsActive" boolean NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_SchedulingProviderWorkingHours_Tenant_Provider_Location_Day"
          ON "SchedulingProviderWorkingHours" ("TenantId", "ProviderId", "LocationId", "DayOfWeek");
        CREATE TABLE IF NOT EXISTS "SchedulingBlockedTimes" (
          "Id" uuid NOT NULL CONSTRAINT "PK_SchedulingBlockedTimes" PRIMARY KEY,
          "TenantId" varchar(64) NOT NULL, "ProviderId" integer NULL, "LocationId" uuid NULL,
          "StartUtc" timestamp with time zone NOT NULL, "EndUtc" timestamp with time zone NOT NULL,
          "Reason" varchar(200) NULL, "IsActive" boolean NOT NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL);
        CREATE INDEX IF NOT EXISTS "IX_SchedulingBlockedTimes_Tenant_Start_End"
          ON "SchedulingBlockedTimes" ("TenantId", "StartUtc", "EndUtc");
        """;

    private const string SqlServer = """
        IF OBJECT_ID(N'[SchedulingProviderWorkingHours]', N'U') IS NULL BEGIN
          CREATE TABLE [SchedulingProviderWorkingHours] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SchedulingProviderWorkingHours] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [ProviderId] int NOT NULL, [LocationId] uniqueidentifier NOT NULL,
            [DayOfWeek] int NOT NULL, [StartLocal] time NOT NULL, [EndLocal] time NOT NULL,
            [IsActive] bit NOT NULL, [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE INDEX [IX_SchedulingProviderWorkingHours_Tenant_Provider_Location_Day]
            ON [SchedulingProviderWorkingHours] ([TenantId], [ProviderId], [LocationId], [DayOfWeek]); END;
        IF OBJECT_ID(N'[SchedulingBlockedTimes]', N'U') IS NULL BEGIN
          CREATE TABLE [SchedulingBlockedTimes] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SchedulingBlockedTimes] PRIMARY KEY,
            [TenantId] nvarchar(64) NOT NULL, [ProviderId] int NULL, [LocationId] uniqueidentifier NULL,
            [StartUtc] datetime2 NOT NULL, [EndUtc] datetime2 NOT NULL, [Reason] nvarchar(200) NULL,
            [IsActive] bit NOT NULL, [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL);
          CREATE INDEX [IX_SchedulingBlockedTimes_Tenant_Start_End]
            ON [SchedulingBlockedTimes] ([TenantId], [StartUtc], [EndUtc]); END;
        """;
}

using Microsoft.EntityFrameworkCore;

public static class BookingRequestSchema
{
    public static async Task EnsureAsync(SchedulingDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        var sql = provider.Contains("Npgsql", StringComparison.Ordinal)
            ? PostgreSql
            : provider.Contains("SqlServer", StringComparison.Ordinal)
                ? SqlServer
                : Sqlite;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        await EnsureV2ColumnsAsync(db, provider, cancellationToken);
    }

    private static async Task EnsureV2ColumnsAsync(SchedulingDbContext db, string provider, CancellationToken cancellationToken)
    {
        var columns = new (string Name, string Sqlite, string PostgreSql, string SqlServer)[]
        {
            ("WebsiteRequestId", "TEXT NULL", "varchar(128) NULL", "nvarchar(128) NULL"),
            ("AlternateStartUtc", "TEXT NULL", "timestamp with time zone NULL", "datetime2 NULL"),
            ("PreferredContact", "TEXT NULL", "varchar(20) NULL", "nvarchar(20) NULL"),
            ("InsuranceIntent", "TEXT NULL", "varchar(20) NULL", "nvarchar(20) NULL"),
            ("InsuranceCarrier", "TEXT NULL", "varchar(120) NULL", "nvarchar(120) NULL"),
            ("Campaign", "TEXT NULL", "varchar(200) NULL", "nvarchar(200) NULL"),
            ("AttributionId", "TEXT NULL", "varchar(200) NULL", "nvarchar(200) NULL"),
            ("AttributionMetadataJson", "TEXT NULL", "varchar(2000) NULL", "nvarchar(2000) NULL"),
            ("SubmittedAtUtc", "TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP", "timestamp with time zone NOT NULL DEFAULT CURRENT_TIMESTAMP", "datetime2 NOT NULL DEFAULT SYSUTCDATETIME()")
        };

        foreach (var column in columns)
        {
            if (provider.Contains("Sqlite", StringComparison.Ordinal) && await SqliteHasColumnAsync(db, column.Name, cancellationToken))
                continue;
            var definition = provider.Contains("Npgsql", StringComparison.Ordinal) ? column.PostgreSql
                : provider.Contains("SqlServer", StringComparison.Ordinal) ? column.SqlServer : column.Sqlite;
            var statement = provider.Contains("Npgsql", StringComparison.Ordinal)
                ? $"ALTER TABLE \"BookingRequests\" ADD COLUMN IF NOT EXISTS \"{column.Name}\" {definition};"
                : provider.Contains("SqlServer", StringComparison.Ordinal)
                    ? $"IF COL_LENGTH('BookingRequests', '{column.Name}') IS NULL ALTER TABLE [BookingRequests] ADD [{column.Name}] {definition};"
                    : $"ALTER TABLE \"BookingRequests\" ADD COLUMN \"{column.Name}\" {definition};";
            await db.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    private static async Task<bool> SqliteHasColumnAsync(SchedulingDbContext db, string name, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('BookingRequests') WHERE name = $name";
            var parameter = command.CreateParameter(); parameter.ParameterName = "$name"; parameter.Value = name;
            command.Parameters.Add(parameter);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        }
        finally { if (shouldClose) await connection.CloseAsync(); }
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "BookingRequests" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_BookingRequests" PRIMARY KEY,
          "EventId" TEXT NOT NULL, "TenantId" TEXT NOT NULL, "Name" TEXT NOT NULL,
          "Phone" TEXT NOT NULL, "Email" TEXT NULL, "WebsiteRequestId" TEXT NULL, "PatientRelationship" INTEGER NOT NULL,
          "PreferredStartUtc" TEXT NOT NULL, "AlternateStartUtc" TEXT NULL, "PreferredDurationMinutes" INTEGER NULL,
          "Reason" TEXT NULL, "Message" TEXT NULL, "Source" TEXT NOT NULL,
          "PreferredContact" TEXT NULL, "InsuranceIntent" TEXT NULL, "InsuranceCarrier" TEXT NULL,
          "Campaign" TEXT NULL, "AttributionId" TEXT NULL, "AttributionMetadataJson" TEXT NULL,
          "SourceReference" TEXT NULL, "Status" INTEGER NOT NULL, "MatchedPatientId" INTEGER NULL,
          "RequestedProviderId" INTEGER NULL, "RequestedLocationId" TEXT NULL,
          "ApprovedAppointmentId" TEXT NULL, "CreatedAt" TEXT NOT NULL, "SubmittedAtUtc" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
          "ReviewedAt" TEXT NULL, "ApprovedAt" TEXT NULL, "RejectedAt" TEXT NULL,
          "ReviewedBy" TEXT NULL, "ApprovedBy" TEXT NULL, "RejectionReason" TEXT NULL, "StaffNotes" TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_EventId" ON "BookingRequests" ("TenantId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_Status_CreatedAt" ON "BookingRequests" ("TenantId", "Status", "CreatedAt");
        """;

    private const string PostgreSql = """
        CREATE TABLE IF NOT EXISTS "BookingRequests" (
          "Id" uuid NOT NULL CONSTRAINT "PK_BookingRequests" PRIMARY KEY,
          "EventId" uuid NOT NULL, "TenantId" varchar(64) NOT NULL, "Name" varchar(200) NOT NULL,
          "Phone" varchar(30) NOT NULL, "Email" varchar(320) NULL, "WebsiteRequestId" varchar(128) NULL, "PatientRelationship" integer NOT NULL,
          "PreferredStartUtc" timestamp with time zone NOT NULL, "AlternateStartUtc" timestamp with time zone NULL, "PreferredDurationMinutes" integer NULL,
          "Reason" varchar(500) NULL, "Message" varchar(2000) NULL, "Source" varchar(100) NOT NULL,
          "PreferredContact" varchar(20) NULL, "InsuranceIntent" varchar(20) NULL, "InsuranceCarrier" varchar(120) NULL,
          "Campaign" varchar(200) NULL, "AttributionId" varchar(200) NULL, "AttributionMetadataJson" varchar(2000) NULL,
          "SourceReference" varchar(200) NULL, "Status" integer NOT NULL, "MatchedPatientId" integer NULL,
          "RequestedProviderId" integer NULL, "RequestedLocationId" uuid NULL, "ApprovedAppointmentId" uuid NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "SubmittedAtUtc" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL,
          "ReviewedAt" timestamp with time zone NULL, "ApprovedAt" timestamp with time zone NULL,
          "RejectedAt" timestamp with time zone NULL, "ReviewedBy" varchar(200) NULL,
          "ApprovedBy" varchar(200) NULL, "RejectionReason" varchar(1000) NULL, "StaffNotes" varchar(2000) NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_EventId" ON "BookingRequests" ("TenantId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_Status_CreatedAt" ON "BookingRequests" ("TenantId", "Status", "CreatedAt");
        """;

    private const string SqlServer = """
        IF OBJECT_ID(N'[BookingRequests]', N'U') IS NULL BEGIN
          CREATE TABLE [BookingRequests] (
            [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_BookingRequests] PRIMARY KEY,
            [EventId] uniqueidentifier NOT NULL, [TenantId] nvarchar(64) NOT NULL, [Name] nvarchar(200) NOT NULL,
            [Phone] nvarchar(30) NOT NULL, [Email] nvarchar(320) NULL, [WebsiteRequestId] nvarchar(128) NULL, [PatientRelationship] int NOT NULL,
            [PreferredStartUtc] datetime2 NOT NULL, [AlternateStartUtc] datetime2 NULL, [PreferredDurationMinutes] int NULL,
            [Reason] nvarchar(500) NULL, [Message] nvarchar(2000) NULL, [Source] nvarchar(100) NOT NULL,
            [PreferredContact] nvarchar(20) NULL, [InsuranceIntent] nvarchar(20) NULL, [InsuranceCarrier] nvarchar(120) NULL,
            [Campaign] nvarchar(200) NULL, [AttributionId] nvarchar(200) NULL, [AttributionMetadataJson] nvarchar(2000) NULL,
            [SourceReference] nvarchar(200) NULL, [Status] int NOT NULL, [MatchedPatientId] int NULL,
            [RequestedProviderId] int NULL, [RequestedLocationId] uniqueidentifier NULL,
            [ApprovedAppointmentId] uniqueidentifier NULL, [CreatedAt] datetime2 NOT NULL, [SubmittedAtUtc] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL,
            [ReviewedAt] datetime2 NULL, [ApprovedAt] datetime2 NULL, [RejectedAt] datetime2 NULL,
            [ReviewedBy] nvarchar(200) NULL, [ApprovedBy] nvarchar(200) NULL,
            [RejectionReason] nvarchar(1000) NULL, [StaffNotes] nvarchar(2000) NULL);
          CREATE UNIQUE INDEX [IX_BookingRequests_TenantId_EventId] ON [BookingRequests] ([TenantId], [EventId]);
          CREATE INDEX [IX_BookingRequests_TenantId_Status_CreatedAt] ON [BookingRequests] ([TenantId], [Status], [CreatedAt]);
        END
        """;
}

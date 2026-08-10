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
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "BookingRequests" (
          "Id" TEXT NOT NULL CONSTRAINT "PK_BookingRequests" PRIMARY KEY,
          "EventId" TEXT NOT NULL, "TenantId" TEXT NOT NULL, "Name" TEXT NOT NULL,
          "Phone" TEXT NOT NULL, "Email" TEXT NULL, "PatientRelationship" INTEGER NOT NULL,
          "PreferredStartUtc" TEXT NOT NULL, "PreferredDurationMinutes" INTEGER NULL,
          "Reason" TEXT NULL, "Message" TEXT NULL, "Source" TEXT NOT NULL,
          "SourceReference" TEXT NULL, "Status" INTEGER NOT NULL, "MatchedPatientId" INTEGER NULL,
          "RequestedProviderId" INTEGER NULL, "RequestedLocationId" TEXT NULL,
          "ApprovedAppointmentId" TEXT NULL, "CreatedAt" TEXT NOT NULL, "UpdatedAt" TEXT NOT NULL,
          "ReviewedAt" TEXT NULL, "ApprovedAt" TEXT NULL, "RejectedAt" TEXT NULL,
          "ReviewedBy" TEXT NULL, "ApprovedBy" TEXT NULL, "RejectionReason" TEXT NULL, "StaffNotes" TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_EventId" ON "BookingRequests" ("TenantId", "EventId");
        CREATE INDEX IF NOT EXISTS "IX_BookingRequests_TenantId_Status_CreatedAt" ON "BookingRequests" ("TenantId", "Status", "CreatedAt");
        """;

    private const string PostgreSql = """
        CREATE TABLE IF NOT EXISTS "BookingRequests" (
          "Id" uuid NOT NULL CONSTRAINT "PK_BookingRequests" PRIMARY KEY,
          "EventId" uuid NOT NULL, "TenantId" varchar(64) NOT NULL, "Name" varchar(200) NOT NULL,
          "Phone" varchar(30) NOT NULL, "Email" varchar(320) NULL, "PatientRelationship" integer NOT NULL,
          "PreferredStartUtc" timestamp with time zone NOT NULL, "PreferredDurationMinutes" integer NULL,
          "Reason" varchar(500) NULL, "Message" varchar(2000) NULL, "Source" varchar(100) NOT NULL,
          "SourceReference" varchar(200) NULL, "Status" integer NOT NULL, "MatchedPatientId" integer NULL,
          "RequestedProviderId" integer NULL, "RequestedLocationId" uuid NULL, "ApprovedAppointmentId" uuid NULL,
          "CreatedAt" timestamp with time zone NOT NULL, "UpdatedAt" timestamp with time zone NOT NULL,
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
            [Phone] nvarchar(30) NOT NULL, [Email] nvarchar(320) NULL, [PatientRelationship] int NOT NULL,
            [PreferredStartUtc] datetime2 NOT NULL, [PreferredDurationMinutes] int NULL,
            [Reason] nvarchar(500) NULL, [Message] nvarchar(2000) NULL, [Source] nvarchar(100) NOT NULL,
            [SourceReference] nvarchar(200) NULL, [Status] int NOT NULL, [MatchedPatientId] int NULL,
            [RequestedProviderId] int NULL, [RequestedLocationId] uniqueidentifier NULL,
            [ApprovedAppointmentId] uniqueidentifier NULL, [CreatedAt] datetime2 NOT NULL, [UpdatedAt] datetime2 NOT NULL,
            [ReviewedAt] datetime2 NULL, [ApprovedAt] datetime2 NULL, [RejectedAt] datetime2 NULL,
            [ReviewedBy] nvarchar(200) NULL, [ApprovedBy] nvarchar(200) NULL,
            [RejectionReason] nvarchar(1000) NULL, [StaffNotes] nvarchar(2000) NULL);
          CREATE UNIQUE INDEX [IX_BookingRequests_TenantId_EventId] ON [BookingRequests] ([TenantId], [EventId]);
          CREATE INDEX [IX_BookingRequests_TenantId_Status_CreatedAt] ON [BookingRequests] ([TenantId], [Status], [CreatedAt]);
        END
        """;
}

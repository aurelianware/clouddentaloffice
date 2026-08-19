using Microsoft.EntityFrameworkCore;

public static class SearchConsoleSchema
{
    public static async Task EnsureAsync(SchedulingDbContext db, CancellationToken cancellationToken = default)
    {
        var provider = db.Database.ProviderName ?? "";
        var sql = provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ? Sqlite
            : provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ? Postgres : SqlServer;
        await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private const string Sqlite = """
        CREATE TABLE IF NOT EXISTS "SearchConsoleIntegrations" ("Id" TEXT NOT NULL PRIMARY KEY,"TenantId" TEXT NOT NULL,"Enabled" INTEGER NOT NULL,"PropertyUrl" TEXT NOT NULL,"CredentialReference" TEXT NOT NULL,"CanonicalHost" TEXT NULL,"SyncStatus" INTEGER NOT NULL,"LastSuccessfulSyncAt" TEXT NULL,"LastAttemptAt" TEXT NULL,"NextSyncAt" TEXT NULL,"LatestImportedDate" TEXT NULL,"LastError" TEXT NULL,"LockId" TEXT NULL,"LockedUntil" TEXT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SearchConsoleIntegration_Tenant" ON "SearchConsoleIntegrations" ("TenantId");
        CREATE INDEX IF NOT EXISTS "IX_SearchConsoleIntegration_Due" ON "SearchConsoleIntegrations" ("Enabled","NextSyncAt");
        CREATE TABLE IF NOT EXISTS "SearchPerformanceDaily" ("Id" TEXT NOT NULL PRIMARY KEY,"TenantId" TEXT NOT NULL,"Date" TEXT NOT NULL,"Query" TEXT NOT NULL,"PagePath" TEXT NOT NULL,"Device" TEXT NOT NULL,"IsProduction" INTEGER NOT NULL,"Clicks" INTEGER NOT NULL,"Impressions" INTEGER NOT NULL,"PositionSum" REAL NOT NULL,"ImportedAt" TEXT NOT NULL,"SourceProperty" TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SearchDaily_Unique" ON "SearchPerformanceDaily" ("TenantId","Date","Query","PagePath","Device");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Date" ON "SearchPerformanceDaily" ("TenantId","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Page_Date" ON "SearchPerformanceDaily" ("TenantId","PagePath","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Query_Date" ON "SearchPerformanceDaily" ("TenantId","Query","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Device_Date" ON "SearchPerformanceDaily" ("TenantId","Device","Date");
        """;
    private const string Postgres = """
        CREATE TABLE IF NOT EXISTS "SearchConsoleIntegrations" ("Id" uuid NOT NULL PRIMARY KEY,"TenantId" varchar(64) NOT NULL,"Enabled" boolean NOT NULL,"PropertyUrl" varchar(512) NOT NULL,"CredentialReference" varchar(256) NOT NULL,"CanonicalHost" varchar(256) NULL,"SyncStatus" integer NOT NULL,"LastSuccessfulSyncAt" timestamp with time zone NULL,"LastAttemptAt" timestamp with time zone NULL,"NextSyncAt" timestamp with time zone NULL,"LatestImportedDate" date NULL,"LastError" varchar(128) NULL,"LockId" uuid NULL,"LockedUntil" timestamp with time zone NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SearchConsoleIntegration_Tenant" ON "SearchConsoleIntegrations" ("TenantId");
        CREATE INDEX IF NOT EXISTS "IX_SearchConsoleIntegration_Due" ON "SearchConsoleIntegrations" ("Enabled","NextSyncAt");
        CREATE TABLE IF NOT EXISTS "SearchPerformanceDaily" ("Id" uuid NOT NULL PRIMARY KEY,"TenantId" varchar(64) NOT NULL,"Date" date NOT NULL,"Query" varchar(500) NOT NULL,"PagePath" varchar(300) NOT NULL,"Device" varchar(20) NOT NULL,"IsProduction" boolean NOT NULL,"Clicks" bigint NOT NULL,"Impressions" bigint NOT NULL,"PositionSum" double precision NOT NULL,"ImportedAt" timestamp with time zone NOT NULL,"SourceProperty" varchar(512) NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_SearchDaily_Unique" ON "SearchPerformanceDaily" ("TenantId","Date","Query","PagePath","Device");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Date" ON "SearchPerformanceDaily" ("TenantId","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Page_Date" ON "SearchPerformanceDaily" ("TenantId","PagePath","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Query_Date" ON "SearchPerformanceDaily" ("TenantId","Query","Date");
        CREATE INDEX IF NOT EXISTS "IX_SearchDaily_Tenant_Device_Date" ON "SearchPerformanceDaily" ("TenantId","Device","Date");
        """;
    private const string SqlServer = """
        IF OBJECT_ID(N'[SearchConsoleIntegrations]',N'U') IS NULL BEGIN CREATE TABLE [SearchConsoleIntegrations] ([Id] uniqueidentifier NOT NULL PRIMARY KEY,[TenantId] nvarchar(64) NOT NULL,[Enabled] bit NOT NULL,[PropertyUrl] nvarchar(512) NOT NULL,[CredentialReference] nvarchar(256) NOT NULL,[CanonicalHost] nvarchar(256) NULL,[SyncStatus] int NOT NULL,[LastSuccessfulSyncAt] datetime2 NULL,[LastAttemptAt] datetime2 NULL,[NextSyncAt] datetime2 NULL,[LatestImportedDate] date NULL,[LastError] nvarchar(128) NULL,[LockId] uniqueidentifier NULL,[LockedUntil] datetime2 NULL); CREATE UNIQUE INDEX [IX_SearchConsoleIntegration_Tenant] ON [SearchConsoleIntegrations] ([TenantId]); CREATE INDEX [IX_SearchConsoleIntegration_Due] ON [SearchConsoleIntegrations] ([Enabled],[NextSyncAt]); END;
        IF OBJECT_ID(N'[SearchPerformanceDaily]',N'U') IS NULL BEGIN CREATE TABLE [SearchPerformanceDaily] ([Id] uniqueidentifier NOT NULL PRIMARY KEY,[TenantId] nvarchar(64) NOT NULL,[Date] date NOT NULL,[Query] nvarchar(500) NOT NULL,[PagePath] nvarchar(300) NOT NULL,[Device] nvarchar(20) NOT NULL,[IsProduction] bit NOT NULL,[Clicks] bigint NOT NULL,[Impressions] bigint NOT NULL,[PositionSum] float NOT NULL,[ImportedAt] datetime2 NOT NULL,[SourceProperty] nvarchar(512) NOT NULL); CREATE UNIQUE INDEX [IX_SearchDaily_Unique] ON [SearchPerformanceDaily] ([TenantId],[Date],[Query],[PagePath],[Device]); CREATE INDEX [IX_SearchDaily_Tenant_Date] ON [SearchPerformanceDaily] ([TenantId],[Date]); CREATE INDEX [IX_SearchDaily_Tenant_Page_Date] ON [SearchPerformanceDaily] ([TenantId],[PagePath],[Date]); CREATE INDEX [IX_SearchDaily_Tenant_Query_Date] ON [SearchPerformanceDaily] ([TenantId],[Query],[Date]); CREATE INDEX [IX_SearchDaily_Tenant_Device_Date] ON [SearchPerformanceDaily] ([TenantId],[Device],[Date]); END;
        """;
}

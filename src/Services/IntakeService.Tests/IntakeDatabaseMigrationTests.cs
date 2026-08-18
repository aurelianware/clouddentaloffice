using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

/// <summary>
/// Exercises the migration-based schema lifecycle against a real PostgreSQL
/// database — the provider IntakeService runs on in every deployed environment.
///
/// These tests run when a PostgreSQL server is available via the
/// <c>INTAKE_TEST_POSTGRES</c> environment variable (set in CI). Each test
/// provisions and drops its own database, so they never share state. When the
/// variable is absent (a developer running <c>dotnet test</c> without PostgreSQL)
/// the tests skip rather than fail, keeping the default suite deterministic.
/// </summary>
public sealed class IntakeDatabaseMigrationTests
{
    private const string ServerEnvVar = "INTAKE_TEST_POSTGRES";

    private static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable(ServerEnvVar);

    [SkippableFact]
    public async Task Fresh_database_is_created_by_migrations_with_inbox_and_unique_constraint()
    {
        await using var database = await PostgresDatabase.CreateAsync();
        var readiness = new IntakeDatabaseReadiness();

        await IntakeDatabaseInitializer.InitializeAsync(BuildServices(database, readiness), migrateOnStartup: true);

        Assert.True(readiness.SchemaReady);
        await using var db = database.CreateContext();
        Assert.True(await TableExistsAsync(db, "IntegrationInboxMessages"));
        Assert.True(await TableExistsAsync(db, "__EFMigrationsHistory"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

        var index = await ScalarAsync(db,
            "SELECT indexdef FROM pg_indexes " +
            "WHERE tablename = 'IntegrationInboxMessages' " +
            "AND indexname = 'IX_IntegrationInboxMessages_TenantId_Channel_ExternalEventId'");
        Assert.NotNull(index);
        Assert.Contains("UNIQUE", index!, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Inbox_unique_constraint_is_enforced_after_migration()
    {
        await using var database = await PostgresDatabase.CreateAsync();
        await IntakeDatabaseInitializer.InitializeAsync(
            BuildServices(database, new IntakeDatabaseReadiness()), migrateOnStartup: true);

        await using var db = database.CreateContext();
        db.AddRange(Row("dupe"), Row("dupe"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [SkippableFact]
    public async Task Repeated_migration_application_is_idempotent()
    {
        await using var database = await PostgresDatabase.CreateAsync();

        await IntakeDatabaseInitializer.InitializeAsync(
            BuildServices(database, new IntakeDatabaseReadiness()), migrateOnStartup: true);
        // A second start against an already-current database must be a safe no-op.
        var readiness = new IntakeDatabaseReadiness();
        await IntakeDatabaseInitializer.InitializeAsync(BuildServices(database, readiness), migrateOnStartup: true);

        Assert.True(readiness.SchemaReady);
        await using var db = database.CreateContext();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [SkippableFact]
    public async Task Existing_EnsureCreated_schema_is_baselined_and_data_survives()
    {
        await using var database = await PostgresDatabase.CreateAsync();

        // Simulate a database originally provisioned with EnsureCreated: the tables
        // exist but there is no migration history table.
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Add(Row("pre-existing"));
            await seed.SaveChangesAsync();
        }
        await using (var check = database.CreateContext())
            Assert.False(await TableExistsAsync(check, "__EFMigrationsHistory"));

        var readiness = new IntakeDatabaseReadiness();
        await IntakeDatabaseInitializer.InitializeAsync(BuildServices(database, readiness), migrateOnStartup: true);

        Assert.True(readiness.SchemaReady);
        await using var db = database.CreateContext();
        Assert.True(await TableExistsAsync(db, "__EFMigrationsHistory"));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        // The pre-existing row must survive baselining untouched.
        Assert.Equal(1, await db.IntegrationInboxMessages.CountAsync(x => x.ExternalEventId == "pre-existing"));
    }

    [SkippableFact]
    public async Task Durable_inbox_persists_and_dispatches_on_migration_created_schema()
    {
        await using var database = await PostgresDatabase.CreateAsync();
        await IntakeDatabaseInitializer.InitializeAsync(
            BuildServices(database, new IntakeDatabaseReadiness()), migrateOnStartup: true);

        var clock = new TestClock(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        await using (var db = database.CreateContext())
        {
            var first = await new IntegrationInbox(db, clock).PersistAsync("tenant-a", "Zocdoc", "evt-1",
                nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "evt-1"));
            var replay = await new IntegrationInbox(db, clock).PersistAsync("tenant-a", "Zocdoc", "evt-1",
                nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "evt-1"));
            Assert.True(first.Created);
            Assert.False(replay.Created);
            Assert.Equal(first.Id, replay.Id);
        }

        await using (var db = database.CreateContext())
        {
            var publisher = new RecordingPublisher();
            var dispatcher = new IntegrationInboxDispatcher(db, publisher,
                new ServiceBusOptions { ConnectionString = "configured" },
                Microsoft.Extensions.Options.Options.Create(new IntegrationInboxOptions()),
                clock, new ZocdocWebhookMetrics(), NullLogger<IntegrationInboxDispatcher>.Instance);
            Assert.Equal(1, await dispatcher.DispatchBatchAsync());
            Assert.Single(publisher.Events);
            Assert.Equal(IntegrationInboxStatus.Published,
                await db.IntegrationInboxMessages.AsNoTracking().Select(x => x.Status).SingleAsync());
        }
    }

    [SkippableFact]
    public async Task Verify_mode_fails_closed_when_schema_is_behind()
    {
        await using var database = await PostgresDatabase.CreateAsync();
        var readiness = new IntakeDatabaseReadiness();

        // Empty database, startup migration disabled: the schema is behind the binary.
        await Assert.ThrowsAsync<IntakeDatabaseSchemaOutdatedException>(() =>
            IntakeDatabaseInitializer.InitializeAsync(BuildServices(database, readiness), migrateOnStartup: false));

        Assert.False(readiness.SchemaReady);
    }

    [SkippableFact]
    public async Task Readiness_check_reports_healthy_only_after_initialization()
    {
        await using var database = await PostgresDatabase.CreateAsync();
        var readiness = new IntakeDatabaseReadiness();

        await using (var db = database.CreateContext())
        {
            var before = await new IntakeDatabaseReadinessHealthCheck(readiness, db)
                .CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, before.Status);
        }

        await IntakeDatabaseInitializer.InitializeAsync(BuildServices(database, readiness), migrateOnStartup: true);

        await using (var db = database.CreateContext())
        {
            var after = await new IntakeDatabaseReadinessHealthCheck(readiness, db)
                .CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, after.Status);
        }
    }

    [Fact]
    public async Task Unreachable_database_fails_closed_without_marking_ready()
    {
        // No PostgreSQL required: a dead endpoint with a short timeout returns
        // CanConnect=false, which must fail closed.
        var readiness = new IntakeDatabaseReadiness();
        var services = new ServiceCollection()
            .AddSingleton(readiness)
            .AddLogging()
            .AddDbContext<IntakeDbContext>(o => o.UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=nope;Username=none;Timeout=1;Command Timeout=1"))
            .BuildServiceProvider();

        await Assert.ThrowsAsync<IntakeDatabaseUnavailableException>(() =>
            IntakeDatabaseInitializer.InitializeAsync(services, migrateOnStartup: true));
        Assert.False(readiness.SchemaReady);
    }

    private static IServiceProvider BuildServices(PostgresDatabase database, IntakeDatabaseReadiness readiness) =>
        new ServiceCollection()
            .AddSingleton(readiness)
            .AddLogging()
            .AddDbContext<IntakeDbContext>(o => o.UseNpgsql(database.ConnectionString))
            .BuildServiceProvider();

    private static async Task<bool> TableExistsAsync(IntakeDbContext db, string table) =>
        await ScalarAsync(db, $"SELECT to_regclass('public.\"{table}\"')::text") is not null;

    private static async Task<string?> ScalarAsync(IntakeDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : result.ToString();
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    private static IntegrationInboxMessage Row(string eventId) => new()
    {
        TenantId = "tenant-a", Channel = "Zocdoc", ExternalEventId = eventId,
        EventType = nameof(ZocdocAppointmentWebhookEvent),
        ReceivedAt = new DateTime(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc),
        Payload = "{}"
    };

    private static ZocdocAppointmentWebhookEvent Event(string tenant, string id) =>
        new(tenant, id, "appointment-1", "created");

    /// <summary>Provisions an isolated PostgreSQL database for one test and drops it on disposal.</summary>
    private sealed class PostgresDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;
        public string ConnectionString { get; }

        private PostgresDatabase(string adminConnectionString, string databaseName)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            var builder = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName };
            ConnectionString = builder.ConnectionString;
        }

        public static async Task<PostgresDatabase> CreateAsync()
        {
            var server = ServerConnectionString;
            Skip.If(string.IsNullOrWhiteSpace(server),
                $"Set {ServerEnvVar} to a PostgreSQL connection string to run migration tests.");

            var name = $"intake_test_{Guid.NewGuid():N}";
            await using (var admin = new NpgsqlConnection(server))
            {
                await admin.OpenAsync();
                await using var command = admin.CreateCommand();
                command.CommandText = $"CREATE DATABASE \"{name}\"";
                await command.ExecuteNonQueryAsync();
            }
            return new PostgresDatabase(server!, name);
        }

        public IntakeDbContext CreateContext() =>
            new(new DbContextOptionsBuilder<IntakeDbContext>().UseNpgsql(ConnectionString).Options);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var command = admin.CreateCommand();
            command.CommandText =
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)";
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<IntegrationEvent> Events { get; } = [];
        public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        {
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

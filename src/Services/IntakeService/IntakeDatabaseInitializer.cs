using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Brings the IntakeService database schema to the version the running binary
/// expects, before the web host starts serving traffic or the durable inbox
/// dispatcher begins processing.
///
/// The service <b>fails closed</b>: if the database is unreachable or the schema
/// is not at the expected migration version, initialization throws and the host
/// never starts. IntakeService therefore never accepts Zocdoc webhooks, and the
/// dispatcher never runs, against an unknown or outdated schema.
///
/// PostgreSQL is the only deployed provider (see container-apps.bicep). Migrations
/// are authored against Npgsql, so:
///  * On PostgreSQL we apply/verify EF migrations.
///  * On SQLite (local development and tests only, where the Npgsql migration DDL
///    does not apply) we fall back to <see cref="RelationalDatabaseFacadeExtensions"/>
///    schema creation from the model. SQLite is never used in a deployed environment.
/// </summary>
public static class IntakeDatabaseInitializer
{
    // A stable, service-specific key for the PostgreSQL session-level advisory lock
    // that serializes schema changes across concurrently starting replicas. Any
    // fixed 64-bit value unique to this service works; it is not a secret.
    private const long MigrationAdvisoryLockKey = 5_270_114_001L;

    public static async Task InitializeAsync(
        IServiceProvider services,
        bool migrateOnStartup,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IntakeDbContext>();
        var readiness = scope.ServiceProvider.GetRequiredService<IntakeDatabaseReadiness>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("IntakeDatabaseInitializer");

        // SQLite is only reached in local development / tests. Npgsql migration DDL
        // cannot run on SQLite, so build the current model schema directly. This is
        // never used by a deployed environment (DatabaseProvider=PostgreSQL).
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
            readiness.MarkReady();
            return;
        }

        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            // Do not log the connection string, credentials, or tokens.
            logger.LogCritical(
                "IntakeService cannot reach its database. Refusing to start until connectivity is restored.");
            throw new IntakeDatabaseUnavailableException("The IntakeService database is unreachable.");
        }

        var connection = db.Database.GetDbConnection();
        var reopened = connection.State != System.Data.ConnectionState.Open;
        if (reopened) await connection.OpenAsync(cancellationToken);
        try
        {
            // Serialize schema changes across replicas. The lock is held on this
            // session for the duration of the migration; other replicas block here
            // and then observe an up-to-date schema with nothing pending.
            await ExecuteAsync(connection, $"SELECT pg_advisory_lock({MigrationAdvisoryLockKey})", cancellationToken);
            try
            {
                await BaselineExistingSchemaAsync(db, connection, logger, cancellationToken);

                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (migrateOnStartup)
                {
                    if (pending.Count > 0)
                    {
                        logger.LogInformation(
                            "Applying {PendingCount} pending IntakeService database migration(s).", pending.Count);
                        await db.Database.MigrateAsync(cancellationToken);
                    }
                    else
                    {
                        logger.LogInformation("IntakeService database schema is already up to date.");
                    }
                }
                else if (pending.Count > 0)
                {
                    // A dedicated deployment migration step owns schema changes here.
                    // The schema is behind the binary: fail closed rather than serve
                    // against an outdated schema.
                    logger.LogCritical(
                        "IntakeService database has {PendingCount} pending migration(s) and startup migration is " +
                        "disabled. Run the deployment migration step before deploying this revision.", pending.Count);
                    throw new IntakeDatabaseSchemaOutdatedException(
                        $"{pending.Count} pending migration(s) must be applied by the deployment migration step.");
                }
            }
            finally
            {
                await ExecuteAsync(connection, $"SELECT pg_advisory_unlock({MigrationAdvisoryLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            if (reopened) await connection.CloseAsync();
        }

        // Confirm the schema is genuinely at the expected version before allowing traffic.
        var stillPending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (stillPending.Count > 0)
        {
            logger.LogCritical(
                "IntakeService schema is still behind after initialization ({PendingCount} pending). Refusing traffic.",
                stillPending.Count);
            throw new IntakeDatabaseSchemaOutdatedException("Schema did not reach the expected migration version.");
        }

        readiness.MarkReady();
    }

    /// <summary>
    /// Safely adopts a database that was originally provisioned with EnsureCreated
    /// (which never records migration history). If the application tables already
    /// exist but the migration history table does not, the baseline migration is
    /// stamped as applied so that <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/>
    /// does not attempt to recreate existing tables and fail. This runs at most once
    /// per database and is a no-op on a fresh database or an already-migrated one.
    /// </summary>
    private static async Task BaselineExistingSchemaAsync(
        IntakeDbContext db, DbConnection connection, ILogger logger, CancellationToken cancellationToken)
    {
        var history = db.GetService<IHistoryRepository>();
        if (await history.ExistsAsync(cancellationToken)) return; // already migration-managed

        var inboxExists = await ExecuteScalarBoolAsync(connection,
            "SELECT to_regclass('public.\"IntegrationInboxMessages\"') IS NOT NULL", cancellationToken);
        if (!inboxExists) return; // fresh database: MigrateAsync will create everything

        var baselineMigrationId = db.Database.GetMigrations().First();
        logger.LogWarning(
            "Existing IntakeService schema found without migration history. Baselining as {MigrationId}.",
            baselineMigrationId);

        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(baselineMigrationId, ProductInfo.GetVersion())), cancellationToken);
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> ExecuteScalarBoolAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is true;
    }
}

/// <summary>Thrown when the database cannot be reached during startup initialization.</summary>
public sealed class IntakeDatabaseUnavailableException(string message) : Exception(message);

/// <summary>Thrown when the schema is not at the migration version the binary expects.</summary>
public sealed class IntakeDatabaseSchemaOutdatedException(string message) : Exception(message);

/// <summary>
/// Tracks whether the database schema has been successfully initialized, so the
/// readiness probe can reflect schema availability independently of liveness.
/// </summary>
public sealed class IntakeDatabaseReadiness
{
    private volatile bool _schemaReady;
    public bool SchemaReady => _schemaReady;
    public void MarkReady() => _schemaReady = true;
}

/// <summary>
/// Readiness check: reports Healthy only once the schema has been initialized to
/// the expected migration version and the database is currently reachable. Wired to
/// the readiness probe so the platform stops routing traffic to an instance whose
/// database is unavailable, without treating the process as dead (see liveness).
/// </summary>
public sealed class IntakeDatabaseReadinessHealthCheck(
    IntakeDatabaseReadiness readiness, IntakeDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!readiness.SchemaReady)
            return HealthCheckResult.Unhealthy("Database schema has not been initialized.");

        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Database is currently unreachable.");
    }
}

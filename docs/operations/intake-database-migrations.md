# IntakeService database migrations

IntakeService owns the durable Zocdoc webhook inbox (`IntegrationInboxMessages`).
Its schema is managed with **EF Core migrations** so that changes are applied to
existing production databases predictably and safely. This replaces the previous
`EnsureCreatedAsync()` bootstrap, which can only create a schema once and never
evolves an existing one.

- **Provider:** PostgreSQL in every deployed environment
  (`DatabaseProvider=PostgreSQL`, see `infrastructure/azure/container-apps.bicep`).
  SQLite is used only for local development and tests.
- **Migrations location:** `src/Services/IntakeService/Migrations/`
- **Baseline migration:** `InitialCreate` — creates `IntegrationInboxMessages`
  with all columns (status, retry metadata, timestamps, lease/claim fields, and the
  `text` payload column) and the **unique index** on
  `(TenantId, Channel, ExternalEventId)` that enforces once-only ingestion per
  tenant + channel + external event.

## Migration strategy

IntakeService applies migrations **at application startup**, guarded by a
PostgreSQL **session-level advisory lock** so that multiple replicas starting
concurrently (the Container App scales 1–3 replicas and does rolling deployments)
serialize on schema changes. Only one replica applies the migrations; the others
block briefly and then observe an up-to-date schema.

This is the simplest reliable approach for the current Azure Container Apps
architecture, which has no separate pre-deploy hook wired for IntakeService. The
alternative — a dedicated deployment migration step — is supported as an opt-in
(see [Applying migrations as a deployment step](#applying-migrations-as-a-deployment-step)).

### Fail-closed guarantee

`IntakeDatabaseInitializer.InitializeAsync` runs **before** the web host starts.
If the database is unreachable, or the schema cannot be brought to the expected
migration version, initialization throws and the host never starts. As a result
IntakeService **never accepts Zocdoc webhooks and never runs the inbox dispatcher
against an unknown or outdated schema**. Failure logs identify the problem
(unreachable database / pending migrations) **without** logging connection
strings, credentials, tokens, or webhook payloads.

### Configuration

| Setting | Default | Behavior |
| --- | --- | --- |
| `Database:MigrateOnStartup` | `true` | Each instance applies pending migrations at startup under the advisory lock. |
| `Database:MigrateOnStartup` | `false` | Instances do **not** modify the schema. They verify the schema is current and **fail closed** if any migration is pending. Use this when a deployment migration step owns schema changes. |

### Readiness vs liveness

Health endpoints are split so a transient database outage does not restart a live
process:

| Endpoint | Meaning | Probe |
| --- | --- | --- |
| `/health`, `/health/live` | Process is alive. No database dependency. | Liveness |
| `/health/ready` | Schema initialized to the expected version **and** database currently reachable. | Readiness |

When the database blips after a healthy start, readiness turns unhealthy (traffic
stops) while liveness stays healthy (the process is not killed).

## Normal deployment

```
migration exists in the image
      ↓
deploy new revision
      ↓
first replica acquires the advisory lock and applies pending migrations
      ↓
schema is at the expected version; readiness (/health/ready) turns healthy
      ↓
traffic is accepted; the inbox dispatcher begins processing
```

New (empty) environment: `MigrateAsync()` creates the database schema from the
migrations and records migration history. Existing (already-migrated) environment:
startup is a no-op because no migrations are pending.

## Roll forward

New schema versions are delivered by adding a migration and deploying:

```bash
# From the repository root
dotnet ef migrations add <DescriptiveName> \
  --project src/Services/IntakeService/IntakeService.csproj \
  --startup-project src/Services/IntakeService/IntakeService.csproj \
  --output-dir Migrations
```

Commit the generated files under `Migrations/` (including the updated
`IntakeDbContextModelSnapshot.cs`). CI’s **migration integrity** job fails if the
model changes without a matching migration. On the next deployment the new
migration is applied by the same startup path.

## Rollback

EF Core migrations favor **roll-forward** recovery. Rolling the application binary
back to a previous image does **not** automatically reverse a database change that
a newer migration already applied — an older binary simply sees a schema that is
ahead of it.

To reverse a schema change, author the reversal explicitly:

- Preferred: add a **new** migration that undoes the change and deploy it
  (roll forward to a corrected state).
- If you must revert to a specific prior migration, run a targeted downgrade with a
  maintenance connection (never a production app instance):

  ```bash
  ConnectionStrings__IntakeDb="<maintenance connection string>" \
  dotnet ef database update <PreviousMigrationName> \
    --project src/Services/IntakeService/IntakeService.csproj \
    --startup-project src/Services/IntakeService/IntakeService.csproj
  ```

  Only migrations with a meaningful `Down` are safely reversible; a `Down` that
  drops a table discards data. Prefer roll-forward for anything holding inbox data.

## Existing pre-migration environments (baseline)

**This is important.** An environment first initialized with `EnsureCreated()` has
the application tables **without** the `__EFMigrationsHistory` table. Running the
initial migration blindly there would try to recreate existing tables and fail.

IntakeService handles this automatically and safely: on startup, if the migration
history table is absent **but** `IntegrationInboxMessages` already exists, the
`InitialCreate` migration is **stamped as already applied** (the history table is
created and the baseline row inserted) before `MigrateAsync()` runs. `MigrateAsync`
then applies only newer migrations. This bootstrap runs at most once per database
and is a no-op on a fresh database or an already-migrated one.

If you prefer to baseline manually before deploying (equivalent to the automatic
step), run this once against the existing database with a maintenance connection:

```sql
CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260818215256_InitialCreate', '8.0.0')
ON CONFLICT ("MigrationId") DO NOTHING;
```

> The baseline is safe only because the `InitialCreate` migration and the
> `EnsureCreated` model are generated from the same entity model and therefore
> produce the same table. Do not stamp a database whose schema differs from
> `InitialCreate`.

## Applying migrations as a deployment step

To take schema changes out of the application startup path (for example, to apply
them once from CI/CD before rolling out replicas), set `Database:MigrateOnStartup=false`
and apply migrations with one of:

Apply directly with the EF tooling and a maintenance connection string
(never embed a production connection string in source or images):

```bash
ConnectionStrings__IntakeDb="<maintenance connection string>" \
dotnet ef database update \
  --project src/Services/IntakeService/IntakeService.csproj \
  --startup-project src/Services/IntakeService/IntakeService.csproj
```

Or generate an **idempotent** SQL script and apply it with `psql` (safe to run
repeatedly; only applies migrations not yet recorded):

```bash
dotnet ef migrations script --idempotent \
  --project src/Services/IntakeService/IntakeService.csproj \
  --startup-project src/Services/IntakeService/IntakeService.csproj \
  --output intake-migrations.sql
```

Generated scripts are **not** committed to the repository. With
`MigrateOnStartup=false`, any replica whose schema is behind fails closed until the
step has run.

## Inspecting migration state

```bash
# List migrations and whether they are applied (needs a reachable database)
ConnectionStrings__IntakeDb="<connection string>" \
dotnet ef migrations list \
  --project src/Services/IntakeService/IntakeService.csproj \
  --startup-project src/Services/IntakeService/IntakeService.csproj

# Fail if the model has changed without a matching migration (no database needed)
dotnet ef migrations has-pending-model-changes \
  --project src/Services/IntakeService/IntakeService.csproj \
  --startup-project src/Services/IntakeService/IntakeService.csproj
```

The `dotnet ef` design-time context comes from `IntakeDbContextDesignTimeFactory`,
which uses a non-secret local placeholder connection string; real connection
strings are supplied through the `ConnectionStrings__IntakeDb` environment variable
at run time.

## CI coverage

- **Tests** run against a real PostgreSQL service container. The IntakeService
  migration lifecycle tests (`IntakeDatabaseMigrationTests`) validate: empty
  database created by migrations, all expected tables present, the inbox unique
  constraint, existing data surviving baselining, repeated (idempotent) application,
  fail-closed behavior when the schema is behind, and the durable inbox working on a
  migration-created schema. They skip cleanly when `INTAKE_TEST_POSTGRES` is unset so
  local runs stay deterministic.
- **Migration integrity** job runs `dotnet ef migrations has-pending-model-changes`
  so model/migration drift fails the build.

## Azure / container implications

- **Scaling from zero / cold start:** the first replica migrates under the advisory
  lock before serving; readiness gates traffic until the schema is ready.
- **Multiple replicas / rolling deployment:** the advisory lock serializes schema
  changes; only one replica applies migrations, avoiding a race where replicas
  independently attempt schema initialization.
- **Deployment failure:** if migration cannot complete, the instance fails to start
  and readiness never turns healthy — the platform does not route traffic to it, and
  no webhooks are accepted against an unknown schema.

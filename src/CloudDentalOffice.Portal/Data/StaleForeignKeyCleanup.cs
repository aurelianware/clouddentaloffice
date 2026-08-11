using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Data;

/// <summary>
/// Drops cross-service foreign key constraints that the EF model no longer
/// declares (Patient/Provider/Appointment/ClaimProcedure live in separate
/// services, so those relationships are validated at the application layer, not
/// by the database).
///
/// These constraints were removed by the <c>RemoveAppointmentForeignKeys</c>,
/// <c>RemoveProceduresClinicalNotesForeignKeys</c> and
/// <c>RemoveTreatmentPlanForeignKeys</c> migrations. Production, however,
/// provisions its schema with <see cref="DatabaseFacade.EnsureCreated"/> rather
/// than migrations (see Program.cs), and <c>EnsureCreated</c> never alters an
/// existing database. A database first created while the constraints still
/// existed therefore keeps enforcing them, and every insert fails with
/// "23503 ... violates foreign key constraint FK_Appointments_Patients_PatientId"
/// (surfaced to users as "An error occurred while saving the entity changes").
///
/// This runs the equivalent of those removal migrations idempotently on startup
/// so existing databases are reconciled with the current model. On a freshly
/// created database the constraints are already absent and every statement is a
/// no-op thanks to <c>DROP CONSTRAINT IF EXISTS</c>.
/// </summary>
public static class StaleForeignKeyCleanup
{
    // (table, constraint) pairs for every cross-service FK the model dropped.
    private static readonly (string Table, string Constraint)[] StaleForeignKeys =
    {
        ("Appointments", "FK_Appointments_Patients_PatientId"),
        ("Appointments", "FK_Appointments_Providers_ProviderId"),
        ("Claims", "FK_Claims_Patients_PatientId"),
        ("Claims", "FK_Claims_Providers_ProviderId"),
        ("ClinicalNotes", "FK_ClinicalNotes_Patients_PatientId"),
        ("ClinicalNotes", "FK_ClinicalNotes_Providers_ProviderId"),
        ("PlannedProcedures", "FK_PlannedProcedures_ClaimProcedures_ClaimProcedureId"),
        ("Procedures", "FK_Procedures_Appointments_AppointmentId"),
        ("Procedures", "FK_Procedures_Patients_PatientId"),
        ("Procedures", "FK_Procedures_Providers_ProviderId"),
        ("TreatmentPlans", "FK_TreatmentPlans_Patients_PatientId"),
        ("TreatmentPlans", "FK_TreatmentPlans_Providers_ProviderId"),
    };

    /// <summary>
    /// Reconciles the database schema. Only PostgreSQL is supported (the
    /// production provider); other providers are skipped because SQLite (dev)
    /// applies the removal migrations directly and cannot drop constraints via
    /// ALTER TABLE.
    /// </summary>
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

        foreach (var (table, constraint) in StaleForeignKeys)
        {
            // Identifiers are fixed, code-defined constants (never user input),
            // and PostgreSQL requires them quoted to preserve PascalCase.
            var sql = $"ALTER TABLE \"{table}\" DROP CONSTRAINT IF EXISTS \"{constraint}\";";

            try
            {
                await dbContext.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                // A single failure (e.g. the table doesn't exist yet) must not
                // block startup or the remaining drops.
                logger.LogWarning(ex, "Could not drop stale foreign key {Constraint} on {Table}", constraint, table);
            }
        }

        logger.LogInformation("Cross-service foreign key reconciliation completed");
    }
}

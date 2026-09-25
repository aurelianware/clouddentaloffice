-- 4b · Rollback of 11-migrate-into-portal.sql. Use only inside the maintenance window,
-- before the Portal reopens. After staff have written against the migrated patients, restore
-- the pre-migration backup instead (see README.md).
-- Run from the directory holding scheduling-patient-refs.csv:
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 13-rollback-portal.sql
-- One transaction. It deletes only rows recorded in "_patient_store_migration" after refusing
-- any rollback that would orphan claims, tenant-scoped Portal rows, or scheduling references.
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '10s';
LOCK TABLE "Patients", "PatientInsurances", "InsurancePlans" IN SHARE ROW EXCLUSIVE MODE;

CREATE TEMP TABLE stage_scheduling_refs (
    "Source" text NOT NULL,
    "TenantId" varchar(64) NOT NULL,
    "PatientId" integer NOT NULL
) ON COMMIT DROP;
\copy stage_scheduling_refs FROM 'scheduling-patient-refs.csv' WITH (FORMAT csv, HEADER)

CREATE TEMP TABLE rollback_blockers (
    source text PRIMARY KEY,
    blocking_rows bigint NOT NULL
) ON COMMIT DROP;

DO $rollback$
DECLARE
    removed_insurances bigint;
    removed_patients bigint;
    removed_plans bigint;
    optional_table text;
    optional_source text;
    blocker_summary text;
BEGIN
    IF to_regclass('"_patient_store_migration"') IS NULL THEN
        RAISE EXCEPTION 'Nothing to roll back: "_patient_store_migration" does not exist.';
    END IF;

    INSERT INTO rollback_blockers (source, blocking_rows)
    SELECT 'Claims (PatientInsuranceId)', count(*)
    FROM "Claims" c JOIN "_patient_store_migration" m
      ON m.kind = 'insurance' AND m.inserted AND m.target_id = c."PatientInsuranceId"
    HAVING count(*) > 0;

    INSERT INTO rollback_blockers (source, blocking_rows)
    SELECT 'Claims (PatientId)', count(*)
    FROM "Claims" c JOIN "_patient_store_migration" m
      ON m.kind = 'patient' AND m.inserted AND m.target_id = c."PatientId"
    HAVING count(*) > 0;

    INSERT INTO rollback_blockers (source, blocking_rows)
    SELECT 'PatientAccounts', count(*)
    FROM "PatientAccounts" x JOIN "_patient_store_migration" m
      ON m.kind = 'patient' AND m.inserted AND m.target_id = x."PatientId"
    HAVING count(*) > 0;

    INSERT INTO rollback_blockers (source, blocking_rows)
    SELECT 'PatientPortalIdentities', count(*)
    FROM "PatientPortalIdentities" x JOIN "_patient_store_migration" m
      ON m.kind = 'patient' AND m.inserted AND m.target_id = x."PatientId"
    HAVING count(*) > 0;

    FOREACH optional_table IN ARRAY ARRAY['TreatmentPlans', 'Procedures', 'ClinicalNotes', 'ReviewOutreaches', 'Appointments'] LOOP
        IF to_regclass(format('"%s"', optional_table)) IS NOT NULL THEN
            optional_source := CASE optional_table
                WHEN 'Appointments' THEN 'Appointments (legacy Portal table)'
                ELSE optional_table
            END;
            EXECUTE format(
                'INSERT INTO rollback_blockers (source, blocking_rows)
                 SELECT %L, count(*)
                 FROM %I x JOIN "_patient_store_migration" m
                   ON m.kind = ''patient'' AND m.inserted AND m.target_id = x."PatientId"
                 HAVING count(*) > 0',
                optional_source, optional_table);
        END IF;
    END LOOP;

    INSERT INTO rollback_blockers (source, blocking_rows)
    SELECT "Source", count(*)
    FROM stage_scheduling_refs x JOIN "_patient_store_migration" m
      ON m.kind = 'patient' AND m.inserted AND m.target_id = x."PatientId"
    GROUP BY "Source";

    SELECT string_agg(format('%s=%s', source, blocking_rows), ', ' ORDER BY source)
    INTO blocker_summary
    FROM rollback_blockers;
    IF blocker_summary IS NOT NULL THEN
        RAISE EXCEPTION 'STOP: migrated rows are still referenced by %. Rolling back would orphan them; restore the pre-migration backup instead (README.md).',
            blocker_summary;
    END IF;

    DELETE FROM "PatientInsurances" i USING "_patient_store_migration" m
     WHERE m.kind = 'insurance' AND m.inserted AND m.target_id = i."PatientInsuranceId";
    GET DIAGNOSTICS removed_insurances = ROW_COUNT;

    DELETE FROM "Patients" p USING "_patient_store_migration" m
     WHERE m.kind = 'patient' AND m.inserted AND m.target_id = p."PatientId";
    GET DIAGNOSTICS removed_patients = ROW_COUNT;

    DELETE FROM "InsurancePlans" pl USING "_patient_store_migration" m
     WHERE m.kind = 'plan' AND m.inserted AND m.target_id = pl."InsurancePlanId"
       AND NOT EXISTS (SELECT 1 FROM "PatientInsurances" i WHERE i."InsurancePlanId" = pl."InsurancePlanId");
    GET DIAGNOSTICS removed_plans = ROW_COUNT;

    DROP TABLE "_patient_store_migration";
    RAISE NOTICE 'Rolled back % insurance rows, % patients, % inserted plans.', removed_insurances, removed_patients, removed_plans;
END
$rollback$;

COMMIT;

-- 4b · Rollback of 11-migrate-into-portal.sql. Use only inside the maintenance window,
-- before the Portal reopens. After staff have written against the migrated patients, restore
-- the pre-migration backup instead (see README.md).
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 13-rollback-portal.sql
-- One transaction. It deletes only rows recorded in "_patient_store_migration". If anything
-- created after the migration references them (claims on a migrated insurance, portal
-- logins on a migrated patient), the foreign keys stop the delete and nothing changes.
\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '10s';
LOCK TABLE "Patients", "PatientInsurances", "InsurancePlans" IN SHARE ROW EXCLUSIVE MODE;

DO $rollback$
DECLARE removed_insurances bigint; removed_patients bigint; removed_plans bigint;
BEGIN
    IF to_regclass('"_patient_store_migration"') IS NULL THEN
        RAISE EXCEPTION 'Nothing to roll back: "_patient_store_migration" does not exist.';
    END IF;

    -- Refuse (with a readable reason) when post-migration work references the migrated rows.
    IF EXISTS (SELECT 1 FROM "Claims" c JOIN "_patient_store_migration" m
                 ON m.kind = 'insurance' AND m.inserted AND m.target_id = c."PatientInsuranceId")
       OR EXISTS (SELECT 1 FROM "PatientPortalIdentities" x JOIN "_patient_store_migration" m
                 ON m.kind = 'patient' AND m.inserted AND m.target_id = x."PatientId") THEN
        RAISE EXCEPTION 'STOP: claims or patient-portal logins now reference migrated rows. Rolling back would orphan them; restore the pre-migration backup instead (README.md).';
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

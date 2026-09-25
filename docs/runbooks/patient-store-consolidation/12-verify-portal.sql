-- 4b · Read-only verification of the Portal DB after 11-migrate-into-portal.sql.
-- Run from the directory holding export-checksum.psql and scheduling-patient-refs.csv:
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 12-verify-portal.sql
-- Optionally pass a known patient to spot-check:  -v patient_id=123
\set ON_ERROR_STOP on
\pset footer off
\i export-checksum.psql
-- Session temp tables, created before the read-only transaction (which forbids CREATE).
CREATE TEMP TABLE stage_scheduling_refs (
    "Source" text NOT NULL,
    "TenantId" varchar(64) NOT NULL,
    "PatientId" integer NOT NULL
);
\copy stage_scheduling_refs FROM 'scheduling-patient-refs.csv' WITH (FORMAT csv, HEADER)

CREATE TEMP TABLE reference_verification (
    source text PRIMARY KEY,
    unresolved bigint NOT NULL,
    tenant_mismatches bigint NOT NULL
);

BEGIN TRANSACTION READ ONLY;

\echo '== 1. Counts: source (PatientService export) vs Portal rows created by the migration'
SELECT :'source_patient_count'::bigint AS source_patients,
       (SELECT count(*) FROM "Patients" p JOIN "_patient_store_migration" m ON m.kind = 'patient' AND m.target_id = p."PatientId") AS migrated_patients,
       :'source_insurance_count'::bigint AS source_insurances,
       (SELECT count(*) FROM "PatientInsurances" i JOIN "_patient_store_migration" m ON m.kind = 'insurance' AND m.target_id = i."PatientInsuranceId") AS migrated_insurances,
       :'source_plan_count'::bigint AS source_plans,
       (SELECT count(*) FROM "_patient_store_migration" WHERE kind = 'plan') AS mapped_plans;

\echo '== 2. Checksum over id | tenant | first | last | DOB (must say MATCH)'
SELECT CASE WHEN md5(coalesce(string_agg(concat_ws('|', p."PatientId", p."TenantId", lower(trim(p."FirstName")), lower(trim(p."LastName")),
            to_char(p."DateOfBirth" AT TIME ZONE 'UTC', 'YYYY-MM-DD')), E'\n' ORDER BY p."PatientId"), '')) = :'source_patient_checksum'
            THEN 'MATCH' ELSE 'MISMATCH' END AS patient_checksum
FROM "Patients" p JOIN "_patient_store_migration" m ON m.kind = 'patient' AND m.target_id = p."PatientId";

\echo '== 3. Identity sequences continue past the preserved ids (sequence_ok must be true)'
SELECT t.table_name, t.max_id, s.last_value AS sequence_last_value,
       (t.max_id IS NULL OR coalesce(s.last_value, 0) >= t.max_id) AS sequence_ok
FROM (SELECT 'Patients' AS table_name, pg_get_serial_sequence('"Patients"', 'PatientId') AS seq, (SELECT max("PatientId") FROM "Patients") AS max_id
      UNION ALL
      SELECT 'PatientInsurances', pg_get_serial_sequence('"PatientInsurances"', 'PatientInsuranceId'), (SELECT max("PatientInsuranceId") FROM "PatientInsurances")
      UNION ALL
      SELECT 'InsurancePlans', pg_get_serial_sequence('"InsurancePlans"', 'InsurancePlanId'), (SELECT max("InsurancePlanId") FROM "InsurancePlans")) t
LEFT JOIN pg_sequences s ON quote_ident(s.schemaname) || '.' || quote_ident(s.sequencename) = t.seq;

INSERT INTO reference_verification (source, unresolved, tenant_mismatches)
SELECT 'Claims' AS source,
       count(*) FILTER (WHERE p."PatientId" IS NULL) AS unresolved,
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL AND p."TenantId" IS DISTINCT FROM x."TenantId") AS tenant_mismatches
FROM "Claims" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId"
UNION ALL
SELECT "Source",
       count(*) FILTER (WHERE p."PatientId" IS NULL),
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL AND p."TenantId" IS DISTINCT FROM x."TenantId")
FROM stage_scheduling_refs x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId"
GROUP BY "Source";

DO $verify$
DECLARE
    optional_table text;
    optional_source text;
BEGIN
    -- Billing tables are absent from databases provisioned before billing existed.
    IF to_regclass('"PatientAccounts"') IS NOT NULL THEN
        FOREACH optional_table IN ARRAY ARRAY['PatientStatements', 'PatientBillingNotifications'] LOOP
            IF to_regclass(format('"%s"', optional_table)) IS NOT NULL THEN
                EXECUTE format(
                    'INSERT INTO reference_verification (source, unresolved, tenant_mismatches)
                     SELECT %L,
                            count(*) FILTER (WHERE p."PatientId" IS NULL),
                            count(*) FILTER (WHERE p."PatientId" IS NOT NULL AND p."TenantId" IS DISTINCT FROM a."TenantId")
                     FROM %I s JOIN "PatientAccounts" a ON a."TenantId" = s."TenantId" AND a."Id" = s."PatientAccountId"
                     LEFT JOIN "Patients" p ON p."PatientId" = a."PatientId"',
                    optional_table || ' (via account)', optional_table);
            END IF;
        END LOOP;
    END IF;

    FOREACH optional_table IN ARRAY ARRAY['PatientAccounts', 'PatientPortalIdentities', 'TreatmentPlans', 'Procedures', 'ClinicalNotes', 'ReviewOutreaches', 'Appointments'] LOOP
        IF to_regclass(format('"%s"', optional_table)) IS NOT NULL THEN
            optional_source := CASE optional_table
                WHEN 'Appointments' THEN 'Appointments (legacy Portal table)'
                ELSE optional_table
            END;
            EXECUTE format(
                'INSERT INTO reference_verification (source, unresolved, tenant_mismatches)
                 SELECT %L,
                        count(*) FILTER (WHERE p."PatientId" IS NULL),
                        count(*) FILTER (WHERE p."PatientId" IS NOT NULL AND p."TenantId" IS DISTINCT FROM x."TenantId")
                 FROM %I x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId"',
                optional_source, optional_table);
        END IF;
    END LOOP;
END
$verify$;

\echo '== 4. Patient references across Portal and Scheduling (expected 0 unresolved and 0 tenant_mismatches after migration)'
SELECT source, unresolved, tenant_mismatches
FROM reference_verification
ORDER BY source;

\if :{?patient_id}
\echo '== 5. Spot check for the given patient'
SELECT p."PatientId", p."TenantId", p."FirstName", p."LastName", p."DateOfBirth"::date AS dob, p."Status",
       (SELECT count(*) FROM "PatientInsurances" i WHERE i."PatientId" = p."PatientId") AS insurances,
       (SELECT count(*) FROM "Claims" c WHERE c."PatientId" = p."PatientId") AS claims,
       (SELECT count(*) FROM stage_scheduling_refs s WHERE s."Source" = 'Scheduling/Appointments' AND s."PatientId" = p."PatientId" AND s."TenantId" = p."TenantId") AS scheduling_appointments,
       (SELECT count(*) FROM stage_scheduling_refs s WHERE s."Source" = 'Scheduling/BookingRequests' AND s."PatientId" = p."PatientId" AND s."TenantId" = p."TenantId") AS scheduling_booking_requests
FROM "Patients" p WHERE p."PatientId" = :patient_id;
\endif

ROLLBACK;

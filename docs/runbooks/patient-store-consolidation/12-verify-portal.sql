-- 4b · Read-only verification of the Portal DB after 11-migrate-into-portal.sql.
-- Run from the directory holding export-checksum.psql:
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 12-verify-portal.sql
-- Optionally pass a known patient to spot-check:  -v patient_id=123
\set ON_ERROR_STOP on
\pset footer off
\i export-checksum.psql
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

\echo '== 4. Patient references in Portal tables that still have no Portal patient row (expected 0 after migration)'
SELECT 'Claims' AS source, count(*) AS unresolved FROM "Claims" x WHERE NOT EXISTS (SELECT 1 FROM "Patients" p WHERE p."PatientId" = x."PatientId")
UNION ALL
SELECT 'PatientAccounts', count(*) FROM "PatientAccounts" x WHERE NOT EXISTS (SELECT 1 FROM "Patients" p WHERE p."PatientId" = x."PatientId")
UNION ALL
SELECT 'PatientPortalIdentities', count(*) FROM "PatientPortalIdentities" x WHERE NOT EXISTS (SELECT 1 FROM "Patients" p WHERE p."PatientId" = x."PatientId");

\if :{?patient_id}
\echo '== 5. Spot check for the given patient'
SELECT p."PatientId", p."TenantId", p."FirstName", p."LastName", p."DateOfBirth"::date AS dob, p."Status",
       (SELECT count(*) FROM "PatientInsurances" i WHERE i."PatientId" = p."PatientId") AS insurances,
       (SELECT count(*) FROM "Claims" c WHERE c."PatientId" = p."PatientId") AS claims,
       (SELECT count(*) FROM "PatientAccounts" a WHERE a."PatientId" = p."PatientId") AS accounts
FROM "Patients" p WHERE p."PatientId" = :patient_id;
\endif

ROLLBACK;

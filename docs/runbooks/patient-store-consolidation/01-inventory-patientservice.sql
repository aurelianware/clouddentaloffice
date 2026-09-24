-- 4a · Read-only inventory of the PatientService database (cdo_patients).
-- Run:  psql "$PATIENTSERVICE_DB_URL" -v ON_ERROR_STOP=1 -f 01-inventory-patientservice.sql
-- Writes no database rows. Produces patientservice-ids.psql in the current
-- directory, which 02 and 03 load for the cross-database overlap checks.
\set ON_ERROR_STOP on
\pset footer off
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Schema check: PatientService patient tables and columns (confirms exact names)'
SELECT table_name, string_agg(column_name || ' ' || data_type, ', ' ORDER BY ordinal_position) AS columns
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name IN ('Patients', 'PatientInsurances', 'InsurancePlans')
GROUP BY table_name ORDER BY table_name;

\echo '== 2. Patients by tenant: count and PatientId range'
SELECT "TenantId", count(*) AS patients, min("PatientId") AS min_id, max("PatientId") AS max_id,
       count(*) FILTER (WHERE "Status" = 'Archived') AS archived
FROM "Patients" GROUP BY "TenantId" ORDER BY "TenantId";

\echo '== 3. Insurance rows and plans'
SELECT 'PatientInsurances' AS table_name, count(*) AS rows, min("PatientInsuranceId") AS min_id, max("PatientInsuranceId") AS max_id FROM "PatientInsurances"
UNION ALL
SELECT 'InsurancePlans', count(*), min("InsurancePlanId"), max("InsurancePlanId") FROM "InsurancePlans";

\echo '== 4. Patient checksum (id | tenant | first | last | DOB); 12-verify-portal.sql recomputes the same value'
SELECT count(*) AS patients,
       md5(coalesce(string_agg(concat_ws('|', "PatientId", "TenantId", lower(trim("FirstName")), lower(trim("LastName")),
           to_char("DateOfBirth" AT TIME ZONE 'UTC', 'YYYY-MM-DD')), E'\n' ORDER BY "PatientId"), '')) AS checksum
FROM "Patients";

-- Hand the PatientService ids to 02 and 03 as a psql variable (client-side file only).
\t on
\a
\o patientservice-ids.psql
SELECT format('\set patientservice_ids %s', quote_literal(coalesce(array_agg("PatientId" ORDER BY "PatientId"), '{}'::int[])::text))
FROM "Patients";
\o
\a
\t off
\echo 'Wrote patientservice-ids.psql'

ROLLBACK;

-- 4a · Read-only inventory of the SchedulingService database (appointment patient ids).
-- Run after 01, from the same directory:
--   psql "$SCHEDULING_DB_URL" -v ON_ERROR_STOP=1 -f 03-inventory-scheduling.sql
-- Writes no database rows; creates scheduling-patient-refs.csv locally for 12 and 13.
\set ON_ERROR_STOP on
\pset footer off
\i patientservice-ids.psql
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Appointments and booking requests: patient ids and whether PatientService knows them'
SELECT 'Appointments' AS source, "TenantId", count(*) AS rows, count(DISTINCT "PatientId") AS distinct_patients,
       count(*) FILTER (WHERE "PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice,
       count(*) FILTER (WHERE NOT ("PatientId" = ANY (:'patientservice_ids'::int[]))) AS not_in_patientservice
FROM "Appointments" GROUP BY "TenantId"
UNION ALL
SELECT 'BookingRequests (matched)', "TenantId", count(*), count(DISTINCT "MatchedPatientId"),
       count(*) FILTER (WHERE "MatchedPatientId" = ANY (:'patientservice_ids'::int[])),
       count(*) FILTER (WHERE NOT ("MatchedPatientId" = ANY (:'patientservice_ids'::int[])))
FROM "BookingRequests" WHERE "MatchedPatientId" IS NOT NULL GROUP BY "TenantId"
ORDER BY 1, 2;

\echo '== 2. Writing scheduling-patient-refs.csv for 12-verify-portal.sql and 13-rollback-portal.sql'
\copy (
    SELECT source, "TenantId", "PatientId"
    FROM (
        SELECT 'Scheduling/Appointments' AS source, "TenantId", "PatientId" FROM "Appointments"
        UNION ALL
        SELECT 'Scheduling/BookingRequests', "TenantId", "MatchedPatientId" AS "PatientId"
        FROM "BookingRequests" WHERE "MatchedPatientId" IS NOT NULL
    ) refs
    ORDER BY source, "TenantId", "PatientId"
) TO 'scheduling-patient-refs.csv' WITH (FORMAT csv, HEADER)

ROLLBACK;

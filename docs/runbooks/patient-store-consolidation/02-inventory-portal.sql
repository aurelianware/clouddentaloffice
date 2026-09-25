-- 4a · Read-only inventory of the Portal database.
-- Run after 01, from the same directory:
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 02-inventory-portal.sql
-- Writes no database rows.
\set ON_ERROR_STOP on
\pset footer off
\i patientservice-ids.psql
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Schema check: Portal tables that hold patient data or reference PatientId (confirms exact names)'
SELECT table_name, string_agg(column_name || ' ' || data_type, ', ' ORDER BY ordinal_position) AS columns
FROM information_schema.columns
WHERE table_schema = 'public' AND table_name IN ('Patients', 'PatientInsurances', 'InsurancePlans', 'Claims',
      'PatientAccounts', 'PatientStatements', 'PatientBillingNotifications', 'PatientPortalIdentities')
GROUP BY table_name ORDER BY table_name;

\echo '== 2. Live foreign keys that point at Patients or PatientInsurances'
SELECT conrelid::regclass AS from_table, conname AS constraint_name, confrelid::regclass AS to_table,
       pg_get_constraintdef(oid) AS definition
FROM pg_constraint
WHERE contype = 'f' AND confrelid IN (to_regclass('"Patients"'), to_regclass('"PatientInsurances"'))
ORDER BY 1, 2;

\echo '== 3. Portal patients by tenant: count and PatientId range'
SELECT "TenantId", count(*) AS patients, min("PatientId") AS min_id, max("PatientId") AS max_id
FROM "Patients" GROUP BY "TenantId" ORDER BY "TenantId";

\echo '== 4. PatientId overlap between the Portal DB and PatientService'
SELECT count(*) AS overlapping_ids, (array_agg("PatientId" ORDER BY "PatientId"))[1:50] AS first_50_overlapping_ids
FROM "Patients" WHERE "PatientId" = ANY (:'patientservice_ids'::int[]);
SELECT count(*) AS portal_only_ids FROM "Patients" WHERE NOT ("PatientId" = ANY (:'patientservice_ids'::int[]));

\echo '== 5. Insurance rows and plans in the Portal DB'
SELECT 'PatientInsurances' AS table_name, count(*) AS rows, min("PatientInsuranceId") AS min_id, max("PatientInsuranceId") AS max_id FROM "PatientInsurances"
UNION ALL
SELECT 'InsurancePlans', count(*), min("InsurancePlanId"), max("InsurancePlanId") FROM "InsurancePlans";

\echo '== 6. Claims by tenant and status'
SELECT "TenantId", "Status", count(*) AS claims, count("PatientInsuranceId") AS with_insurance
FROM "Claims" GROUP BY "TenantId", "Status" ORDER BY "TenantId", "Status";

\echo '== 7. Which store each patient reference resolves to'
\echo '   in_portal = a Portal patient row exists; in_patientservice = the id is a PatientService patient'
SELECT 'Claims' AS source, count(*) AS rows,
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL) AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice,
       count(*) FILTER (WHERE p."PatientId" IS NULL AND NOT (x."PatientId" = ANY (:'patientservice_ids'::int[]))) AS in_neither
FROM "Claims" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId"
UNION ALL
SELECT 'PatientAccounts', count(*),
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL),
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])),
       count(*) FILTER (WHERE p."PatientId" IS NULL AND NOT (x."PatientId" = ANY (:'patientservice_ids'::int[])))
FROM "PatientAccounts" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId"
UNION ALL
SELECT 'PatientStatements (via account)', count(*),
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL),
       count(*) FILTER (WHERE a."PatientId" = ANY (:'patientservice_ids'::int[])),
       count(*) FILTER (WHERE p."PatientId" IS NULL AND NOT (a."PatientId" = ANY (:'patientservice_ids'::int[])))
FROM "PatientStatements" s JOIN "PatientAccounts" a ON a."TenantId" = s."TenantId" AND a."Id" = s."PatientAccountId"
LEFT JOIN "Patients" p ON p."PatientId" = a."PatientId"
UNION ALL
SELECT 'PatientBillingNotifications (via account)', count(*),
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL),
       count(*) FILTER (WHERE a."PatientId" = ANY (:'patientservice_ids'::int[])),
       count(*) FILTER (WHERE p."PatientId" IS NULL AND NOT (a."PatientId" = ANY (:'patientservice_ids'::int[])))
FROM "PatientBillingNotifications" n JOIN "PatientAccounts" a ON a."TenantId" = n."TenantId" AND a."Id" = n."PatientAccountId"
LEFT JOIN "Patients" p ON p."PatientId" = a."PatientId"
UNION ALL
SELECT 'PatientPortalIdentities', count(*),
       count(*) FILTER (WHERE p."PatientId" IS NOT NULL),
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])),
       count(*) FILTER (WHERE p."PatientId" IS NULL AND NOT (x."PatientId" = ANY (:'patientservice_ids'::int[])))
FROM "PatientPortalIdentities" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";

\echo '== 8. Other Portal tables keyed by PatientId (only those present)'
SELECT to_regclass('"TreatmentPlans"') IS NOT NULL AS has_treatment_plans,
       to_regclass('"Procedures"') IS NOT NULL AS has_procedures,
       to_regclass('"ClinicalNotes"') IS NOT NULL AS has_clinical_notes,
       to_regclass('"ReviewOutreaches"') IS NOT NULL AS has_review_outreaches,
       to_regclass('"Appointments"') IS NOT NULL AS has_appointments \gset
\if :has_treatment_plans
SELECT 'TreatmentPlans' AS source, count(*) AS rows, count(p."PatientId") AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice
FROM "TreatmentPlans" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";
\endif
\if :has_procedures
SELECT 'Procedures' AS source, count(*) AS rows, count(p."PatientId") AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice
FROM "Procedures" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";
\endif
\if :has_clinical_notes
SELECT 'ClinicalNotes' AS source, count(*) AS rows, count(p."PatientId") AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice
FROM "ClinicalNotes" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";
\endif
\if :has_review_outreaches
SELECT 'ReviewOutreaches' AS source, count(*) AS rows, count(p."PatientId") AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice
FROM "ReviewOutreaches" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";
\endif
\if :has_appointments
SELECT 'Appointments (legacy Portal table)' AS source, count(*) AS rows, count(p."PatientId") AS in_portal,
       count(*) FILTER (WHERE x."PatientId" = ANY (:'patientservice_ids'::int[])) AS in_patientservice
FROM "Appointments" x LEFT JOIN "Patients" p ON p."PatientId" = x."PatientId";
\endif

ROLLBACK;

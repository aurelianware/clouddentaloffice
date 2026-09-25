-- Read-only pre-check before resetting the patient data. Run against cdo_portal:
--   psql -d cdo_portal -v ON_ERROR_STOP=1 -f 20-precheck.sql
-- Confirms cdo_portal holds nothing that a fresh database would not recreate: the Portal
-- re-seeds the 4 sample providers (tenant 'demo') and the 38 procedure codes, and rebuilds
-- the tenant, organization and review-outreach settings from its deploy settings.
-- Providers added by staff are not recreated; section 1 lists them for re-entry.
\set ON_ERROR_STOP on
\pset footer off
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Providers. Ids 1-4 in tenant demo are the seeded samples and come back on their own.'
\echo '      Any other row was added by staff: keep this output and re-enter it on the Providers page afterwards.'
-- SELECT * because an older production schema may lack some of the current columns.
\x on
SELECT * FROM "Providers" ORDER BY "ProviderId";
\x off

\echo '== 2. Procedure codes (expected: 38 rows, ids 1-38, none added by staff)'
SELECT count(*) AS procedure_codes, max("ProcedureCodeId") AS max_id,
       count(*) FILTER (WHERE "ProcedureCodeId" > 38) AS added_by_staff
FROM "ProcedureCodes";

\echo '== 3. Every table with rows (expected: only the tables above plus Tenants, Organizations,'
\echo '      ReviewOutreachSettings, Claims and ClaimProcedures)'
SELECT table_name,
       (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text::int AS row_count
FROM information_schema.tables
WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
ORDER BY 1;

ROLLBACK;

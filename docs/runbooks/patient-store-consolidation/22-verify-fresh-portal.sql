-- Read-only check that the new Portal build created a complete cdo_portal on first start.
--   psql -d cdo_portal -v ON_ERROR_STOP=1 -f 22-verify-fresh-portal.sql
\set ON_ERROR_STOP on
\pset footer off
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Tables the old database was missing (every present must be t)'
SELECT t AS table_name, to_regclass(format('"%s"', t)) IS NOT NULL AS present
FROM unnest(ARRAY['PatientAccounts', 'PatientLedgerEntries', 'PatientStatements', 'PatientStatementLines',
                  'PatientPayments', 'PatientPaymentAttempts', 'PatientBillingNotifications',
                  'PatientPortalIdentities']) t;

\echo '== 2. Seed and bootstrap rows (expected: tenants 1, organizations 1, providers 4 (the seeded samples), procedure_codes 38, patients 0, claims 0)'
SELECT (SELECT count(*) FROM "Tenants") AS tenants,
       (SELECT count(*) FROM "Organizations") AS organizations,
       (SELECT count(*) FROM "Providers") AS providers,
       (SELECT count(*) FROM "ProcedureCodes") AS procedure_codes,
       (SELECT count(*) FROM "Patients") AS patients,
       (SELECT count(*) FROM "Claims") AS claims;

\echo '== 3. Tenant created from the deploy settings'
SELECT "TenantId", "Name", "IsActive" FROM "Tenants";

ROLLBACK;

-- Read-only check that the new Portal build created a complete cdo_portal on first start.
--   psql -d cdo_portal -v ON_ERROR_STOP=1 -v tenant_id=third-set-smiles -f 22-verify-fresh-portal.sql
-- Every section prints one row per check; the last column must be t (true) in every row.
\set ON_ERROR_STOP on
\pset footer off
\if :{?tenant_id}
\else
\echo 'Pass the practice tenant: -v tenant_id=third-set-smiles'
\quit
\endif
BEGIN TRANSACTION READ ONLY;

\echo '== 1. Every table in the current Portal model exists (the old database lacked the billing tables)'
SELECT t AS table_name, to_regclass(format('"%s"', t)) IS NOT NULL AS present
FROM unnest(ARRAY[
    'Tenants', 'Organizations', 'Users', 'Patients', 'PatientInsurances', 'InsurancePlans', 'Providers',
    'Appointments', 'ReviewOutreaches', 'ReviewOutreachSettings', 'TreatmentPlans', 'PlannedProcedures',
    'Claims', 'ClaimProcedures', 'ProcedureCodes', 'Procedures', 'ClinicalNotes',
    'PatientAccounts', 'PatientLedgerEntries', 'PatientStatements', 'PatientStatementLines',
    'PatientPayments', 'PatientPaymentAllocations', 'PatientRefunds', 'PaymentReconciliationIssues',
    'FinancialAuditEvents', 'PaymentProcessorConfigurations', 'PaymentProcessorEvents',
    'PatientPaymentAttempts', 'PatientPortalIdentities', 'PatientBillingNotifications']) t
ORDER BY present, t;

\echo '== 2. Practice tenant, organization and review settings were created from the deploy settings'
SELECT 'tenant' AS item, EXISTS (SELECT 1 FROM "Tenants" WHERE "TenantId" = :'tenant_id' AND "IsActive") AS ok
UNION ALL
SELECT 'organization', EXISTS (SELECT 1 FROM "Organizations" WHERE "TenantId" = :'tenant_id')
UNION ALL
SELECT 'review outreach settings', EXISTS (SELECT 1 FROM "ReviewOutreachSettings" WHERE "TenantId" = :'tenant_id');

\echo '== 3. Seed data is exactly the model seed: providers 1-4 in the sample tenant demo, and the 38 CDT codes'
\echo '      (the sample providers are not visible to the practice; staff-added providers are re-entered in step 7)'
SELECT 'providers are the 4 samples' AS item,
       (SELECT array_agg("ProviderId" ORDER BY "ProviderId") FROM "Providers") = ARRAY[1, 2, 3, 4]
       AND NOT EXISTS (SELECT 1 FROM "Providers" WHERE "TenantId" <> 'demo') AS ok
UNION ALL
SELECT 'procedure codes are the 38 seeded codes',
       (SELECT array_agg("Code" ORDER BY "ProcedureCodeId") FROM "ProcedureCodes") = ARRAY[
           'D0120', 'D0140', 'D0150', 'D0210', 'D0220', 'D0230', 'D0270', 'D0274', 'D0330', 'D1110',
           'D1120', 'D1206', 'D1208', 'D1351', 'D2140', 'D2150', 'D2160', 'D2330', 'D2331', 'D2332',
           'D2391', 'D2392', 'D2393', 'D3310', 'D3320', 'D3330', 'D4341', 'D4342', 'D5110', 'D5120',
           'D5213', 'D5214', 'D6240', 'D6750', 'D6010', 'D7140', 'D7210', 'D7240']::varchar[]
       AND (SELECT array_agg("ProcedureCodeId" ORDER BY "ProcedureCodeId") FROM "ProcedureCodes") =
           (SELECT array_agg(i) FROM generate_series(1, 38) i);

\echo '== 4. No patient data carried over'
SELECT 'no patients' AS item, NOT EXISTS (SELECT 1 FROM "Patients") AS ok
UNION ALL
SELECT 'no claims', NOT EXISTS (SELECT 1 FROM "Claims");

ROLLBACK;

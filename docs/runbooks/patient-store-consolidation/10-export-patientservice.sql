-- 4b · Read-only export of PatientService patients, insurance and plans.
-- Run during the maintenance window, after writes are paused and both DBs are backed up:
--   psql "$PATIENTSERVICE_DB_URL" -v ON_ERROR_STOP=1 -f 10-export-patientservice.sql
-- Writes export-*.csv and export-checksum.psql to the current directory. No database rows change.
\set ON_ERROR_STOP on
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;

\copy (SELECT "PatientId", "TenantId", "FirstName", "LastName", "MiddleName", "PreferredName", "DateOfBirth", "Gender", "SSN", "Email", "PrimaryPhone", "SecondaryPhone", "Address1", "Address2", "City", "State", "ZipCode", "Status", "CreatedDate", "ModifiedDate", "CreatedBy", "ModifiedBy" FROM "Patients" ORDER BY "PatientId") TO 'export-patients.csv' WITH (FORMAT csv, HEADER)
\copy (SELECT "InsurancePlanId", "TenantId", "PayerId", "PayerName", "PlanName", "PlanType", "Phone", "Address1", "Address2", "City", "State", "ZipCode", "EdiPayerId", "EdiEnabled", "EdiSubmissionType", "IsActive", "CreatedDate", "ModifiedDate" FROM "InsurancePlans" ORDER BY "InsurancePlanId") TO 'export-insurance-plans.csv' WITH (FORMAT csv, HEADER)
\copy (SELECT "PatientInsuranceId", "TenantId", "PatientId", "InsurancePlanId", "MemberId", "GroupNumber", "SequenceNumber", "EffectiveDate", "TerminationDate", "IsActive", "RelationshipToSubscriber", "SubscriberFirstName", "SubscriberLastName", "SubscriberDateOfBirth", "CreatedDate", "ModifiedDate" FROM "PatientInsurances" ORDER BY "PatientInsuranceId") TO 'export-patient-insurances.csv' WITH (FORMAT csv, HEADER)

-- Source counts and checksum, handed to 11 and 12 as psql variables.
\t on
\a
\o export-checksum.psql
SELECT format(E'\\set source_patient_count %s\n\\set source_insurance_count %s\n\\set source_plan_count %s\n\\set source_patient_checksum %s',
    (SELECT count(*) FROM "Patients"), (SELECT count(*) FROM "PatientInsurances"), (SELECT count(*) FROM "InsurancePlans"),
    (SELECT md5(coalesce(string_agg(concat_ws('|', "PatientId", "TenantId", lower(trim("FirstName")), lower(trim("LastName")),
        to_char("DateOfBirth" AT TIME ZONE 'UTC', 'YYYY-MM-DD')), E'\n' ORDER BY "PatientId"), '')) FROM "Patients"));
\o
\a
\t off
\echo 'Exported. Source counts and checksum:'
\i export-checksum.psql
\echo patients :source_patient_count insurances :source_insurance_count plans :source_plan_count checksum :source_patient_checksum

ROLLBACK;

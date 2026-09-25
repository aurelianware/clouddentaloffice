-- 4b · Copy PatientService patients and insurance into the Portal DB, preserving PatientId
-- and PatientInsuranceId. One transaction: any guard failure rolls everything back.
-- Run from the directory holding the 10-export outputs:
--   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -f 11-migrate-into-portal.sql
--
-- Guards (the whole script aborts, writing nothing):
--   * Portal "Patients" or "PatientInsurances" hold rows this migration did not create (4c: stop and report).
--   * Existing tenant-scoped Portal references disagree with the staged patient or insurance tenant.
--   * The exported files don't match the source counts/checksum from 10-export.
--   * Exported insurance rows reference patients or plans missing from the export.
-- Idempotent: rows already copied by an earlier run are recognised through
-- "_patient_store_migration" and skipped; a re-run inserts nothing new.
-- Insurance plans are matched to an existing Portal plan by tenant, payer, plan name and
-- plan type; unmatched plans are inserted with new ids. The mapping is recorded.
\set ON_ERROR_STOP on
\i export-checksum.psql
BEGIN;
SET LOCAL lock_timeout = '10s';
SELECT set_config('cdo.source_patient_count', :'source_patient_count', true),
       set_config('cdo.source_insurance_count', :'source_insurance_count', true),
       set_config('cdo.source_plan_count', :'source_plan_count', true),
       set_config('cdo.source_patient_checksum', :'source_patient_checksum', true);

LOCK TABLE "Patients", "PatientInsurances", "InsurancePlans" IN SHARE ROW EXCLUSIVE MODE;

CREATE TABLE IF NOT EXISTS "_patient_store_migration" (
    kind text NOT NULL CHECK (kind IN ('patient', 'insurance', 'plan')),
    source_id integer NOT NULL,
    target_id integer NOT NULL,
    inserted boolean NOT NULL,
    migrated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (kind, source_id));

CREATE TEMP TABLE stage_patients (
    "PatientId" integer PRIMARY KEY, "TenantId" varchar(64) NOT NULL, "FirstName" varchar(100) NOT NULL,
    "LastName" varchar(100) NOT NULL, "MiddleName" varchar(50), "PreferredName" varchar(20),
    "DateOfBirth" timestamptz NOT NULL, "Gender" varchar(1) NOT NULL, "SSN" varchar(11), "Email" varchar(255),
    "PrimaryPhone" varchar(20), "SecondaryPhone" varchar(20), "Address1" varchar(255), "Address2" varchar(255),
    "City" varchar(100), "State" varchar(2), "ZipCode" varchar(10), "Status" varchar(20) NOT NULL,
    "CreatedDate" timestamptz NOT NULL, "ModifiedDate" timestamptz, "CreatedBy" varchar(100), "ModifiedBy" varchar(100)
) ON COMMIT DROP;
CREATE TEMP TABLE stage_plans (
    "InsurancePlanId" integer PRIMARY KEY, "TenantId" varchar(64) NOT NULL, "PayerId" varchar(10) NOT NULL,
    "PayerName" varchar(255) NOT NULL, "PlanName" varchar(255), "PlanType" varchar(50), "Phone" varchar(20),
    "Address1" varchar(255), "Address2" varchar(255), "City" varchar(100), "State" varchar(2), "ZipCode" varchar(10),
    "EdiPayerId" varchar(50), "EdiEnabled" boolean NOT NULL, "EdiSubmissionType" varchar(20), "IsActive" boolean NOT NULL,
    "CreatedDate" timestamptz NOT NULL, "ModifiedDate" timestamptz
) ON COMMIT DROP;
CREATE TEMP TABLE stage_insurances (
    "PatientInsuranceId" integer PRIMARY KEY, "TenantId" varchar(64) NOT NULL, "PatientId" integer NOT NULL,
    "InsurancePlanId" integer NOT NULL, "MemberId" varchar(50) NOT NULL, "GroupNumber" varchar(50),
    "SequenceNumber" integer NOT NULL, "EffectiveDate" timestamptz NOT NULL, "TerminationDate" timestamptz,
    "IsActive" boolean NOT NULL, "RelationshipToSubscriber" varchar(20), "SubscriberFirstName" varchar(100),
    "SubscriberLastName" varchar(100), "SubscriberDateOfBirth" timestamptz, "CreatedDate" timestamptz NOT NULL,
    "ModifiedDate" timestamptz
) ON COMMIT DROP;

\copy stage_patients FROM 'export-patients.csv' WITH (FORMAT csv, HEADER)
\copy stage_plans FROM 'export-insurance-plans.csv' WITH (FORMAT csv, HEADER)
\copy stage_insurances FROM 'export-patient-insurances.csv' WITH (FORMAT csv, HEADER)

DO $migrate$
DECLARE
    foreign_patients bigint;
    foreign_insurances bigint;
    orphan_insurances bigint;
    staged_checksum text;
    migrated_checksum text;
    plan record;
    new_plan_id integer;
    inserted_patients bigint;
    inserted_insurances bigint;
    patient_tenant_conflicts bigint := 0;
    insurance_tenant_conflicts bigint := 0;
    source_conflicts bigint;
    patient_conflict_details text := '';
    insurance_conflict_details text := '';
    optional_table text;
    optional_source text;
BEGIN
    -- File integrity: the staged export must match what 10-export counted at the source.
    SELECT md5(coalesce(string_agg(concat_ws('|', "PatientId", "TenantId", lower(trim("FirstName")), lower(trim("LastName")),
               to_char("DateOfBirth" AT TIME ZONE 'UTC', 'YYYY-MM-DD')), E'\n' ORDER BY "PatientId"), ''))
      INTO staged_checksum FROM stage_patients;
    IF (SELECT count(*) FROM stage_patients) <> current_setting('cdo.source_patient_count')::bigint
       OR (SELECT count(*) FROM stage_insurances) <> current_setting('cdo.source_insurance_count')::bigint
       OR (SELECT count(*) FROM stage_plans) <> current_setting('cdo.source_plan_count')::bigint
       OR staged_checksum <> current_setting('cdo.source_patient_checksum') THEN
        RAISE EXCEPTION 'STOP: exported files do not match the source counts/checksum from 10-export. Re-export.';
    END IF;

    -- 4c: the Portal DB must hold no patient data except what this migration created.
    SELECT count(*) INTO foreign_patients FROM "Patients" p
     WHERE NOT EXISTS (SELECT 1 FROM "_patient_store_migration" m WHERE m.kind = 'patient' AND m.target_id = p."PatientId");
    SELECT count(*) INTO foreign_insurances FROM "PatientInsurances" i
     WHERE NOT EXISTS (SELECT 1 FROM "_patient_store_migration" m WHERE m.kind = 'insurance' AND m.target_id = i."PatientInsuranceId");
    IF foreign_patients > 0 OR foreign_insurances > 0 THEN
        RAISE EXCEPTION 'STOP (4c): the Portal DB already has % patient row(s) and % insurance row(s) not created by this migration. Report the overlap before continuing.',
            foreign_patients, foreign_insurances;
    END IF;

    SELECT count(*) INTO source_conflicts
    FROM "Claims" x JOIN stage_patients s ON s."PatientId" = x."PatientId"
    WHERE x."TenantId" IS DISTINCT FROM s."TenantId";
    patient_tenant_conflicts := patient_tenant_conflicts + source_conflicts;
    IF source_conflicts > 0 THEN
        patient_conflict_details := concat_ws('; ', patient_conflict_details, format('Claims=%s', source_conflicts));
    END IF;

    SELECT count(*) INTO source_conflicts
    FROM "PatientAccounts" x JOIN stage_patients s ON s."PatientId" = x."PatientId"
    WHERE x."TenantId" IS DISTINCT FROM s."TenantId";
    patient_tenant_conflicts := patient_tenant_conflicts + source_conflicts;
    IF source_conflicts > 0 THEN
        patient_conflict_details := concat_ws('; ', patient_conflict_details, format('PatientAccounts=%s', source_conflicts));
    END IF;

    SELECT count(*) INTO source_conflicts
    FROM "PatientPortalIdentities" x JOIN stage_patients s ON s."PatientId" = x."PatientId"
    WHERE x."TenantId" IS DISTINCT FROM s."TenantId";
    patient_tenant_conflicts := patient_tenant_conflicts + source_conflicts;
    IF source_conflicts > 0 THEN
        patient_conflict_details := concat_ws('; ', patient_conflict_details, format('PatientPortalIdentities=%s', source_conflicts));
    END IF;

    FOREACH optional_table IN ARRAY ARRAY['TreatmentPlans', 'Procedures', 'ClinicalNotes', 'ReviewOutreaches', 'Appointments'] LOOP
        IF to_regclass(format('"%s"', optional_table)) IS NOT NULL THEN
            EXECUTE format(
                'SELECT count(*) FROM %I x JOIN stage_patients s ON s."PatientId" = x."PatientId" WHERE x."TenantId" IS DISTINCT FROM s."TenantId"',
                optional_table)
            INTO source_conflicts;
            patient_tenant_conflicts := patient_tenant_conflicts + source_conflicts;
            IF source_conflicts > 0 THEN
                optional_source := CASE optional_table
                    WHEN 'Appointments' THEN 'Appointments (legacy Portal table)'
                    ELSE optional_table
                END;
                patient_conflict_details := concat_ws('; ', patient_conflict_details, format('%s=%s', optional_source, source_conflicts));
            END IF;
        END IF;
    END LOOP;

    SELECT count(*) INTO source_conflicts
    FROM "Claims" x JOIN stage_insurances s ON s."PatientInsuranceId" = x."PatientInsuranceId"
    WHERE x."TenantId" IS DISTINCT FROM s."TenantId";
    insurance_tenant_conflicts := insurance_tenant_conflicts + source_conflicts;
    IF source_conflicts > 0 THEN
        insurance_conflict_details := concat_ws('; ', insurance_conflict_details, format('Claims=%s', source_conflicts));
    END IF;

    IF patient_tenant_conflicts > 0 OR insurance_tenant_conflicts > 0 THEN
        RAISE EXCEPTION 'STOP (4c): existing tenant-scoped references disagree with the staged data. PatientId tenant conflicts: %. PatientInsuranceId tenant conflicts: %. Report the overlap before continuing.',
            coalesce(nullif(patient_conflict_details, ''), 'none'),
            coalesce(nullif(insurance_conflict_details, ''), 'none');
    END IF;

    -- Referential integrity inside the export.
    SELECT count(*) INTO orphan_insurances FROM stage_insurances i
     WHERE NOT EXISTS (SELECT 1 FROM stage_patients p WHERE p."PatientId" = i."PatientId")
        OR NOT EXISTS (SELECT 1 FROM stage_plans pl WHERE pl."InsurancePlanId" = i."InsurancePlanId");
    IF orphan_insurances > 0 THEN
        RAISE EXCEPTION 'STOP: % exported insurance row(s) reference a patient or plan missing from the export.', orphan_insurances;
    END IF;

    -- Plans: reuse a matching Portal plan, otherwise insert one; record the mapping once.
    FOR plan IN SELECT s.* FROM stage_plans s
                 WHERE NOT EXISTS (SELECT 1 FROM "_patient_store_migration" m WHERE m.kind = 'plan' AND m.source_id = s."InsurancePlanId")
                 ORDER BY s."InsurancePlanId" LOOP
        SELECT min(p."InsurancePlanId") INTO new_plan_id FROM "InsurancePlans" p
         WHERE p."TenantId" = plan."TenantId" AND p."PayerId" = plan."PayerId"
           AND p."PlanName" IS NOT DISTINCT FROM plan."PlanName" AND p."PlanType" IS NOT DISTINCT FROM plan."PlanType";
        IF new_plan_id IS NOT NULL THEN
            INSERT INTO "_patient_store_migration" (kind, source_id, target_id, inserted) VALUES ('plan', plan."InsurancePlanId", new_plan_id, false);
        ELSE
            INSERT INTO "InsurancePlans" ("TenantId", "PayerId", "PayerName", "PlanName", "PlanType", "Phone", "Address1", "Address2",
                "City", "State", "ZipCode", "EdiPayerId", "EdiEnabled", "EdiSubmissionType", "SftpUseSshKey", "IsActive", "CreatedDate", "ModifiedDate")
            VALUES (plan."TenantId", plan."PayerId", plan."PayerName", plan."PlanName", plan."PlanType", plan."Phone", plan."Address1", plan."Address2",
                plan."City", plan."State", plan."ZipCode", plan."EdiPayerId", plan."EdiEnabled", plan."EdiSubmissionType", false, plan."IsActive",
                plan."CreatedDate", plan."ModifiedDate")
            RETURNING "InsurancePlanId" INTO new_plan_id;
            INSERT INTO "_patient_store_migration" (kind, source_id, target_id, inserted) VALUES ('plan', plan."InsurancePlanId", new_plan_id, true);
        END IF;
    END LOOP;

    -- Patients, preserving PatientId.
    INSERT INTO "Patients" ("PatientId", "TenantId", "FirstName", "LastName", "MiddleName", "PreferredName", "DateOfBirth", "Gender", "SSN",
        "Email", "PrimaryPhone", "SecondaryPhone", "Address1", "Address2", "City", "State", "ZipCode", "Status", "CreatedDate",
        "ModifiedDate", "CreatedBy", "ModifiedBy")
    SELECT s."PatientId", s."TenantId", s."FirstName", s."LastName", s."MiddleName", s."PreferredName", s."DateOfBirth", s."Gender", s."SSN",
        s."Email", s."PrimaryPhone", s."SecondaryPhone", s."Address1", s."Address2", s."City", s."State", s."ZipCode", s."Status", s."CreatedDate",
        s."ModifiedDate", s."CreatedBy", s."ModifiedBy"
    FROM stage_patients s
    WHERE NOT EXISTS (SELECT 1 FROM "Patients" p WHERE p."PatientId" = s."PatientId");
    GET DIAGNOSTICS inserted_patients = ROW_COUNT;
    INSERT INTO "_patient_store_migration" (kind, source_id, target_id, inserted)
    SELECT 'patient', "PatientId", "PatientId", true FROM stage_patients ON CONFLICT DO NOTHING;

    -- Patient insurance, preserving PatientInsuranceId and remapping plans.
    INSERT INTO "PatientInsurances" ("PatientInsuranceId", "TenantId", "PatientId", "InsurancePlanId", "MemberId", "GroupNumber",
        "SequenceNumber", "EffectiveDate", "TerminationDate", "IsActive", "RelationshipToSubscriber", "SubscriberFirstName",
        "SubscriberLastName", "SubscriberDateOfBirth", "CreatedDate", "ModifiedDate")
    SELECT s."PatientInsuranceId", s."TenantId", s."PatientId", m.target_id, s."MemberId", s."GroupNumber",
        s."SequenceNumber", s."EffectiveDate", s."TerminationDate", s."IsActive", s."RelationshipToSubscriber", s."SubscriberFirstName",
        s."SubscriberLastName", s."SubscriberDateOfBirth", s."CreatedDate", s."ModifiedDate"
    FROM stage_insurances s
    JOIN "_patient_store_migration" m ON m.kind = 'plan' AND m.source_id = s."InsurancePlanId"
    WHERE NOT EXISTS (SELECT 1 FROM "PatientInsurances" i WHERE i."PatientInsuranceId" = s."PatientInsuranceId");
    GET DIAGNOSTICS inserted_insurances = ROW_COUNT;
    INSERT INTO "_patient_store_migration" (kind, source_id, target_id, inserted)
    SELECT 'insurance', "PatientInsuranceId", "PatientInsuranceId", true FROM stage_insurances ON CONFLICT DO NOTHING;

    -- New Portal rows must continue after the preserved ids.
    -- setval(..., max, true): the next generated id is max + 1.
    PERFORM setval(pg_get_serial_sequence('"Patients"', 'PatientId'), max("PatientId"), true)
       FROM "Patients" HAVING max("PatientId") IS NOT NULL;
    PERFORM setval(pg_get_serial_sequence('"PatientInsurances"', 'PatientInsuranceId'), max("PatientInsuranceId"), true)
       FROM "PatientInsurances" HAVING max("PatientInsuranceId") IS NOT NULL;
    PERFORM setval(pg_get_serial_sequence('"InsurancePlans"', 'InsurancePlanId'), max("InsurancePlanId"), true)
       FROM "InsurancePlans" HAVING max("InsurancePlanId") IS NOT NULL;

    -- In-transaction verification: every exported patient is present with identical id/tenant/name/DOB.
    SELECT md5(coalesce(string_agg(concat_ws('|', p."PatientId", p."TenantId", lower(trim(p."FirstName")), lower(trim(p."LastName")),
               to_char(p."DateOfBirth" AT TIME ZONE 'UTC', 'YYYY-MM-DD')), E'\n' ORDER BY p."PatientId"), ''))
      INTO migrated_checksum
      FROM "Patients" p WHERE EXISTS (SELECT 1 FROM stage_patients s WHERE s."PatientId" = p."PatientId");
    IF migrated_checksum <> staged_checksum
       OR (SELECT count(*) FROM "PatientInsurances" i WHERE EXISTS (SELECT 1 FROM stage_insurances s WHERE s."PatientInsuranceId" = i."PatientInsuranceId"))
          <> (SELECT count(*) FROM stage_insurances) THEN
        RAISE EXCEPTION 'STOP: post-copy verification failed (checksum or insurance count); nothing was committed.';
    END IF;

    RAISE NOTICE 'This run inserted % patients and % insurance rows. Now present: % of % patients, % of % insurance rows, % of % plans mapped (% inserted as new). Checksum %.',
        inserted_patients, inserted_insurances,
        (SELECT count(*) FROM stage_patients), current_setting('cdo.source_patient_count'),
        (SELECT count(*) FROM stage_insurances), current_setting('cdo.source_insurance_count'),
        (SELECT count(*) FROM "_patient_store_migration" WHERE kind = 'plan'), current_setting('cdo.source_plan_count'),
        (SELECT count(*) FROM "_patient_store_migration" WHERE kind = 'plan' AND inserted), migrated_checksum;
END
$migrate$;

COMMIT;

# Runbook: consolidate patients into the Portal database

**Why:** ACA production keeps patients in PatientService (`cdo_patients`), but billing,
claims/EDI, patient insurance and patient-portal logins read the Portal database
(`cdo_portal`). The Option 1 build makes the Portal database the only patient store.
Before that build ships, PatientService patients and their insurance are copied into
`cdo_portal` **with their existing `PatientId` and `PatientInsuranceId`**. Appointments,
booking requests and claims already store those ids, so nothing else needs rewriting.

**Rule:** nothing here writes to production except `11-migrate-into-portal.sql` and
`13-rollback-portal.sql`, and both run only inside the maintenance window below.

## Files

| Script | Runs against | Writes |
|---|---|---|
| `01-inventory-patientservice.sql` | `cdo_patients` | nothing (read-only transaction); creates `patientservice-ids.psql` locally |
| `02-inventory-portal.sql` | `cdo_portal` | nothing |
| `03-inventory-scheduling.sql` | `cdo_scheduling` | nothing; creates `scheduling-patient-refs.csv` locally |
| `10-export-patientservice.sql` | `cdo_patients` | nothing; creates `export-*.csv` and `export-checksum.psql` locally |
| `11-migrate-into-portal.sql` | `cdo_portal` | patients, patient insurance, any unmatched insurance plans, `_patient_store_migration` (one transaction) |
| `12-verify-portal.sql` | `cdo_portal` | nothing; reads `scheduling-patient-refs.csv` locally |
| `13-rollback-portal.sql` | `cdo_portal` | deletes only rows recorded in `_patient_store_migration` (one transaction); reads `scheduling-patient-refs.csv` locally |

Use psql 14 or later from a secured host with access to the ACA PostgreSQL server
(`SSL Mode=Require`). Always pass `-v ON_ERROR_STOP=1` and run every script from the
same working directory, because later scripts read files the earlier ones write.

> The CSV and `.psql` outputs contain patient data. Keep them on the secured host only,
> never commit them (see `.gitignore`), and delete them (`shred -u`) when the window closes.

## Step 1 · Inventory (4a), no downtime

```bash
psql "$PATIENTSERVICE_DB_URL" -v ON_ERROR_STOP=1 -f 01-inventory-patientservice.sql | tee 01.out
psql "$PORTAL_DB_URL"         -v ON_ERROR_STOP=1 -f 02-inventory-portal.sql         | tee 02.out
psql "$SCHEDULING_DB_URL"     -v ON_ERROR_STOP=1 -f 03-inventory-scheduling.sql     | tee 03.out
```

Share `01.out`, `02.out` and `03.out` (they contain counts, id ranges and ids, no names).
Decide from `02.out`:

* **Section 3 shows no Portal patients** → proceed with the migration below (4b).
* **Section 3 shows Portal patients, or section 4 shows overlapping ids** → **stop** (4c).
  Report the overlap; a different reconciliation is needed. `11-migrate-into-portal.sql`
  refuses to run in this case anyway.

Also note from `01.out` section 2 any patients whose `TenantId` is `default` (created by
the pre-#70 API without a tenant). They are copied as-is and stay invisible to staff until
reassigned to the practice's tenant.

## Step 2 · Maintenance window (4d)

1. **Announce maintenance** to the practice (start time and expected duration of about 30 minutes).
2. **Pause writes.** Deactivate the active revisions of `portal`, `scheduling-service`
   and `patient-service` (Container Apps has no zero-replica maximum, so deactivate):
   ```bash
   for app in portal scheduling-service patient-service; do
     rev=$(az containerapp revision list -g "$RG" -n "$app" --query "[?properties.active].name" -o tsv)
     az containerapp revision deactivate -g "$RG" -n "$app" --revision "$rev"
   done
   ```
   IntakeService stays up: website bookings and Zocdoc webhooks keep landing in its durable
   inbox and on Service Bus, and are processed after reopening.
3. **Back up both databases** (plus scheduling for completeness) and confirm the files are non-empty:
   ```bash
   stamp=$(date -u +%Y%m%dT%H%M%SZ)
   pg_dump "$PORTAL_DB_URL"         -Fc -f "cdo_portal-$stamp.dump"
   pg_dump "$PATIENTSERVICE_DB_URL" -Fc -f "cdo_patients-$stamp.dump"
   pg_dump "$SCHEDULING_DB_URL"     -Fc -f "cdo_scheduling-$stamp.dump"
   ```
4. **Run the migration:**
   ```bash
   psql "$PATIENTSERVICE_DB_URL" -v ON_ERROR_STOP=1 -f 10-export-patientservice.sql
   psql "$PORTAL_DB_URL"         -v ON_ERROR_STOP=1 -f 11-migrate-into-portal.sql
   ```
   Expect `NOTICE: This run inserted N patients and M insurance rows. Now present: N of N …`
   followed by `COMMIT`. Any `STOP` error means nothing was written; read the message.
5. **Verify:**
   ```bash
   psql "$PORTAL_DB_URL" -v ON_ERROR_STOP=1 -v patient_id=<known patient id> -f 12-verify-portal.sql
   ```
   Required: counts equal, `patient_checksum = MATCH`, every `sequence_ok = t`, and every
   `unresolved` and `tenant_mismatches` count `0`. The spot check shows the known patient with
   their insurance, claims, account, and any scheduling references in the same tenant.
6. **Deploy the Option 1 build** (the `deploy-aca.yml` workflow). It creates new active
   revisions of `portal` and `scheduling-service`. PatientService is no longer built, deployed or
   called. Bicep deployments are incremental, so the existing `patient-service` container app is
   **not deleted**: leave it deactivated (step 2) until the PatientService retirement pass removes it.
7. **Smoke-test with the known patient**, signed in as staff:
   * **Patients**: the patient is listed and opens with correct demographics.
   * **Billing**: selecting the patient loads their account and ledger (no "Patient was not found").
   * **Claim Wizard**: selecting the patient shows their insurance.
   * Optionally, approve a test booking request and confirm the appointment shows the right patient.
   * **Zocdoc path (SchedulingService → Portal internal port).** From inside the environment, send
     the known patient's own name and date of birth, so it matches instead of creating a patient.
     The runtime image has no curl, so this uses bash's `/dev/tcp`:
     ```bash
     az containerapp exec -g "$RG" -n scheduling-service --command "bash -c '
       body={\"firstName\":\"<First>\",\"lastName\":\"<Last>\",\"dateOfBirth\":\"<YYYY-MM-DD>\"}
       exec 3<>/dev/tcp/portal/5091
       printf \"POST /api/internal/patients/match-or-create?tenantId=<tenant> HTTP/1.1\r\nHost: portal:5091\r\nX-CDO-Service-Key: <PATIENT_SERVICE_API_KEY>\r\nContent-Type: application/json\r\nContent-Length: \${#body}\r\nConnection: close\r\n\r\n\$body\" >&3
       head -c 400 <&3'"
     ```
     Expect `HTTP/1.1 200` with `{"patientId":<known id>,"created":false}`. A `302` means EasyAuth is
     intercepting the internal port (roll back and report); `401` means the key or tenant is wrong.
     From outside, `https://<portal host>/api/internal/patients/match-or-create` must return `404`.
8. **Reopen:** confirm the new revisions are active and healthy (`/health/ready`), then announce the end of maintenance.

## Rollback

* **Before step 6 (new build not deployed):** run `13-rollback-portal.sql`, reactivate the
  previous revisions of the three apps, and reopen. PatientService is untouched by the migration.
* **After step 6 but before reopening:** redeploy the previous image tag, then run `13-rollback-portal.sql`.
* **After reopening:** do not use `13-rollback-portal.sql`; it refuses once claims, other
  patient-bearing Portal tables, or scheduling rows reference the migrated rows. Fix forward, or restore the step-3 backups with
  `pg_restore --clean` (this loses everything written since reopening).

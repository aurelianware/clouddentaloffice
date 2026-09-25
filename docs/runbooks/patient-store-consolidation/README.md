# Runbook: make the Portal database the only patient store (reset)

**Why:** ACA production kept patients in PatientService (`cdo_patients`), while billing, claims,
patient insurance and patient-portal logins read the Portal database (`cdo_portal`). The current
`main` build reads patients only from `cdo_portal`.

**Decision (2026-09-25):** the existing patient data is not needed, so instead of migrating it we
start `cdo_portal` fresh and clear the scheduling rows that point at the old patient ids.
Production held 3 PatientService patients, 2 draft claims, 3 appointments and 8 booking requests.

Why both databases: a fresh `cdo_portal` hands out patient ids 1, 2, 3 … again. Any appointment,
booking request or claim still pointing at the old ids would attach to the wrong new patient.

A fresh `cdo_portal` also fixes a separate gap: the old database was created before the billing
tables existed (`PatientAccounts`, statements, payments, portal logins) and never received them.
On first start the Portal creates the full current schema and re-seeds:

* the tenant, organization and review-outreach settings (from the `InitialTenant__*` settings);
* the 4 sample providers (tenant `demo`) and the 38 procedure codes;
* staff sign-in comes from the `StaffAuth__Users__*` settings, not the database.

Not recreated: providers that staff added (re-enter them, step 7) and anything else staff typed in.

`cdo_scheduling` is **cleaned, not recreated**, so the Google Search Console connection and its
history (Google only offers about 16 months to re-import) and the patient-acquisition events stay.

> The files `01`–`13` in this folder belong to the superseded migration plan. Do not run them.

## Files

| Script | Runs against | Writes |
|---|---|---|
| `20-precheck.sql` | `cdo_portal` | nothing (read-only) |
| `21-clear-scheduling-patient-refs.sql` | `cdo_scheduling` | deletes all `Appointments` and `BookingRequests` (one transaction) |
| `22-verify-fresh-portal.sql` | `cdo_portal` | nothing (read-only) |

## Connecting (Azure Cloud Shell, Bash)

Cloud Shell has psql, git and az, and reaches the database without a firewall change. The
connection details come from the Portal's `conn-default` Container App secret:

```bash
git clone https://github.com/aurelianware/clouddentaloffice.git
cd clouddentaloffice/docs/runbooks/patient-store-consolidation
RG=cdo-prod-rg
SERVER=cdo-postgres-mfosw3gaw6fk2
conn=$(az containerapp secret show -g "$RG" -n portal --secret-name conn-default --query value -o tsv)
export PGHOST=$(sed -n 's/.*Host=\([^;]*\).*/\1/p' <<<"$conn")
export PGUSER=$(sed -n 's/.*Username=\([^;]*\).*/\1/p' <<<"$conn")
export PGPASSWORD=$(sed -n 's/.*Password=\([^;]*\).*/\1/p' <<<"$conn")
export PGSSLMODE=require
unset conn
```

## Before the window

Keep automatic deploys from shipping the new build early: they fail today only because Azure
login is broken (see step 5). Do not fix that login before the window, or first disable the
**Deploy to Azure Container Apps** workflow in the repository's Actions tab.

## Maintenance window (about 30 minutes)

1. **Announce maintenance** to the practice.
2. **Pause the apps.** Deactivate the active revisions of `portal`, `scheduling-service` and
   `patient-service`:
   ```bash
   failed=0
   for app in portal scheduling-service patient-service; do
     revs=$(az containerapp revision list -g "$RG" -n "$app" --query "[?properties.active].name" -o tsv) \
       || { echo "LIST FAILED: $app"; failed=1; continue; }
     for rev in $revs; do
       az containerapp revision deactivate -g "$RG" -n "$app" --revision "$rev" \
         || { echo "DEACTIVATE FAILED: $app $rev"; failed=1; }
     done
     left=$(az containerapp revision list -g "$RG" -n "$app" --query "[?properties.active].name" -o tsv)
     [ -z "$left" ] || { echo "STILL ACTIVE: $app $left"; failed=1; }
   done
   [ "$failed" = 0 ] && echo "ALL APPS PAUSED" || echo "STOP: not every app is paused"
   ```
   Continue only after `ALL APPS PAUSED`. An app with no active revision is skipped.
   IntakeService stays up: new website bookings and Zocdoc webhooks wait in its inbox and on
   Service Bus and are processed after reopening.
3. **Back up all three databases.** Each dump must succeed, be non-empty and be readable by
   `pg_restore`. They contain patient data: keep them in secured storage, never in the repository.
   ```bash
   stamp=$(date -u +%Y%m%dT%H%M%SZ)
   failed=0
   for db in cdo_portal cdo_patients cdo_scheduling; do
     f="$db-$stamp.dump"
     if pg_dump -d "$db" -Fc -f "$f" && [ -s "$f" ] && pg_restore -l "$f" > /dev/null; then
       echo "OK $f"
     else
       echo "BACKUP FAILED: $db"; failed=1
     fi
   done
   [ "$failed" = 0 ] && echo "ALL BACKUPS OK" || echo "STOP: a backup failed"
   ```
   Continue only after `ALL BACKUPS OK`.
4. **Pre-check, then reset.**
   ```bash
   set -o pipefail
   psql -d cdo_portal -v ON_ERROR_STOP=1 -f 20-precheck.sql | tee 20-precheck.out \
     && echo "PRECHECK OK" || echo "STOP: the pre-check failed"
   ```
   `pipefail` makes a `psql` failure show even though the output goes through `tee`.
   Section 1 lists the providers. Ids 1–4 in tenant `demo` are samples; **keep the details of
   any other provider** (the output file has them) to re-enter in step 7. Section 2 must show
   `added_by_staff = 0`, and section 3 only the tables it names. If anything else has rows, stop
   and decide whether it matters before continuing.

   Only after `ALL BACKUPS OK`, `PRECHECK OK` and reading the pre-check output, recreate
   `cdo_portal` empty and clear the scheduling references. Each command runs only if the one
   before it succeeded:
   ```bash
   az postgres flexible-server db delete --resource-group "$RG" --server-name "$SERVER" --name cdo_portal --yes \
     && az postgres flexible-server db create --resource-group "$RG" --server-name "$SERVER" --name cdo_portal \
     && psql -d cdo_scheduling -v ON_ERROR_STOP=1 -f 21-clear-scheduling-patient-refs.sql \
     && echo "RESET DONE" || echo "STOP: the reset did not finish; see the error above"
   ```
   Expect `appointments 0, booking_requests 0`, `COMMIT` and `RESET DONE`.
5. **Fix the deploy login.** GitHub now identifies the repository to Azure with numeric ids, and
   the app registration has no federated credential for that subject (deploy runs 68–71 failed
   with `AADSTS700213`). Add one, using the application (client) id stored in the
   `AZURE_CLIENT_ID` repository secret:
   ```bash
   az ad app federated-credential create --id <AZURE_CLIENT_ID> --parameters '{
     "name": "github-main",
     "issuer": "https://token.actions.githubusercontent.com",
     "subject": "repo:aurelianware@194855645/clouddentaloffice@1158178664:ref:refs/heads/main",
     "audiences": ["api://AzureADTokenExchange"]
   }'
   ```
6. **Deploy.** Re-enable the deploy workflow if you disabled it, then run **Deploy to Azure
   Container Apps** from the Actions tab (*Run workflow* on `main`). It runs CI first, then
   creates new active revisions of `portal` and `scheduling-service`. The `patient-service` app is
   no longer deployed and stays deactivated. When the run is green:
   ```bash
   psql -d cdo_portal -v ON_ERROR_STOP=1 -v tenant_id=third-set-smiles -f 22-verify-fresh-portal.sql
   ```
   Every row must end in `t`. It checks that every table in the current model exists (including
   all the billing tables), that the practice tenant, organization and review settings were
   created, that the seed is exactly providers 1–4 in the sample tenant `demo` plus the 38 CDT
   codes, and that there are no patients or claims.
7. **Re-enter staff-added providers** on the Providers page, from the step 4 output.
8. **Smoke-test**, signed in as staff. Create a test patient with insurance, then:
   * **Patients:** the patient is listed and opens with the right details.
   * **Billing:** selecting the patient loads their account (this page could not work before).
   * **Claim Wizard:** selecting the patient shows their insurance.
   * **Zocdoc path (SchedulingService → Portal internal port).** From inside the environment, send
     the test patient's own name and date of birth, so it matches instead of creating a patient.
     The runtime image has no curl, so this uses bash's `/dev/tcp`:
     ```bash
     az containerapp exec -g "$RG" -n scheduling-service --command "bash -c '
       body={\"firstName\":\"<First>\",\"lastName\":\"<Last>\",\"dateOfBirth\":\"<YYYY-MM-DD>\"}
       exec 3<>/dev/tcp/portal/5091
       printf \"POST /api/internal/patients/match-or-create?tenantId=third-set-smiles HTTP/1.1\r\nHost: portal:5091\r\nX-CDO-Service-Key: <PATIENT_SERVICE_API_KEY>\r\nContent-Type: application/json\r\nContent-Length: \${#body}\r\nConnection: close\r\n\r\n\$body\" >&3
       head -c 400 <&3'"
     ```
     Expect `HTTP/1.1 200` with `{"patientId":<test patient id>,"created":false}`. A `302` means
     EasyAuth is intercepting the internal port; `401` means the key or tenant is wrong. From
     outside, `https://<portal host>/api/internal/patients/match-or-create` must return `404`.

   Archive the test patient afterwards if you don't want it listed.
9. **Reopen:** confirm the new revisions are healthy (`/health/ready`) and announce the end of
   maintenance.

## If something goes wrong

* **Before step 4's reset:** reactivate the previous revisions and reopen. Nothing has changed.
* **After the reset, before reopening:** restore the step 3 backups, then reactivate the previous
  revisions:
  ```bash
  az postgres flexible-server db delete --resource-group "$RG" --server-name "$SERVER" --name cdo_portal --yes
  az postgres flexible-server db create --resource-group "$RG" --server-name "$SERVER" --name cdo_portal
  pg_restore -d cdo_portal --no-owner cdo_portal-<stamp>.dump
  pg_restore -d cdo_scheduling --clean --if-exists --no-owner cdo_scheduling-<stamp>.dump
  ```
  If the new build is already deployed, redeploy the previous image tag first, or the Portal
  will ignore the restored PatientService setup.
* **After reopening:** fix forward; restoring would discard everything entered since.

## Later (PatientService retirement)

* Delete the `patient-service` container app and, once your records-retention needs are met,
  the `cdo_patients` database. The step 3 backup keeps a copy.
* Remove the PatientService projects, `PatientService.Dockerfile` and the superseded scripts
  `01`–`13` in this folder.

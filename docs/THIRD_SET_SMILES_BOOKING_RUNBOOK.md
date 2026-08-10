# 3rd Set Smiles production booking intake

This path records a **booking request for staff review**. A `202` never means an appointment is confirmed. Only `IntakeService` is public; API Gateway, Portal, SchedulingService, PostgreSQL, and their APIs remain private.

## Architecture and controls

`3rdsetsmiles.com /book` → Cloudflare Pages Function → HTTPS IntakeService → Azure Service Bus topic `booking-requests` → private SchedulingService subscription `scheduling` → tenant-scoped `BookingRequests` table → staff review/approval.

- Tenant: `third-set-smiles`; practice: 3rd Set Smiles; domain: `3rdsetsmiles.com`; timezone: `America/Phoenix` (the website sends an offset-bearing UTC instant).
- API keys map to a tenant server-side. Use a unique 32-byte random key per site/environment; rotate by temporarily adding a second `Clients` entry, updating Cloudflare, then removing the old entry.
- IntakeService has no database connection and exposes only POST intake plus `/health`. Swagger is development-only.
- Scheduling/API gateway ingress stays internal. Never route `/api/booking-requests` or `/api/appointments` from a public ingress.
- Accepted data is limited to name, phone, optional email, new/existing relationship, preferred instant, optional 15–240 minute duration, reason (500), and message (2,000). Times must include an offset and be 5 minutes–1 year ahead.
- `Idempotency-Key` (8–128 characters) deterministically sets the event ID. Service Bus suppresses the same message ID for 24 hours and `(TenantId, EventId)` is uniquely indexed in PostgreSQL. Without the header, each accepted retry is a new request.
- Broker unavailable/unconfigured returns `503`; unauthorized returns `401`; invalid returns `400`; throttled returns `429`; accepted returns `202 { status: "requested" }`. Cloudflare then retains Resend as its fallback/copy.

## Assumptions requiring confirmation

- Azure Container Apps, Azure Service Bus Standard, and PostgreSQL Flexible Server are the intended production platform.
- Confirm the desired public hostname. The generated ACA URL works immediately; `https://book-api.3rdsetsmiles.com` is the recommended custom domain.
- Dr. Matthew Phillips's NPI, Arizona license number, exact suffix, and staff identity/login are intentionally not seeded. Add them through the authenticated Portal after confirming the values.
- Current website code does not send `Idempotency-Key`; server support is ready, but end-to-end retry deduplication requires a small follow-up in `3rdsetsmiles` to generate and reuse one per form submission.

## Deploy in order

Prerequisites: Azure CLI logged in, resource group selected, images built/pushed to ACR (including `intake-service` and `scheduling-service`), and database backup/restore tested.

Pin and verify the production subscription before every manual deployment. The
GitHub repository secret `AZURE_SUBSCRIPTION_ID` must contain the same value:

```bash
az account set --subscription 85bd1f0d-3a84-4070-a1d1-9358fa42c10e
test "$(az account show --query id -o tsv)" = "85bd1f0d-3a84-4070-a1d1-9358fa42c10e"
az account show --query '{name:name,id:id,tenantId:tenantId,state:state}' -o table
```

Expected tenant ID: `e77fcd28-df84-4a84-b8ff-63edfb2bb498`. Stop if either
identifier differs or the subscription state is not `Enabled`.

1. Deploy base infrastructure. This provisions Log Analytics, ACR, Container Apps environment, and Service Bus topic/subscription with duplicate detection:

   ```bash
   az deployment group create -g cdo-rg -f infrastructure/azure/main.bicep -p appName=cdo
   ```

2. Deploy PostgreSQL, then create/apply the scheduling and portal schemas. Back up before upgrading an existing environment:

   ```bash
   az deployment group create -g cdo-rg -f infrastructure/azure/postgres.bicep -p adminPassword='<POSTGRES_PASSWORD>'
   ```

3. Retrieve the Service Bus connection string without printing or committing it, and create independent random secrets:

   ```bash
   az servicebus namespace authorization-rule keys list -g cdo-rg --namespace-name '<SERVICE_BUS_NAMESPACE>' --name booking-intake-send --query primaryConnectionString -o tsv
   az servicebus namespace authorization-rule keys list -g cdo-rg --namespace-name '<SERVICE_BUS_NAMESPACE>' --name booking-scheduling-listen --query primaryConnectionString -o tsv
   openssl rand -base64 32   # CLOUDDENTAL_API_KEY / publicBookingApiKey
   openssl rand -base64 48   # JWT key
   ```

4. Deploy Container Apps. Pass secrets through a protected parameter source/CI secret, not shell history. Required app parameters are `postgresAdminPassword`, `jwtKey`, `serviceBusSendConnection`, `serviceBusListenConnection`, and `publicBookingApiKey`. `initialTenantId` defaults to `third-set-smiles`.

5. Verify SchedulingService is healthy and listening on `booking-requests/scheduling`; verify the `third-set-smiles` TenantRegistry and Organization rows exist. Then verify IntakeService. Only after the full consumer smoke test succeeds should Cloudflare be enabled.

The deployment uses the Container Apps Consumption profile. Portal, API Gateway,
and IntakeService keep one replica warm. SchedulingService scales from zero when
the Service Bus subscription contains a message. Patient, claims, eligibility,
ERA, auth, prescription, and vision services scale to zero between requests.
This avoids paying for ten continuously running replicas while keeping the
booking form and staff review path responsive.

Minimum runtime settings:

| Service | Setting/secret |
|---|---|
| Intake | `ASPNETCORE_ENVIRONMENT=Production`, `PublicBooking__Enabled=true`, `PublicBooking__Clients__0__TenantId=third-set-smiles`, `PublicBooking__Clients__0__ApiKey`, `PublicBooking__Source=3rdsetsmiles.com`, send-only `ServiceBus__ConnectionString` |
| Scheduling | `ASPNETCORE_ENVIRONMENT=Production`, `DatabaseProvider=PostgreSQL`, `ConnectionStrings__SchedulingDb`, listen-only `ServiceBus__ConnectionString`, topic `booking-requests`, subscription `scheduling` |
| Portal bootstrap | `InitialTenant__Enabled=true`, `InitialTenant__TenantId=third-set-smiles`, `InitialTenant__Name=3rd Set Smiles`, `InitialTenant__Domain=3rdsetsmiles.com` |

## Smoke tests

Set local shell variables without recording secrets in the command history. Health must return 200:

```bash
curl --fail-with-body "${CLOUDDENTAL_API_BASE}/health"
```

Unauthorized must return 401. Invalid/missing timezone must return 400. A valid request must return 202 and `status=requested`:

```bash
curl -i -X POST "${CLOUDDENTAL_API_BASE}/api/public/booking-requests" \
  -H 'Content-Type: application/json' \
  --data '{"name":"Production smoke test","phone":"4805550100","patientRelationship":"New","preferredStart":"2026-09-15T17:00:00Z"}'

curl --fail-with-body -X POST "${CLOUDDENTAL_API_BASE}/api/public/booking-requests" \
  -H "Authorization: Bearer ${CLOUDDENTAL_API_KEY}" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: deploy-smoke-2026-09-15' \
  --data '{"name":"Production smoke test","phone":"4805550100","email":"ops@example.com","patientRelationship":"New","preferredStart":"2026-09-15T17:00:00Z","durationMinutes":60,"reason":"Deployment verification"}'
```

Repeat the idempotent request and confirm only one tenant-scoped row exists. In the private staff UI, confirm it is `New`, not an Appointment. Reject/delete the synthetic request through the normal staff workflow. Also submit through `/book/` and verify both the staff queue entry and Resend copy.

## Monitoring and alerts

Logs contain event ID and tenant ID, never full request bodies or contact fields. Send Container App logs to Log Analytics and alert on:

- Intake 5xx > 2 in 5 minutes; 401/429 spikes > 20 in 5 minutes.
- Intake zero healthy replicas for 2 minutes or p95 response time > 2 seconds for 10 minutes.
- Service Bus active messages oldest age > 5 minutes, dead-letter count > 0, or subscription delivery count/retries rising.
- Scheduling consumer exceptions or PostgreSQL connection failures > 0 in 5 minutes.

Route alerts to the practice operations action group. Review dead letters before replay; never paste message bodies into tickets or chat.

## Cloudflare Pages values

In **Workers & Pages → 3rdsetsmiles → Settings → Variables and Secrets**, configure Production (and Preview separately if desired), then redeploy:

| Variable | Exact value |
|---|---|
| `CLOUDDENTAL_API_BASE` | `https://<intakeFqdn output>` (no trailing slash), or confirmed custom domain `https://book-api.3rdsetsmiles.com` |
| `CLOUDDENTAL_API_KEY` | same generated 32-byte secret as Intake client 0; store encrypted |
| `CLOUDDENTAL_BOOKING_PATH` | omit (default is `/api/public/booking-requests`) |
| `CLOUDDENTAL_APPT_MINUTES` | `60` |
| `CLOUDDENTAL_TIMEOUT_MS` | `8000` |

Keep `RESEND_API_KEY`, `CONTACT_TO_EMAIL`, and `CONTACT_FROM_EMAIL` configured. Validation checklist: valid booking yields the request-only thank-you page; CDO row is tenant `third-set-smiles` and status `New`; Resend copy arrives; bad API key causes Resend-only delivery; temporarily set API base to an unreachable HTTPS host and confirm the response returns after about 8 seconds and Resend still delivers; restore the base and redeploy.

## Rollback

First remove `CLOUDDENTAL_API_BASE` and `CLOUDDENTAL_API_KEY` from Cloudflare Production and redeploy; the website immediately uses Resend only. Do not delete queued messages. Roll Container Apps back to the prior revision, keeping SchedulingService private. If the consumer caused the incident, scale only SchedulingService to zero while Intake continues queueing, then restore the database from the pre-deploy backup if schema/data rollback is required. Rotate the public key after any suspected exposure. Replay dead letters only after the fix is deployed and tenant/event IDs are checked.

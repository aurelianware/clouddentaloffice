# 3rd Set Smiles production booking intake

This path records a **booking request for staff review**. A `202` never means an appointment is confirmed. Only `IntakeService` is public; API Gateway, Portal, SchedulingService, PostgreSQL, and their APIs remain private.

## Architecture and controls

`3rdsetsmiles.com /book` → Cloudflare Pages Function → HTTPS IntakeService → Azure Service Bus topic `booking-requests` → private SchedulingService subscription `scheduling` → tenant-scoped `BookingRequests` table → staff review/approval.

Availability follows a separate read path:

`3rdsetsmiles.com /book` → same-origin Cloudflare Function → authenticated IntakeService → tenant-authenticated private SchedulingService → canonical `ISchedulingAvailabilityService` (`PublicWebsite` channel).

The response contains public aliases, display labels, times, and an opaque signed
selection only. It contains no tenant ID, patient data, integration configuration,
or internal provider/location/appointment-type IDs. IntakeService revalidates the
opaque selection immediately before publishing `BookingRequestedEvent`; a stale
slot returns HTTP `409` and publishes nothing.

## Booking semantics

- **Request-based website booking:** an anonymous visitor selects canonical
  availability, but submission creates only a `BookingRequest`. Staff match the
  patient and use **Approve & Schedule** before an `Appointment` exists.
- **Confirmed marketplace booking:** an authenticated marketplace such as Zocdoc
  uses the external confirmed-booking workflow and may create/confirm an
  appointment after webhook verification and patient resolution.

Never route the website through the confirmed marketplace workflow, and never
turn the public POST into anonymous appointment creation.

- Tenant: `third-set-smiles`; practice: 3rd Set Smiles; domain: `3rdsetsmiles.com`; timezone: `America/Phoenix` (the website sends an offset-bearing UTC instant).
- API keys map to a tenant server-side. Use a unique 32-byte random key per site/environment; rotate by temporarily adding a second `Clients` entry, updating Cloudflare, then removing the old entry.
- IntakeService has no database connection and exposes only POST intake plus `/health`. Swagger is development-only.
- Scheduling/API gateway ingress stays internal. Never route `/api/booking-requests` or `/api/appointments` from a public ingress.
- Accepted data is limited to name, phone, optional email, new/existing relationship, preferred and optional alternate instants, preferred contact, optional 15–240 minute duration, reason (500), scheduling message (2,000), insurance intent/carrier, source/campaign, and allowlisted attribution metadata. Times must include an offset and the preferred time must be 5 minutes–1 year ahead. Member/subscriber IDs, card images, and clinical data are not accepted.
- The website creates one `requestId` per loaded form and sends it as `Idempotency-Key` (8–128 characters). The tenant/key pair deterministically sets the event ID; Service Bus duplicate detection and the `(TenantId, EventId)` unique index make a replay an accepted success with one persisted request. Without the header, legacy callers remain supported and each accepted retry is a new request.
- Broker unavailable/unconfigured returns `503`; unauthorized returns `401`; invalid returns `400`; throttled returns `429`; accepted returns `202 { status: "requested" }`. Cloudflare then retains Resend as its fallback/copy.

### Contract v2 and migration

`BookingRequestedEvent` remains the same event subject and retains its original positional fields. Additive `ContractVersion=2` properties carry website request ID, preferred contact, alternate time, insurance intent/carrier, campaign, attribution ID/allowlisted metadata, and submitted timestamp. Older messages deserialize with null optional fields.

Scheduling startup adds the corresponding nullable columns to existing SQLite, PostgreSQL, or SQL Server `BookingRequests` tables and backfills `SubmittedAtUtc` with the database current timestamp. Back up the scheduling database before the first upgraded deployment. No new secrets or public routes are required.

## Assumptions requiring confirmation

- Azure Container Apps, Azure Service Bus Standard, and PostgreSQL Flexible Server are the intended production platform.
- Confirm the desired public hostname. The generated ACA URL works immediately; `https://book-api.3rdsetsmiles.com` is the recommended custom domain.
- Dr. Matthew Phillips's NPI, Arizona license number, exact suffix, and staff identity/login are intentionally not seeded. Add them through the authenticated Portal after confirming the values.

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
   az deployment group create -g cdo-prod-rg -f infrastructure/azure/main.bicep -p appName=cdo location=westus3
   ```

2. Deploy PostgreSQL, then create/apply the scheduling and portal schemas. Back up before upgrading an existing environment:

   ```bash
   az deployment group create -g cdo-prod-rg -f infrastructure/azure/postgres.bicep -p location=westus3 adminPassword='<POSTGRES_PASSWORD>'
   ```

3. Retrieve the Service Bus connection string without printing or committing it, and create independent random secrets:

   ```bash
   az servicebus namespace authorization-rule keys list -g cdo-prod-rg --namespace-name '<SERVICE_BUS_NAMESPACE>' --name booking-intake-send --query primaryConnectionString -o tsv
   az servicebus namespace authorization-rule keys list -g cdo-prod-rg --namespace-name '<SERVICE_BUS_NAMESPACE>' --name booking-scheduling-listen --query primaryConnectionString -o tsv
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
| Scheduling public availability | `InternalApi__PublicIntakeClients__0__TenantId`, secret `InternalApi__PublicIntakeClients__0__ApiKey`, secret `PublicAvailability__SlotTokenKey`; enable a tenant `PublicWebsite` channel configuration and create active provider/location/visit-reason mappings |
| Intake scheduling client | `Services__SchedulingService=http://scheduling-service`, matching tenant/key under `Services__SchedulingServiceClients__0__*` |

The deployment workflow requires two independent 32+ character GitHub secrets:
`PUBLIC_SCHEDULING_SERVICE_API_KEY` for the Intake→Scheduling tenant boundary
and `PUBLIC_AVAILABILITY_SLOT_KEY` for authenticated encryption. Do not reuse the website's
`PUBLIC_BOOKING_API_KEY` for either purpose.

An administrator can enable the channel using
`PUT /api/scheduling-integrations/PublicWebsite/configuration`, then manage the
public aliases with the existing authenticated
`/api/scheduling-integrations/PublicWebsite/mappings` endpoints. Map only the
providers, locations, and appointment types that the website may offer.
| Portal bootstrap | `InitialTenant__Enabled=true`, `InitialTenant__TenantId=third-set-smiles`, `InitialTenant__Name=3rd Set Smiles`, `InitialTenant__Domain=3rdsetsmiles.com` |

## Staff portal Google Workspace authentication

The Portal alone uses Azure Container Apps built-in Google authentication. IntakeService remains public with its independent API key; SchedulingService and the API gateway remain private.

Production uses these custom domains. Their Cloudflare records must remain DNS-only so Azure can validate and renew the managed certificates:

- Staff portal: `https://portal.3rdsetsmiles.com`
- Booking intake: `https://book-api.3rdsetsmiles.com`

The deployment workflow idempotently binds both hostnames and their Azure-managed certificates after the Container Apps Bicep deployment. Required Cloudflare records are `portal` and `book-api` CNAMEs pointing to their generated Container Apps hostnames, plus matching `asuid.portal` and `asuid.book-api` TXT records containing the Container Apps custom-domain verification ID.

Create a Google OAuth **Web application** client with:

- Authorized JavaScript origin: `https://portal.3rdsetsmiles.com`
- Authorized redirect URI: `https://portal.3rdsetsmiles.com/.auth/login/google/callback`

Store the client values as GitHub Actions secrets `GOOGLE_OAUTH_CLIENT_ID` and
`GOOGLE_OAUTH_CLIENT_SECRET`. Never commit or print the client secret. The ACA
deployment enables Easy Auth only when the client ID is supplied.

The application independently enforces a least-privilege allowlist and pins every
approved identity to tenant `third-set-smiles`. Initial administrators are:

- `matt@3rdsetsmiles.com`
- `markus.phillips@gmail.com` (temporary deployment/testing access; remove after handoff)

A matching Workspace suffix is not sufficient by itself. Any Google identity not
explicitly listed receives HTTP 403 after authentication. Require Workspace MFA,
review ACA authentication logs, and remove the temporary Gmail administrator after
Matt verifies access.

Authentication smoke tests:

1. An anonymous request to the Portal redirects to Google.
2. Matt can sign in and sees the `third-set-smiles` appointment-request queue.
3. The temporary Gmail administrator can sign in during deployment testing.
4. A Google identity outside the allowlist receives 403.
5. Intake `/health` and authenticated website booking intake still work unchanged.

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
| `CLOUDDENTAL_API_BASE` | `https://book-api.3rdsetsmiles.com` (no trailing slash) |
| `CLOUDDENTAL_API_KEY` | same generated 32-byte secret as Intake client 0; store encrypted |
| `CLOUDDENTAL_BOOKING_PATH` | omit (default is `/api/public/booking-requests`) |
| `CLOUDDENTAL_APPT_MINUTES` | `60` |
| `CLOUDDENTAL_TIMEOUT_MS` | `8000` |

Keep `RESEND_API_KEY`, `CONTACT_TO_EMAIL`, and `CONTACT_FROM_EMAIL` configured. Validation checklist: valid booking yields the request-only thank-you page; CDO row is tenant `third-set-smiles` and status `New`; Resend copy arrives; bad API key causes Resend-only delivery; temporarily set API base to an unreachable HTTPS host and confirm the response returns after about 8 seconds and Resend still delivers; restore the base and redeploy.

## Rollback

First remove `CLOUDDENTAL_API_BASE` and `CLOUDDENTAL_API_KEY` from Cloudflare Production and redeploy; the website immediately uses Resend only. Do not delete queued messages. Roll Container Apps back to the prior revision, keeping SchedulingService private. If the consumer caused the incident, scale only SchedulingService to zero while Intake continues queueing, then restore the database from the pre-deploy backup if schema/data rollback is required. Rotate the public key after any suspected exposure. Replay dead letters only after the fix is deployed and tenant/event IDs are checked.

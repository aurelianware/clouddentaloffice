# Zocdoc scheduling integration

CloudDentalOffice's first Zocdoc adapter is a server-to-server integration
boundary for reference-data mapping, timeslot synchronization, and confirmed
appointment webhook processing.

The implementation follows the official documentation current in August 2026:

- [Authentication and access tokens](https://api-docs.zocdoc.com/guides/authentication)
- [API reference](https://api-docs.zocdoc.com/apis/calendar-integration-timeslots)
- [Reference data](https://api-docs.zocdoc.com/guides/reference-data)
- [Schedulable entities feed](https://api-docs.zocdoc.com/guides/schedulable-entities-feed)
- [Create timeslots](https://api-docs.zocdoc.com/guides/scheduling/create-timeslots)
- [Webhooks](https://api-docs.zocdoc.com/guides/webhooks)
- [Performance and rate limits](https://api-docs.zocdoc.com/guides/performance)

Production API access and particular scopes/products may require Zocdoc partner
approval, contracting, certification, and credentials issued by Zocdoc. Do not
assume sandbox access grants production access.

## Environments

| Environment | API base URL | OAuth token URL | OAuth audience |
| --- | --- | --- | --- |
| Sandbox | `https://api-developer-sandbox.zocdoc.com/` | `https://auth-api-developer-sandbox.zocdoc.com/oauth/token` | `https://api-developer-sandbox.zocdoc.com/` |
| Production | `https://api-developer.zocdoc.com/` | `https://auth.zocdoc.com/oauth/token` | `https://api-developer.zocdoc.com/` |

The adapter uses OAuth 2.0 client credentials. Tokens are cached before expiry
and keyed by tenant, environment, and client ID. A cached token is never shared
between tenants.

## Tenant configuration

Administrators configure and operate the integration in the Portal at:

```text
Settings -> Integrations -> Scheduling
```

The page provides Zocdoc status and mapping counts, safe non-secret settings,
provider/location/visit-reason mapping, publishable availability inspection,
connection testing, external-reference refresh, availability reconciliation,
and sanitized operational diagnostics. The route and every backing Scheduling
Service endpoint require the `Admin` role and derive the tenant exclusively from
the authenticated tenant claim.

The UI accepts only an opaque credential reference. It never accepts, reads, or
returns the client secret, webhook signing key, OAuth tokens, raw webhook bodies,
or remote payloads. Secret values remain in Azure Key Vault or Container App
secret configuration. A connection test is the safe way to verify that the
referenced credentials are present and valid.

Create or update the tenant's `SchedulingIntegrationConfiguration`:

```text
TenantId: <tenant ID>
Channel: Zocdoc
Enabled: true
Environment: Sandbox | Production
CredentialReference: <opaque configuration reference>
```

`CredentialReference` is not a secret. It selects a configuration section whose
values are supplied by user secrets, Azure Container App secrets, Key Vault, or
another configured ASP.NET configuration provider.

For a reference named `third-set-smiles-zocdoc`, configure these placeholders:

```text
SchedulingCredentials__third-set-smiles-zocdoc__ClientId=<Client ID>
SchedulingCredentials__third-set-smiles-zocdoc__ClientSecret=<Client Secret>
```

Never place the values in `appsettings.json`, database rows, source control,
logs, screenshots, or pull-request descriptions.

## Current adapter operations

`ZocdocSchedulingAdapter` implements the canonical
`ISchedulingExternalEntitySource` contract:

- validate OAuth credentials and authorized API connectivity with a bounded
  `GET /v1/schedulable_entities` request;
- retrieve active provider/location data from `GET /v1/schedulable_entities`;
- retrieve visit-reason IDs and names from `GET /v1/visit_reasons`;
- map Zocdoc transport DTOs into channel-neutral provider, location, and visit
  reason entities for the CDO mapping workflow.
- replace the complete timeslot set for one mapped provider/local date through
  `PUT /v1/providers/{provider_id}/calendar/timeslots?date=YYYY-MM-DD`.

Zocdoc JSON DTOs are internal to `Integrations/Zocdoc`. They are never returned
from core SchedulingService APIs.

## Errors and observability

The transport translates failures into integration categories:

- authentication
- authorization
- throttling
- remote validation
- temporary remote failure
- local misconfiguration

Typed clients use the standard .NET HTTP resilience pipeline. Zocdoc documents
HTTP `429` for rate limiting and recommends exponential backoff. Structured
logs include tenant ID, `Channel=Zocdoc`, operation, external correlation ID
when returned, result, and duration. Tokens, client secrets, response bodies,
and PHI are not logged.

## Appointment webhooks

Configure Zocdoc to send appointment events to the externally reachable
IntakeService route:

```text
POST https://<intake-host>/api/integrations/zocdoc/<opaque-integration-id>/webhooks
```

Configure the trusted route mapping and Zocdoc-issued base64 signing key through
secret-backed environment variables (one numeric index per integration):

```text
ZocdocWebhooks__Integrations__0__IntegrationId=<unguessable route identifier>
ZocdocWebhooks__Integrations__0__TenantId=<CDO tenant ID>
ZocdocWebhooks__Integrations__0__WebhookSecret=<base64 Zocdoc shared key>
ZocdocWebhooks__Integrations__0__Enabled=true
```

SchedulingService resolves patients through PatientService's private internal
endpoint using a separate tenant-scoped service credential. Configure the same
32+ character secret on both services:

```text
# PatientService
InternalApi__Clients__0__TenantId=<CDO tenant ID>
InternalApi__Clients__0__ApiKey=<service credential>

# SchedulingService
Services__PatientServiceClients__0__TenantId=<CDO tenant ID>
Services__PatientServiceClients__0__ApiKey=<same service credential>
```

The caller sends the credential in `X-CDO-Service-Key`. PatientService uses a
constant-time comparison and authorizes it only for the tenant in the matching
configuration entry. The Azure deployment exposes this as the secure
`patientServiceApiKey` parameter; never reuse the public-booking or webhook key.

The ingress follows Zocdoc's documented verification algorithm: it enforces the
five-minute timestamp tolerance, decodes the shared key from base64, and checks
the `webhook-signature` `v1` value against HMAC-SHA256 of the exact UTF-8 bytes
`<webhook-timestamp>.<raw request body>` using a constant-time comparison. The
tenant comes only from the trusted route mapping, never from the event body.

After verification, IntakeService commits a PHI-free event containing only the
tenant, external event ID, appointment ID, and update type to its isolated durable
inbox before returning `202`. A background dispatcher publishes that record to
Service Bus; SchedulingService
then fetches the current appointment from `GET /v1/appointments/{appointment_id}`,
resolves tenant-scoped provider/location/visit-reason mappings, safely matches or
creates the patient through PatientService, revalidates the slot, persists the
appointment and external reference, and calls `POST /v1/appointments/confirm`
for a `pending_booking`. A persisted channel/tenant/event lease prevents duplicate
delivery from creating a second appointment. Cancellations and updates use the
existing external appointment reference.

Raw webhook bodies, demographics, tokens, and secrets are never logged. A local
appointment is not created when a required mapping is missing or the selected
slot is no longer available.

### Troubleshooting

- `401`: signature missing, invalid, wrong secret, or timestamp outside five minutes.
- `404`: the route identifier is unknown or the tenant integration is disabled.
- `400`: the signed body is malformed or is not a supported appointment event.
- `503`: the inbox database could not durably commit the event; it was not accepted.
- Service Bus outages do not change webhook acknowledgement after the inbox commit.
  Pending records retry with bounded exponential backoff and eventually enter
  `Failed` rather than being discarded.
- A dead-lettered `DisabledIntegration` message means the integration was disabled
  after ingress accepted the event.
- Repeated processing failures usually indicate missing entity mappings, a slot
  collision, PatientService unavailability, or a temporary Zocdoc API failure.
  Inspect structured logs using tenant/event IDs; do not copy request bodies into logs.

Credential rotation and production certification automation remain operational
tasks. Production webhook and API access may require Zocdoc approval.

## Availability synchronization

```text
CDO provider schedules and appointments
  -> canonical availability engine
  -> tenant-scoped provider/location/visit-reason mappings
  -> Zocdoc adapter
  -> provider/date timeslot replacement
```

Zocdoc's API replaces every slot for a provider and date; an empty `timeslots`
array clears that date. Reconciliation therefore recalculates the complete
provider/date unit. A persisted content hash suppresses unchanged requests,
making duplicate events retry-safe and avoiding unnecessary API calls.

Scheduling changes publish a PHI-free `SchedulingAvailabilityChangedEvent` to
the `scheduling-availability` Service Bus topic. The independent Zocdoc consumer
reconciles only the affected provider and dates. A Zocdoc outage is recorded for
retry and never rolls back the local appointment transaction.

Provider, location, and appointment-type/visit-reason mappings must be active.
Unmapped slots are omitted and recorded in tenant-scoped diagnostics available
to administrators:

```text
GET  /api/scheduling-integrations/zocdoc/availability/status
POST /api/scheduling-integrations/zocdoc/availability/reconcile?from=...&to=...&providerId=...
```

Both routes require authenticated tenant context. Metrics from the
`CloudDentalOffice.Scheduling.Zocdoc` meter cover attempts, successes, failures,
mapping skips, and API latency without patient information.

## Appointment lifecycle synchronization

The lifecycle implementation follows Zocdoc's current documented appointment actions:

| CDO change | Zocdoc operation |
| --- | --- |
| Cancelled | `POST /v1/appointments/cancel` with `other_provider_reason` |
| Rescheduled | `POST /v1/appointments/reschedule` with an ISO-8601 practice-local start time |
| Checked in | `PUT /v1/appointments/update-status` with `arrived` |
| No-show | `PUT /v1/appointments/update-status` with `no_show` |

Zocdoc permits `arrived` and `no_show` only after the appointment start, and a
no-show appointment must be no more than two days old. Remote validation errors
are permanent and dead-lettered rather than retried indefinitely.

Locally initiated changes commit first, then publish a PHI-free
`AppointmentLifecycleChangedEvent` to the `appointment-lifecycle` topic. The
Zocdoc consumer performs the remote call asynchronously, so remote downtime does
not roll back the CDO appointment. Transient failures use Service Bus retry;
configuration, authorization, validation, incomplete mapping, and conflict
failures are visible in the dead-letter subscription.

The existing `ExternalAppointmentReference` durably retains tenant, CDO and
Zocdoc appointment IDs, provider/location/visit-reason IDs, pending operation,
last diagnostic, and synchronization state:

```text
Synced -> Pending -> Synced
                    Failed
                    Conflict
```

Incoming webhook changes are applied with source `Zocdoc` and do not publish an
outgoing lifecycle event. The outgoing consumer processes only source
`CloudDentalOffice`. This explicit causation boundary prevents synchronization
loops. When an incoming change disagrees with a different pending local change,
the reference becomes `Conflict` and CDO is not silently overwritten.

Tenant administrators can inspect the persisted state without PHI through:

```text
GET /api/scheduling-integrations/zocdoc/appointments/status
```

## Readiness and reconciliation

The authenticated, tenant-bound operational endpoints are:

```text
GET /api/scheduling-integrations/zocdoc/readiness?probeAuthentication=true
GET /api/scheduling-integrations/zocdoc/reconciliation?staleAfterMinutes=1440
```

Readiness verifies safe configuration, an explicit OAuth/API probe, complete and
valid provider/location/visit-reason mappings, presence of a secret-backed webhook
key, and recorded successful availability and appointment synchronization. It never
returns credentials. Reconciliation returns aggregate, PHI-free counts for dangling
external references, stale availability, failed or stale-pending outbound work,
failed inbound events, and conflicts. Use the existing targeted availability
reconciliation action after correcting mappings or schedules.

An appointment created from a Zocdoc webhook always receives an
`ExternalAppointmentReference`. The current appointment schema does not retain a
separate source-channel field, so a pre-existing CDO appointment that should have
been associated with Zocdoc but has no reference cannot be inferred safely. Compare
partner exports during pilot reconciliation instead of guessing from patient data.

## Sandbox validation

Zocdoc publishes predefined sandbox entities and appointment IDs for endpoint
testing. External tests are isolated from the normal solution and skip cleanly when
credentials are absent:

```bash
export ZOCDOC_SANDBOX_CLIENT_ID='<sandbox client id>'
export ZOCDOC_SANDBOX_CLIENT_SECRET='<sandbox client secret>'
dotnet test src/Services/Zocdoc.IntegrationTests/Zocdoc.IntegrationTests.csproj
```

The automated sandbox suite verifies OAuth, reference data, multiple-location data,
new and existing patient fixtures, and pending, confirmed, booking-failed,
cancelled, no-show, pending-reschedule, rescheduled, and reschedule-failed states.
It also verifies authentication rejection. Local automated tests cover invalid,
malformed, and replayed signed webhooks, unavailable-slot collision handling,
idempotency, tenant isolation, mapping failures, retry classification, and lifecycle
commands without contacting Zocdoc.

State-changing sandbox cases (create/confirm/cancel/reschedule), insurance rejection
fixtures, and `POST /v1/webhook/mock-request` are partner-assisted checklist steps.
They require Zocdoc-issued scopes, a configured callback URL, and shared sandbox
coordination; do not make them unattended CI tests.

## Production operations runbook

Before enabling a tenant:

1. Run the sandbox suite and save test output without environment values.
2. Configure all mappings, then run readiness with authentication probing.
3. Ask Zocdoc to configure the HTTPS webhook URL and verify one mock event end to end.
4. Reconcile a narrow provider/date range and confirm published slots in Zocdoc.
5. Perform one new-patient and one existing-patient booking; verify confirmation and
   durable external references.
6. Exercise cancel, reschedule, arrived, and no-show where partner permissions allow.
7. Start the pilot with one location, a small provider set, and daily reconciliation.

During operation, alert on availability success rate, webhook validation failures,
booking conflicts, outbound failures, API latency, and Service Bus dead-letter count.
The application emits `CloudDentalOffice.Scheduling.Zocdoc` availability counters
and latency plus `CloudDentalOffice.Intake.Zocdoc` webhook received/validation-failed
counters. Appointment created/conflict/failure totals can be derived from the
tenant-scoped persisted event and external-reference states without patient labels.
Service Bus dead-letter depth is an Azure Monitor broker metric, not an in-process
counter. IntakeService additionally emits inbox persisted, publish success/failure,
retry, poison, and oldest-pending-age instruments. Do not add tenant, appointment,
patient, email, or phone values as metric
labels.

For an incident:

1. Disable the tenant integration if signatures, credentials, or tenant routing may
   be compromised; local scheduling remains authoritative.
2. Inspect readiness and reconciliation, then Azure Service Bus active/dead-letter
   counts and sanitized structured logs.
3. Inspect the tenant-scoped internal inbox status endpoint. After correcting the
   broker/configuration issue, requeue a `Failed` record through
   `POST /api/internal/integration-inbox/{id}/retry` using its tenant-bound admin key.
   The admin API returns status/counts only and never returns payloads.
4. Correct credentials or mappings, replay only verified messages, and perform a
   targeted reconciliation. Do not replay raw webhook bodies from logs.
5. Escalate remote correlation IDs and timestamps to Zocdoc; never send PHI unless
   the approved support channel explicitly requires it.

Credential rotation: add the replacement secret version, update the opaque reference
or secret binding, restart instances to clear process token caches, probe OAuth, ask
Zocdoc to rotate the webhook key, validate a signed mock event, then revoke the old
credentials. Sandbox and production credentials must remain separate.

## Reliability and security notes

- Typed clients use bounded 30-second timeouts and the repository standard resilience
  handler. Retried operations are replacement/idempotent or guarded by persisted state.
- Database uniqueness constraints protect tenant/channel external appointments,
  internal/external mappings, provider/date sync state, and webhook event IDs.
- Webhooks are limited to 1 MiB, rate limited, timestamp bounded to five minutes,
  verified over raw bytes with constant-time HMAC comparison, and tenant-routed only
  from trusted route configuration.
- Correctness does not depend on Zocdoc retries: verified events are acknowledged
  only after the inbox transaction commits.
- Service Bus consumers retry transient failures and dead-letter permanent lifecycle
  failures. Availability and appointments remain eventually consistent during a
  remote outage.
- OAuth tokens are memory-only and tenant/environment/client keyed. Client and webhook
  secrets stay in secret-backed configuration and are never returned to the browser.
- Logs and metrics exclude payload bodies, demographics, tokens, and patient identifiers.

```text
Zocdoc
  ↓
verified webhook
  ↓
Durable Inbox
  ↓
ACK
  ↓
publisher
  ↓
Service Bus
  ↓
SchedulingService
```

`Published` is IntakeService's responsibility boundary. Downstream completion remains
observable through SchedulingService idempotency state and Service Bus diagnostics;
there is intentionally no reverse acknowledgement coupling the two services.

The inbox schema is created on service startup using the repository's existing
`EnsureCreated` convention. Rollback requires draining `Received`/`Publishing` records
before deploying the prior image; retain `cdo_intake` until all retained records have
been published or deliberately reconciled.

Configure `IntegrationInbox__AdminClients__{n}__TenantId` and a distinct,
secret-backed `IntegrationInbox__AdminClients__{n}__ApiKey` for operational access.
Do not reuse the public-booking, scheduling-service, or webhook credential.

See [the partner/certification checklist](zocdoc-certification-checklist.md) for the
sign-off artifact used with Zocdoc.

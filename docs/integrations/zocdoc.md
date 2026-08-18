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

After verification, IntakeService publishes a PHI-free event containing only
the tenant, external event ID, appointment ID, and update type. SchedulingService
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
- `503`: Service Bus is unavailable and the event was not accepted. Zocdoc's
  documented retry behavior is based on connection/no-response failures rather
  than HTTP status, so operators should verify delivery and reconcile manually.
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

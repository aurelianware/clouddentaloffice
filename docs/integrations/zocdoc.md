# Zocdoc scheduling integration

CloudDentalOffice's first Zocdoc adapter is a server-to-server integration
boundary for validating connectivity and retrieving external entities used by
the scheduling mapping workflow. It does not yet publish timeslots, create
appointments, or process webhooks.

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
SchedulingCredentials__third-set-smiles-zocdoc__WebhookSecret=<Webhook secret>
```

Never place the values in `appsettings.json`, database rows, source control,
logs, screenshots, or pull-request descriptions. `WebhookSecret` is reserved
for the future signed-webhook implementation and is not consumed by this PR.

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

## Deliberately deferred

- Zocdoc appointment creation, confirmation, cancellation, or rescheduling
- signed webhook receipt and HMAC-SHA256 verification
- credential rotation and production certification automation

These must build on the canonical availability and booking boundaries rather
than adding Zocdoc fields to core scheduling entities.

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

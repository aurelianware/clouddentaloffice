# Scheduling service trust boundary

```text
Internet
   │
   ├─ 3rdSetSmiles
   │
   └─ Zocdoc webhook
          ↓
      IntakeService
          ↓
  authenticated internal messaging/API
          ↓
    SchedulingService
```

SchedulingService and API Gateway use internal-only Azure Container Apps ingress.
Kubernetes exposes SchedulingService only as a `ClusterIP`. Network isolation is
defense in depth: every data-bearing HTTP route also authenticates and authorizes
the caller. Tenant IDs are derived from validated JWT claims or a tenant-bound
service credential, never from query strings, route values, or webhook bodies.

## Route-security matrix

| Route | Service | Exposure | Authentication | Authorization | Tenant source | Caller |
|---|---|---|---|---|---|---|
| `GET /health` | SchedulingService | Internal; safe probe | None | Liveness only; returns `Healthy` | None | Container platform |
| `/api/appointments*` | SchedulingService | Internal | JWT bearer | Authenticated tenant staff | Validated `tenant_id` claim | Portal through private gateway |
| `/api/booking-requests*` | SchedulingService | Internal | JWT bearer | Authenticated tenant staff | Validated `tenant_id` claim | Portal through private gateway |
| `/api/scheduling-integrations/*` | SchedulingService | Internal | JWT bearer | `Admin` role plus tenant claim | Validated `tenant_id` claim | Portal integration administration |
| `/api/internal/public-scheduling/*` | SchedulingService | Internal | `X-CDO-Service-Key` | Configured client credential | Credential-to-tenant mapping | IntakeService only |
| `GET /health` | IntakeService | Public | None | Liveness only; returns `Healthy` | None | Container platform |
| `/api/public/availability` | IntakeService | Public by design | Website API key | Enabled tenant client | Credential-to-tenant mapping | 3rdSetSmiles server-side function |
| `/api/public/booking-requests` | IntakeService | Public by design | Website API key | Enabled tenant client | Credential-to-tenant mapping | 3rdSetSmiles server-side function |
| `/api/integrations/zocdoc/{integrationId}/webhooks` | IntakeService | Public by design | Zocdoc HMAC signature | Enabled integration route | Trusted route configuration | Zocdoc |
| `/api/internal/integration-inbox/*` | IntakeService | Public ingress, operational | Tenant admin API key | Tenant status/retry only; rate limited | Credential-to-tenant mapping | Authorized operator |

Appointment IDs are not authorization capabilities. Single-record reads include
the authenticated tenant in the database predicate and return `404` for another
tenant's identifier. Scheduling integration APIs require Admin even when reached
from the private network. Missing JWT or service-key configuration fails closed.

The intentionally public routes never expose internal tenant metadata, integration
secrets, raw webhook bodies, appointment records, or PHI-bearing diagnostics.

# Portal downtime runbook — portal.3rdsetsmiles.com

The staff portal is the Blazor Server app in `src/CloudDentalOffice.Portal`,
deployed as the `portal` Azure Container App (external HTTPS ingress, target
port 5000) and reached by staff at **https://portal.3rdsetsmiles.com**. This
runbook is for "the portal is down / unreachable" reports.

The single most useful first step is to decide **which layer is failing**: the
application replicas, or the custom-domain / ingress layer in front of them.
They fail independently and have completely different fixes.

## 1. Triage: is the app down, or just the custom domain?

Compare the customer-facing custom domain against the Container App's own
default hostname. Both terminate at the same environment ingress and route to
the same `portal` app, so if one works and the other does not, the fault is in
the **custom-domain binding**, not the application.

```bash
# Customer-facing custom domain
curl -sS -o /dev/null -w 'custom : HTTP %{http_code}\n' https://portal.3rdsetsmiles.com/

# Container App default hostname (get the current value from Azure)
default_fqdn=$(az containerapp show --name portal --resource-group "$RESOURCE_GROUP" \
  --query properties.configuration.ingress.fqdn -o tsv)
curl -sS -o /dev/null -w "default: HTTP %{http_code}  ($default_fqdn)\n" "https://$default_fqdn/"
```

Interpret the pair:

| custom domain | default hostname | Meaning | Go to |
|---|---|---|---|
| `503` / TLS reset / `SSLV3`/`SYSCALL` | `401` or `302` | App is **up**; custom-domain binding or managed cert is broken | [§2](#2-custom-domain--certificate-is-broken-app-is-up) |
| `503` | `503` | App replicas are **unhealthy** (crash loop, DB init, bad image) | [§3](#3-application-replicas-are-unhealthy) |
| `401` / `302` | `401` / `302` | Portal is actually **serving** — this is the Google login gate, not an outage | [§4](#4-portal-is-serving-401302-is-expected) |

Notes on expected codes: unauthenticated requests to a healthy portal are
answered by Container Apps EasyAuth. Browsers get a `302` redirect to Google;
header-light requests (curl/openssl) get `401`. A `503` body reading
`upstream connect error or disconnect/reset before headers ... remote
connection failure` comes from the **ingress (Envoy)**, meaning it could not
reach a healthy upstream for that hostname.

## 2. Custom domain / certificate is broken (app is up)

This is the case where the default hostname answers (`401`/`302`) but
`portal.3rdsetsmiles.com` returns `503` or fails the TLS handshake. The domain
binding and/or its managed certificate on the ingress has been lost or has
entered a failed state.

Check the binding state:

```bash
az containerapp hostname list --name portal --resource-group "$RESOURCE_GROUP" -o table
# Look for portal.3rdsetsmiles.com with bindingType=SniEnabled and a bound,
# non-expired managed certificate.
```

Re-bind the hostname and managed certificate. This is exactly the
`Bind production custom domains` step in `.github/workflows/deploy-aca.yml`, so
**re-running the `deploy-aca` workflow** performs the same remediation:

```bash
environment_name="$(basename "$ENVIRONMENT_ID")"   # e.g. from the infra outputs

az containerapp hostname bind \
  --name portal \
  --resource-group "$RESOURCE_GROUP" \
  --environment "$environment_name" \
  --hostname portal.3rdsetsmiles.com \
  --validation-method CNAME
```

If binding fails validation, confirm the Cloudflare DNS records still exist —
the platform needs both:

- `CNAME  portal.3rdsetsmiles.com  ->  <portal default hostname>`
- `TXT    asuid.portal.3rdsetsmiles.com  ->  <Container App verification id>`

Get the verification id with
`az containerapp env show --name "$environment_name" --resource-group "$RESOURCE_GROUP" --query properties.customDomainConfiguration.customDomainVerificationId -o tsv`.

Verify recovery:

```bash
curl -sS -o /dev/null -w 'HTTP %{http_code}\n' \
  'https://portal.3rdsetsmiles.com/.auth/login/google?post_login_redirect_uri=%2F'
# Expect a 302 redirect to https://accounts.google.com/...
```

## 3. Application replicas are unhealthy

Both hostnames return `503`. The `portal` app has no healthy replica.

```bash
# Current replica / revision health
az containerapp replica list --name portal --resource-group "$RESOURCE_GROUP" -o table
az containerapp revision list --name portal --resource-group "$RESOURCE_GROUP" -o table

# Recent container logs (look for startup exceptions)
az containerapp logs show --name portal --resource-group "$RESOURCE_GROUP" --tail 200
```

Common causes and fixes:

- **Database unreachable at startup.** The portal applies its schema at boot.
  A production DB init failure is logged but **no longer crash-loops the
  process** (see `Program.cs`): the readiness probe `/health/ready` reports
  unhealthy while the database is unreachable, so the ingress holds traffic and
  the replica recovers once PostgreSQL is back. Check the Postgres flexible
  server health and the `conn-default` secret if `/health/ready` stays red.
- **Bad image / crash on boot.** Roll back to the previous good revision:
  `az containerapp revision copy` from the last healthy revision, or redeploy a
  known-good `imageTag`.
- **Cold start under load.** `minReplicas` is 1; confirm it has not been scaled
  to 0.

## 4. Portal is serving (401/302 is expected)

Both hostnames return `401`/`302`. The portal is healthy and the response is the
Google staff-authentication gate, not an outage. If a specific user "can't get
in", it is an **authorization** issue, not downtime — confirm their Google
address is in `StaffAuth:Users` (`infrastructure/azure/container-apps.bicep`);
an allowed Google account that is not on the staff allowlist is answered with
`403 This Google account is not authorized for CloudDentalOffice.`

## Health probes

The portal exposes two probe endpoints (anonymous, and exempt from the staff
allowlist so the platform can reach them):

- `GET /health/live` — process liveness; runs no dependency checks, so a
  transient database outage never restarts a healthy replica.
- `GET /health/ready` — readiness; healthy only when the database is reachable,
  so the ingress routes traffic only to replicas that can serve requests.

These back the `Liveness`/`Readiness` probes on the `portal` Container App
(`infrastructure/azure/container-apps.bicep`). To inspect readiness directly,
port-forward to a replica or hit the endpoint from another app in the
environment — externally, `/health/*` is behind the same ingress and EasyAuth
as the rest of the site.

## Prevention

- The custom-domain bind is codified in `deploy-aca.yml`; re-running that
  workflow re-binds the hostname + managed certificate.
- Managed certificates renew automatically, but a failed renewal presents as
  §2. If this recurs, add a scheduled synthetic check against
  `https://portal.3rdsetsmiles.com/.auth/login/google` and alert on anything
  that is not a `302` to Google.

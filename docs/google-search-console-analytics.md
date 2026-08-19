# Google Search Console acquisition analytics

CloudDentalOffice imports aggregate Google Search Console data and compares it with aggregate booking outcomes:

`Search Console → daily query/page/device import → canonical page normalization → patient-acquisition report → page-level booking comparison`

Google Search Console provides aggregate search performance data. CloudDentalOffice compares Search Console metrics with aggregate acquisition and scheduling metrics by page and reporting period. The system does not identify the search query used by an individual patient. Search rows never contain patient, booking-request, appointment, or acquisition-session identifiers.

## Configuration and authentication

Create a Google service account, grant its email read access to the exact Search Console property, and store its email/private key in the deployment secret store. Do not commit its JSON key. Container Apps accepts `SEARCH_CONSOLE_SERVICE_ACCOUNT_EMAIL` and `SEARCH_CONSOLE_PRIVATE_KEY` repository secrets and exposes them to SchedulingService under the opaque credential reference `third-set-smiles`. Other tenants can use their own `SearchConsoleCredentials:<reference>` environment/Key Vault configuration.

An authorized SchedulingService admin configures `Enabled`, the exact HTTPS property URL, the credential reference, and canonical host through `PUT /api/admin/search-console`. `POST /api/admin/search-console/sync?backfill=true` requests a bounded backfill. Neither endpoint returns credentials. Disabling the integration is the operational kill switch.

For 3rd Set Smiles, the canonical website host is `www.3rdsetsmiles.com` and the candidate URL-prefix property is `https://www.3rdsetsmiles.com/`; deployment must use the property that is actually verified in Search Console. No credential or assumed property access is committed.

## Import behavior

The durable schedule lives in `SearchConsoleIntegrations.NextSyncAt`; a leased worker scans due integrations, and an Azure Container Apps cron scaler wakes SchedulingService daily. Normal sync repairs the most recent seven days because Google can revise data. Initial/manual backfill defaults to 90 days. Data through two days before the run is requested as final data, reflecting Search Console reporting delay.

The client requests `date`, `query`, `page`, and `device`, follows `startRow` pagination, uses bounded retry only for rate limits and transient Google failures, and caps each day at 50,000 rows by default. Dashboard totals therefore reflect the aggregate rows returned and retained under that configured cap; Search Console may also omit anonymized queries. Authentication, permission, and invalid-property failures do not retry indefinitely. Previous imported data remains reportable while status is degraded.

Imports upsert by `(TenantId, Date, Query, PagePath, Device)`. Full URLs are reduced to canonical trailing-slash paths. `http`, apex-domain, and `www` variants of the configured canonical host roll up together. `/hero-demo/*` and other hosts remain stored for historical diagnosis but are classified non-production and excluded from current conversion comparisons.

## Reporting semantics

The Admin-only Patient Acquisition dashboard shows impressions, clicks, aggregate CTR (`clicks / impressions`), impression-weighted average position, daily trends, top queries, query/page rows, device metrics, and landing-page booking comparisons. “Requests per search click” is an aggregate page/date-range ratio, not deterministic Google conversion attribution. Search Console excludes Google Ads and Google Business Profile-specific reporting.

Operational status includes enabled state, property, last attempt/success, latest imported date, sync status, and a sanitized error code. Logs include tenant, property, date window, and row count but not credentials, patient identity, or bulk raw queries.

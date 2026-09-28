# Stedi integrated accounts: current state

**Purpose.** Baseline for making Cloud Dental Office (CDO) a published Stedi app under Stedi's integrated accounts model. In that model each practice (tenant) has its own production Stedi account, installs our app, and we hold a Stedi API key scoped to that practice.

**Snapshot.** 2026-09-26, CDO `origin/main` at `2c92270`, with the eligibility rows updated for #75 (`4b80949`), which merged afterwards. File references are to those commits. For the Cloud Health Office (CHO) side, `aurelianware/cloudhealthoffice` `main` after #1204. This document is read-only discovery; the changes made in response are in [credentials.md](credentials.md).

**Target.** Every Stedi call uses the calling tenant's own credentials, resolved at runtime, with no shared key except an explicit, flagged fallback.

---

## 1. Where CDO talks to Stedi or a clearinghouse

**CDO never calls Stedi directly.** No code references Stedi; the word appears only in `docs/architecture/claim-lifecycle.md` and a test that asserts vendor names never reach staff. Payer traffic goes one of three ways:

- to CHO over HTTP,
- to a per-payer SFTP host,
- to nowhere (stubs).

| Transaction | Path today | Code | Authentication |
|---|---|---|---|
| **837D claim submission** | CDO → CHO `POST {InsurancePlan.ApiEndpoint}api/v1/claims/import/raw837` (multipart X12) | `ClaimServiceImpl.SubmitClaimAsync` (`Services/ClaimServiceImpl.cs:150`) → `EdiSubmissionService` (`Services/EdiSubmissionService.cs:39`, routes on `InsurancePlan.EdiSubmissionType`) → `CloudHealthOfficeApiService.SubmitClaimAsync` (`Services/CloudHealthOfficeApiService.cs:40`, POST at :95); X12 built by `EdiX12Service` (:63) | `X-Tenant-ID` = claim tenant (:73), plus `Authorization: Bearer` or `X-API-Key` (:82/:86). The key comes from **per-payer DB column** `InsurancePlan.ApiKeyEncrypted` |
| **837D over SFTP** | CDO → per-payer SFTP host (clearinghouse or payer) | `EdiSftpService` (`Services/EdiSftpService.cs:26`) | `InsurancePlan.SftpUsername`, plus `SftpPasswordEncrypted` or `SftpSshKeyEncrypted` (per-payer DB columns). **Host key checking is disabled** (`e.CanTrust = true`, :75, :153) |
| **Payer connection test** | API: `GET health/live` on the payer's CHO endpoint. SFTP: connect | `EdiSubmissionService.TestPayerConnectionAsync` (:119), `CloudHealthOfficeApiService.cs:133-158` | Same as the rows above |
| **270/271 eligibility (since #75)** | CDO → CHO provider eligibility API `POST /api/v1/eligibility/check` → CHO's Stedi account. It is selected per payer by `PayerConnectivity__Payers__{payerId}__Eligibility=CloudHealthOffice` | `EligibilityRequestBuilder` and `CloudHealthOfficeEligibilityClient` (`Services/CloudHealthOfficeEligibility.cs`); `CloudHealthOfficeTradingPartnerAdapter` (`Services/PayerConnectivity.cs`); UI `Components/TreatmentPlanInsuranceEstimate.razor` | `X-Api-Key` from the **global** `CloudHealthOffice:Eligibility:ApiKey` (Container Apps secret `cho-eligibility-api-key` ← GitHub secret `CHO_ELIGIBILITY_API_KEY`) plus `X-Tenant-ID`. CHO binds that key to a tenant allow-list, then uses **its single Stedi key** |
| Eligibility stubs | `Mock` adapter (Development only); `src/Services/EligibilityService` returns a hard-coded response (TODO) | `Services/PayerConnectivity.cs`; `src/Services/EligibilityService/Program.cs:12-37` | None |
| **277CA / 276/277 / 835 (claim lifecycle)** | Read from CHO's claim intelligence read model. CDO does no 276/277 of its own, parses no 277CA and ingests no 835 (`docs/PAYER_CONNECTIVITY.md:41`) | `ClaimIntelligenceClient.GetAsync` (`Services/ClaimIntelligenceService.cs:73-120`) → `GET {IntelligenceBaseUrl or BaseUrl}/api/claims/{claimId}/intelligence`. Refresh runs after submit and on demand (`ClaimServiceImpl.cs:192,242`); there is no poller | `X-Tenant-ID` + `X-API-Key` from the **global** `CloudHealthOffice:ApiKey` (:88-90) |
| **Treatment estimates** (not HIPAA X12; included because it shares the credential) | CDO → CHO `POST /api/v1/adjudication/estimate` | `CloudHealthOfficeInsuranceEstimateService` (`Services/InsuranceEstimateService.cs:74,99-106`) | `X-Tenant-Id` + `X-Api-Key` from the **global** `CloudHealthOffice:ApiKey` |
| **835 / ERA** | Stub only | `src/Services/EraService/Program.cs:12-34` (TODOs, no auth); Portal feature flag `Era:Enabled=false` (`appsettings.json:151`) | None |
| **Claims microservice** | Stub: marks a claim Submitted without sending anything | `src/Services/ClaimsService/Program.cs:78-92` (TODO :85) | None |
| **Payer enrollment** | **None.** No enrollment code or data model (ERA or claims) | n/a | n/a |
| **Inbound webhooks** | **None for clearinghouse, claims or ERA.** The only webhooks are Stripe (`src/Services/IntakeService/StripeWebhooks.cs:82`) and Zocdoc (`IntakeService/Program.cs:219`) | n/a | n/a |
| **Dead code** | `EdiService` targets `EdiApi:BaseUrl` (default `https://edi.cloudhealthoffice.com/api`) for 837, eligibility, claim status, 834 and 835. It is registered (`Program.cs:382`) but never injected | `Services/EdiService.cs` | `X-API-Key` from global `EdiApi:ApiKey` |

Unused shared X12 code: `src/Shared/CloudDentalOffice.EdiCommon` (`Claim837DGenerator`, `X12Parser`) is referenced by the Claims, Era and Eligibility services but not called.

### What happens on the CHO side (relevant to Stedi)

CHO's Stedi gateway (`CloudHealthOffice.Infrastructure/Gateways/Stedi`) is where Stedi calls happen. It holds **one Stedi API key per deployment**, `HealthcareTransactions:Gateways:Stedi:ApiKey` (`StediGatewayOptions.cs:45`), with no per-tenant key. The new `provider-eligibility` app reads it from Key Vault secret `provider-eligibility-stedi-api-key` (`infrastructure/azure/provider-eligibility-container-app.bicep`). Tenant id flows through to the gateway, but only for payer overlays and logging, not for credential selection.

---

## 2. Where credentials come from today

| Credential | Scope | Source |
|---|---|---|
| `CloudHealthOffice:ApiKey`: estimates and claim intelligence | **Global**, one per deployment | Container Apps secret `cloudhealthoffice-api-key` ← Bicep param ← GitHub secret `CHO_ESTIMATE_API_KEY` (`infrastructure/azure/container-apps.bicep:101,138`). Absent from `appsettings.json` |
| `CloudHealthOffice:BaseUrl` | Global | `apps.bicep:77` default: the `benefit-plan-estimate` app. The workflow doesn't override it. Claim intelligence therefore targets the estimate-only app, which hides every other path, so **intelligence calls probably return 404 in production** |
| `CloudHealthOffice:Eligibility:ApiKey`: eligibility via CHO (#75) | **Global** | Container Apps secret `cho-eligibility-api-key` ← GitHub secret `CHO_ELIGIBILITY_API_KEY`. The URL comes from GitHub variable `CHO_ELIGIBILITY_BASE_URL`; payer routes from `CHO_ELIGIBILITY_PAYER_IDS` |
| `InsurancePlan.ApiKeyEncrypted` / `ApiEndpoint` / `ApiAuthType` | **Per tenant, per payer** (`InsurancePlan` is an `ITenantEntity`) | Entered by staff in `Pages/PayerConfigDialog.razor`. "Encryption" is **Base64 only** (`EncryptValue`, :294-302, "NOT SECURE"). Decryptors at `PayerConfigDialog.razor:305`, `CloudHealthOfficeApiService.cs:168-183` and `EdiSftpService.cs:204-221` return the input unchanged if it isn't Base64. Data Protection is not configured |
| `InsurancePlan.Sftp*` (password or SSH key) | Per tenant, per payer | Same Base64 scheme |
| `EdiApi:ApiKey` (dead code) | Global | Empty in `appsettings.json:58`; **placeholder value hard-coded in `appsettings.Development.json:13`** |
| Stedi key | **None in CDO** | Lives only in CHO, as one global key (see above) |

Also noted: `Program.cs:246` has a hard-coded fallback SQL Server connection string with an `sa` password. It is a development fallback, but it is committed.

---

## 3. How tenancy works

### Model
- **`Organization`** (`Models/Organization.cs:9`): the tenant record.
  - `TenantId` is a string, defaulting to a GUID string (:18).
  - Also holds `AzureAdTenantId`, `Domain` (informational), `Plan`, Stripe ids, `IsActive` and `TrialExpiresAt`.
  - `Settings` (:70) is a free-form JSON string that nothing reads.
- **`TenantRegistry`** (`Models/TenantRegistry.cs:5`): keyed by `TenantId`.
- **Tenant id** is a free-form string. Production uses `third-set-smiles`, seeded by `InitialTenantBootstrap.cs` from `InitialTenant__*` (`container-apps.bicep:121-124`). The default is `demo` (`Services/Tenancy/TenantConstants.cs:5`).
- **`ITenantEntity`** (`Models/ITenantEntity.cs:3`) is the per-row marker.
- **No routing fields:** there is no slug, subdomain or custom-domain field used for routing. Custom domains are bound in `deploy-aca.yml:260-294` only; no code maps a host to a tenant.

### Existing per-tenant settings patterns (reusable)
- **`CredentialReference`:** a table row per (tenant, provider) stores the *name* of a configuration key; the secret itself is read from `IConfiguration`.
  - Stripe: `PaymentProcessorConfiguration` (`Models/PatientPayment.cs:115`), resolved in `Services/StripeConnect.cs:62-76`.
  - Zocdoc: `SchedulingIntegrationConfiguration` (`SchedulingService/Integrations/SchedulingIntegrationEntities.cs:11`), resolved via `SchedulingCredentials:{ref}`.
  - Search Console: `SearchConsoleIntegration`.
- **`ReviewOutreachSettings`:** one row per tenant.
- **`InsurancePlan`:** per-payer endpoint and key fields, with weak encryption (see §2).

### How a request resolves its tenant
- **Production (Container Apps EasyAuth with Google):**
  - `StaffAccessMiddleware` (`Services/Auth/ContainerAppsStaffIdentity.cs:87`, `Program.cs:563`) parses `X-MS-CLIENT-PRINCIPAL` and checks the `StaffAuth:Users` allowlist.
  - It stamps **one global `StaffAuth:TenantId`** onto every staff user (:43, :53-54). **A deployment therefore serves exactly one tenant today.**
- **Blazor circuit:** `BlazorTenantProvider` (`Program.cs:241`) reads the claims `tenant_id` → `tenantId` → `tid` → `tenant` (`Services/Tenancy/BlazorTenantProvider.cs:48-51`).
  - It falls back to `demo` when unauthenticated, when auth isn't ready yet (:63-67), and silently on any exception (:77-81).
- **Non-EasyAuth fallback:** `PortalAuthStateProvider` reads a JWT from ProtectedLocalStorage and checks expiry only, not the signature (`Services/Auth/PortalAuthStateProvider.cs:45-51`). The storage is encrypted with Data Protection, which limits tampering.
- **Unregistered provider:** `HttpContextTenantProvider` trusts `X-Tenant`/`X-Tenant-Id` headers before claims, but it is not registered in DI.
- **Azure AD path:** maps the AAD `tid` to an Organization (`Program.cs:105-230`). It is disabled in Container Apps.

### Data isolation
- **Portal EF Core:**
  - Global query filter `TenantId == CurrentTenantId` (`Data/CloudDentalDbContext.cs:98-454`).
  - `ApplyTenantId` fills a blank `TenantId` on save (:711-729); an explicitly set value is trusted.
  - `Organization`, `TenantRegistry` and `ProcedureCode` are unfiltered.
  - Column default is `demo` (:737).
- **Microservices** (Patient, Scheduling, Intake) have no global filters; each query adds `Where(x => x.TenantId == tenantId)`.

### Tenant across services
- **Portal → YARP gateway (no auth) → services:**
  - `SchedulingTenantAuthorizationHandler` (`Services/SchedulingIntegrationAdminClient.cs:178-207`) mints a 2-minute HS256 JWT with `tenant_id`, signed with the shared `Jwt:Key`.
  - Services validate it: `PatientTenant.Of`, `StaffAuth.Tenant`, `VisionAuth`, `SchedulingIntegrationAdminApi.TenantId`.
- **Service keys:** `X-CDO-Service-Key` maps to a tenant through config arrays (`InternalApi:PublicIntakeClients`, `InternalApi:Clients`, `PublicBooking:Clients`). Only index `0` is populated.
- **No tenant concept:** EligibilityService, ClaimsService, EraService, ApiGateway.
- **Outbound to CHO:** `X-Tenant-ID` header plus the global key. Whether CHO binds the estimate and intelligence key to that tenant is not enforced on the CDO side (the new CHO eligibility API does enforce it).

### Background work

| Worker | Kind | Tenant source |
|---|---|---|
| `ReviewOutreachWorker` | Timer | Iterates all tenants (`IgnoreQueryFilters`); tenant from the row |
| `PatientBillingNotificationWorker` | Timer | All tenants; row `TenantId` |
| `StripePaymentWebhookConsumer` | Service Bus | Payload `TenantId`. IntakeService maps Stripe `account` → tenant via config `StripeWebhooks:Accounts` |
| `IntegrationInboxWorker` (Intake) | Outbox timer | Row `TenantId` |
| `BookingRequestConsumer` (Scheduling) | Service Bus | Payload `evt.TenantId` |
| Zocdoc consumers (availability, webhook, lifecycle) | Service Bus | Payload `evt.TenantId` |
| `SearchConsoleSyncWorker` | Timer | Iterates enabled integration rows |

Tenant always travels in the payload or the row, never in Service Bus `ApplicationProperties`. Nothing in the claim or eligibility paths runs in the background.

---

## 4. How secrets reach Azure Container Apps

**Infrastructure** (`infrastructure/azure/main.bicep`):
- One user-assigned identity, `cdo-identity`, whose **only role is AcrPull** (:46-53).
- There is **no Key Vault**, storage account or other role assignment.

**Container Apps** (`infrastructure/azure/container-apps.bicep`):
- Every secret is a plain `secrets: [{ name, value }]` entry fed from `@secure()` parameters. `keyVaultUrl` is never used.
- Portal secrets: `conn-default`, `jwt-key`, `google-oauth-client-secret` and `cloudhealthoffice-api-key` (:97-102).
- Other services use `secretRef` for connection strings, `jwt-key`, Service Bus keys, internal API keys, `search-console-private-key`, `zocdoc-webhook-secret` and others.

**App code:**
- Only the Portal wires Key Vault: `AddAzureKeyVault(KeyVault:VaultUri, DefaultAzureCredential)` outside Development (`Program.cs:289-298`).
- It is inactive: `KeyVault:VaultUri` is never set, `AZURE_CLIENT_ID` isn't set for the user-assigned identity, and it is added after some configuration has already been read.
- All other services read environment variables only.

**Deployment** (`.github/workflows/deploy-aca.yml`):
- OIDC login (`azure/login@v2`).
- Deploys `main.bicep`, reads Service Bus keys with `az servicebus … keys list` (:205-225), then passes **GitHub secrets as inline `--parameters`** to `apps.bicep` (:230-252).
- Secret names: `POSTGRES_ADMIN_PASSWORD`, `JWT_KEY`, `PUBLIC_BOOKING_API_KEY`, `PATIENT_SERVICE_API_KEY`, `PUBLIC_SCHEDULING_SERVICE_API_KEY`, `PUBLIC_AVAILABILITY_SLOT_KEY`, `GOOGLE_OAUTH_CLIENT_ID`, `GOOGLE_OAUTH_CLIENT_SECRET`, `SEARCH_CONSOLE_SERVICE_ACCOUNT_EMAIL`, `SEARCH_CONSOLE_PRIVATE_KEY`, `CHO_ESTIMATE_API_KEY`, plus the Azure and ACR identifiers.
- `zocdocWebhookSecret` and `integrationInboxAdminApiKey` are not passed, so they deploy empty.
- There is one environment (`cdo-prod-rg`).

**Consequence:** adding a per-tenant secret today means editing the Bicep and the workflow and redeploying. There is no runtime secret store to write to when a practice installs the app.

---

## 5. Logging that could write PHI or API keys

| Severity | Location | Risk |
|---|---|---|
| **High** | `Services/CloudHealthOfficeApiService.cs:119-122` | Logs CHO's raw error body and puts it in the exception message. That message is logged again (`EdiSubmissionService.cs:113,195`; `ClaimServiceImpl.cs:202`) and shown in the UI. A validation error could echo 837 content (names, dates of birth, member ids) |
| **High** | `InsurancePlan` `*Encrypted` columns | Base64 only: anyone with database or backup access has plaintext payer API keys, SFTP passwords and SSH keys |
| **Medium (dev only)** | `Program.cs:257` | `EnableSensitiveDataLogging()` when using SQLite in Development, with `Default: Debug`, logs EF parameter values, including `PatientInsurance.SubscriberSSN`. The `Database:EnableSensitiveDataLogging` settings are never read |
| Medium | `Services/EdiSftpService.cs:121,137,150-152,198` | Logs the SFTP username, password *length* (Debug), host key fingerprints, and exception messages that can include host or user details |
| Medium | `Services/EdiService.cs:80,90,202,212` | Logs `MemberId` (PHI). The code is dead but still compiled |
| Low | `EdiSubmissionService.cs:109`, `ClaimServiceImpl.cs:212`, `Program.cs:524-525` | Exception messages and stack traces; could contain connection details |
| OK | `PayerConnectivity.cs:47-56` audit sink; `EdiX12Service.cs:140`; estimate and intelligence clients | IDs, status and counts only; no raw X12 logged |

**Sanitization:**
- Current code has only local CR/LF stripping (`ClaimIntelligenceService.cs:338`), and it isn't applied to the EDI, SFTP or CHO submit logs.
- The `LogSanitizer` from the log-forging fix exists only on unmerged branches (`origin/alert-autofix-4`, `origin/copilot/sub-pr-4`). It also only escapes control characters, and masks neither PHI nor secrets.

**Telemetry:** no Serilog, OpenTelemetry or Application Insights SDK in code. Default `IHttpClientFactory` logging records request URIs only; URIs currently carry no PHI.

---

## Gaps against the target

1. **Stedi credentials live in CHO as one global key.** CHO's Stedi gateway has a single `ApiKey`, and neither CDO nor CHO has any notion of a per-practice Stedi key or of selecting credentials by tenant at call time.
2. **No per-tenant integration or credential store for Stedi.** There's no table or model for "this practice's Stedi account/key". The `CredentialReference` pattern (Stripe, Zocdoc, Search Console) is the closest fit, but it resolves secrets from static configuration.
3. **No runtime secret store.** CDO has no Key Vault. Its identity is AcrPull-only, the Portal's Key Vault hook is inactive, and every secret is a Bicep parameter from GitHub. An app install can't store a new practice's key without a redeploy.
4. **A deployment serves one tenant.** `StaffAuth:TenantId` is a single global value, service-key maps only populate index 0, and nothing maps a host to a tenant. Multi-practice SaaS requires fixing tenant resolution before per-tenant credentials matter.
5. **Existing per-payer secrets are not protected.** `InsurancePlan` keys, passwords and SSH keys are Base64-encoded, and SFTP host keys aren't verified.
6. **Global CHO key for estimates and claim intelligence.** It is sent with a caller-chosen `X-Tenant-ID`, and CDO can't show that CHO binds the key to the tenant. Claim intelligence also appears to point at the estimate-only host.
7. **Most transactions don't exist yet.** Eligibility exists only through CHO (#75). There's no 276/277, no 835 ingestion, no payer enrollment, and no inbound clearinghouse webhooks, so a per-tenant design can be built in rather than retrofitted.
8. **No explicit, flagged fallback.** Where a shared key exists (CHO's global key, CHO's single Stedi key), it is the only path. It is neither flagged nor logged as a fallback.
9. **PHI and credential logging risks** (§5) need fixing before practice-owned keys and more payer traffic flow through these paths, especially the raw CHO error bodies.

## Could not determine

- Actual deployed values: whether `CloudHealthOffice:ApiKey` is set, and whether a Key Vault exists outside this repo.
- Whether the Portal calls AuthService in production.
- Whether CHO's claim-import and estimate endpoints validate `X-Tenant-ID` against the API key.
- What CHO's `raw837` error bodies contain in practice.

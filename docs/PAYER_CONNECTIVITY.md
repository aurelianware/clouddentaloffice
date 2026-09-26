# Payer connectivity foundation

CloudDentalOffice is clearinghouse-first, but not clearinghouse-dependent. Staff workflows depend on normalized application services rather than clearinghouse, payer, FHIR, or X12-specific APIs.

```text
CloudDentalOffice
     |
     v
Payer Transaction Router
     |
 +---+-------------------+
 |                       |
 v                       v
CloudHealthOffice    Clearinghouse
                         |
                         v
                       Payer
```

Each payer can route transactions independently. For example, eligibility may use a clearinghouse while payment estimates use CloudHealthOffice. Estimate routes are ordered: an authoritative payer or payer AEOB adapter may be attempted before the CloudHealthOffice simulation. The router returns one result and its source; it never combines amounts from multiple sources.

Adapters declare capabilities. Eligibility and estimate interfaces are separate so an adapter is not forced to pretend it supports transactions it cannot perform. Current implementations are:

- `Mock`: development/test normalized eligibility only; makes no external call.
- `CloudHealthOffice`: prospective payment estimates through the server-side adapter from PR #20, and real-time eligibility through CHO's provider eligibility API when that is configured (see below).
- Clearinghouse/direct payer: not configured until real credentials and specifications are supplied.

Example environment configuration:

```text
PayerConnectivity__Payers__PAYER1__Eligibility=Mock
PayerConnectivity__Payers__PAYER1__PaymentEstimate__0=DirectPayer
PayerConnectivity__Payers__PAYER1__PaymentEstimate__1=CloudHealthOffice
PayerConnectivity__Payers__PAYER1__ClaimSubmission=Clearinghouse
```

Eligibility results normalize coverage status, effective dates, plan details, deductible/maximum values, service benefits, messages, source, verification time, and operational correlation metadata. Raw X12 is not exposed to the UI.

When a real 270/271 adapter is added, HIPAA syntax must remain behind dedicated `270 mapper → adapter → 271 parser` boundaries. ISA/GS/ST/EB knowledge must not enter the portal UI or business services. Operational audit records contain correlation ID, tenant, payer, adapter, transaction type, timestamps, status, and elapsed time; they never contain member IDs, names, or raw X12.

## Real-time eligibility through CloudHealthOffice

CDO sends eligibility checks to CHO's provider eligibility API (`provider-eligibility-api` in the CHO repo), which runs the 270/271 through CHO's clearinghouse and returns a normalized answer. CDO never sees X12.

```text
Portal → PayerTransactionRouter → CloudHealthOffice adapter
       → POST {Eligibility:BaseUrl}/api/v1/eligibility/check   (X-Api-Key, X-Tenant-ID)
       → CHO → clearinghouse → payer
```

**What is sent.** Payers match on member ID plus the policyholder's name and date of birth. `EligibilityRequestBuilder` builds the request from the patient and the selected coverage:

- Relationship `Self` (or blank): the patient's own record is the subscriber.
- `Spouse`, `Child`, anything else: the coverage's subscriber first name, last name and date of birth are the subscriber, and the patient is sent as a dependent. The check is refused before any call when those subscriber fields are missing.
- The coverage's member ID is sent as the subscriber's member ID. Dental plans usually identify dependents by the policyholder's ID; a separate dependent ID is not stored today.

**What comes back.** Coverage status, plan name, coverage dates, the annual maximum and deductible (with remaining amounts) and per-benefit lines. Dental plans report the annual maximum as a limitation; lifetime and orthodontic maxima are not shown as the annual maximum. A payer rejection (for example "subscriber not found") is shown as a result, not an error.

**Failures** are split so staff know whether to fix something:

| CHO status | Staff sees |
| --- | --- |
| 400 | Which insurance fields to review (names only, never values) |
| 422 | Payer ID not recognized, enrollment required, or payer not supported |
| 503 | Temporarily unavailable — try again in a minute (includes CHO's payer directory still loading) |
| 401/403 | Not authorized for this practice — contact support |
| 502/other, timeout | Could not be completed; not a problem with the patient's information |

The router generates one correlation ID and passes it to CHO, so CDO audit logs and CHO logs for the same check line up. No log line contains member IDs, names or dates of birth.

**Configuration.** Eligibility is off until both the URL and the credential are set.

```text
CloudHealthOffice__Eligibility__BaseUrl=https://provider-eligibility.internal.<cdo-env domain>
CloudHealthOffice__Eligibility__ApiKey=<CHO Key Vault secret provider-eligibility-cdo-api-key>
PayerConnectivity__Payers__<payer ID on the insurance plan>__Eligibility=CloudHealthOffice
```

The payer ID on each insurance plan must be one CHO's payer directory recognizes (search it with CHO's `GET /api/v1/payers?q=`). In Azure these come from the `deploy-aca.yml` inputs: repository variable `CHO_ELIGIBILITY_BASE_URL`, secret `CHO_ELIGIBILITY_API_KEY`, and variable `CHO_ELIGIBILITY_PAYER_IDS` (a JSON array, e.g. `["PAYER1","PAYER2"]`). The CHO app has internal-only ingress in the shared `cdo-env` environment, so only apps in that environment can reach it.

Claim lifecycle is read from Cloud Health Office claim intelligence (`GET /api/claims/{claimId}/intelligence`). CloudDentalOffice does not inquire 276/277, parse 277CA, or ingest 835 files. Staff see a practice-facing timeline, status, patient responsibility, and posted patient-ledger financials. Clearinghouse vendor names never appear in the portal.

See [architecture/claim-lifecycle.md](architecture/claim-lifecycle.md).

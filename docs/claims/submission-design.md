# Dental claim submission through Cloud Health Office: design

**Decision (2026-09-28).** CDO sends dental claims to payers **through Cloud Health Office (CHO)**:
CDO → CHO → Stedi → payer. Acknowledgments, claim status and ERAs come back the same way. CHO keeps the
clearinghouse work (transport, 277CA/835 matching, claim intelligence); CDO keeps the practice work
(what was done, for whom, under which verified coverage, and posting payments to the ledger).

**Baselines.** CDO `main` at `4e4276f` (#93). CHO `main` at `db8dca7`. File references are to those commits.

---

## 1. Where things stand

### CDO today

| Piece | State | Where |
|---|---|---|
| Claim Wizard | Staff pick patient and provider, then **type** CDT codes and fees. Not linked to an appointment or to recorded procedures. | `Pages/ClaimWizard.razor` |
| 837D builder | Prototype. Hard-coded submitter "CLOUD DENTAL OFFICE", phone `5555551234`, billing address "123 DENTAL PLAZA, ANYTOWN CA 90210", Tax ID set to the submitter ID, subscriber always the patient (`SBR…18`), gender defaults to F, rendering = billing, no secondary payer. | `Services/EdiX12Service.cs` |
| Submission | Per insurance plan: `SFTP`, or `API` = multipart upload of the X12 to that plan's `ApiEndpoint` + `api/v1/claims/import/raw837`, with `X-Tenant-ID` and the plan's encrypted key. CHO's claim ID is stored as `Claim.CloudHealthOfficeClaimId`. | `Services/EdiSubmissionService.cs`, `CloudHealthOfficeApiService.cs:60-130` |
| Claim status | Claim lifecycle and posted amounts are read from CHO claim intelligence (#68). | `Services/ClaimIntelligenceService.cs` |
| Procedures | `Procedure` records CDT code, tooth, surface, fee, service date and status (`Completed`/`Billed`/`Paid`). `AppointmentId` is an `int` from the legacy Portal appointment table; appointments now live in SchedulingService with `Guid` IDs. | `Models/Procedure.cs:29` |
| Practice billing identity | **Missing.** No practice (Type 2) NPI, practice Tax ID or billing address at the organization level; only `Provider.TaxId`. | `Models/Organization.cs`, `Models/Provider.cs:58` |
| ERA (835) | `EraService` is TODO stubs. | `src/Services/EraService/Program.cs` |
| `ClaimsService` microservice | Separate database the Portal doesn't use. Leave it; the Portal path is the real one. | `src/Services/ClaimsService` |

### CHO today

- **`POST /api/v1/claims/import/raw837` is the wrong door for CDO.** It feeds CHO's own **payer adjudication**
  pipeline (scrub, provider integrity, network, benefits, NCCI, COB → approve/deny/pend) and never forwards
  to Stedi (`claims-service/Controllers/ClaimsV1Controller.cs:125-232`, `Program.cs:384-421`). Its parser has
  no `SV3`/`TOO` handling (`EDI/Inbound/X12837Parser.cs:320-386`), so a real 837D comes back `success:false`
  per claim. `claims-service` has no API-key authentication and falls back to `default-tenant`
  (`TenantMiddleware.cs:130-141`). It is not deployed next to CDO.
- **The provider-side pieces exist as a library**, contract-tested against Stedi's documented API, not yet live:
  - dental submission to `/2024-04-01/dental-claims/submission` with tooth, surface, oral cavity and quadrant
    (`Gateways/Stedi/StediHealthcareGateway.cs:188-370`, `Mapping/StediClaimMapper.cs:159-170`,
    `Models/GatewayClaimSubmissionRequest.cs`), idempotent by `IdempotencyKey`, returning a `TransmissionId`;
  - 277CA ingest (poller + webhook), 276/277 status, 835 ingest matched on payer/patient control number,
    and the Stedi webhook `POST /api/integrations/stedi/claim-responses`;
  - **claim intelligence** `GET /api/claims/{id}/intelligence`, which returns 404 unless the claim went through
    a gateway transmission (`eligibility-service/Controllers/ClaimIntelligenceController.cs`).
  - The only caller of claim submission is a Development-only demo controller
    (`GatewayClaimDemoController.cs:29-61`). ERA posting sinks default to in-memory.
- **The pattern to copy is already deployed**: `provider-eligibility-api`, a small service with `X-Api-Key` +
  tenant allow-list (`Security/ProviderApiAuthenticationMiddleware.cs`), a guard that refuses the Mock gateway
  outside Development (`Security/ProductionGatewayGuard.cs`), internal ingress, and `cdoTenantIds` in its Bicep.

---

## 2. Target flow

```
CDO appointment ──► completed procedures ──► draft claim (verified coverage + payer)
      │                                             │ pre-submission checks (§4)
      │                                             ▼
      │                         POST {CHO}/api/v1/claims/dental   (X-Api-Key, X-Tenant-ID, Idempotency-Key = CDO claim key)
      │                                             │
      │                                  CHO provider-claims-api ──► IClaimSubmissionGateway ──► Stedi ──► payer
      │                                             │                                              │
      │                     transmissionId ◄────────┘        277CA / 835 ──► Stedi webhook ──► CHO stores
      ▼                                                                                          │
CDO claim: Submitted ──► Accepted / Rejected (277CA) ──► Paid / PartiallyPaid / Denied (835) ◄── GET intelligence, GET remittances
                                                          │
                                                          └──► ClaimLifecycleService posts payment + adjustments to the ledger (one owner, §4f)
```

`raw837` stays what it is: CHO acting **as the payer**, for plans CHO administers. CDO routes a plan there only
when CHO is that plan's payer.

---

## 3. CHO: `provider-claims-api`

A new service beside `provider-eligibility-api`, built the same way, with **no** payer-side routes.

**Endpoints**

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/v1/claims/dental` | Validate a JSON dental claim, map it to `GatewayClaimSubmissionRequest` (`ClaimType = Dental`, `ClaimVersion` and `FrequencyCode` from the request, §4d) and submit through `IClaimSubmissionGateway`. `Idempotency-Key` header → `IdempotencyKey`, so a CDO retry of the same generation never submits twice. Returns `{ claimId, transmissionId, transmissionStatus, correlationId }`. `400` field errors for fixable data (same shape as provider eligibility); `422` for payer-level problems (`PayerNotFound`, `EnrollmentRequired`); `503` transient. |
| `GET` | `/api/v1/claims/{claimId}/intelligence` | The existing `ClaimIntelligenceComposer` view, tenant-scoped. |
| `GET` | `/api/v1/remittances?since=` and `/api/v1/remittances/{id}` | ERAs received for the tenant: payment, check/EFT trace, per-claim and per-line paid/adjustment codes (CARC/RARC), patient responsibility. Read model over the existing 835 receipts. |
| `GET` | `/api/v1/payers` | Payer search that reports, per payer, **dental claim (837D) support, 277CA, and ERA (835) support plus the tenant's enrollment status** for each. The provider eligibility search can't be reused as is: it exposes only `Eligibility` (`ProviderEligibilityController.cs:147`). `POST /claims/dental` also enforces it: a payer without 837D support, or requiring enrollment the tenant lacks, gets `422` before anything is sent. |

**The Stedi webhook is a separate, public boundary.** Stedi calls in from outside, with its own webhook
credential and no tenant header, so it can't live behind this service's internal ingress or its CDO
API-key + `X-Tenant-ID` middleware. It stays the existing `POST /api/integrations/stedi/claim-responses`
(`StediClaimResponseWebhookController.cs`), deployed in its own small Container App with **external** ingress
and only that route, authenticated by `StediGatewayOptions.WebhookCredentialValue`
(`WebhookCredentialHeaderName`), fail-closed. The tenant is taken from the matched transmission, never from
the request. It writes the **same durable stores** that `provider-claims-api` reads.

**Contract (JSON, versioned `v1`).** Mirrors `GatewayClaimSubmissionRequest` with dental fields required
where the CDT code needs them: billing provider (practice name, Type 2 NPI, Tax ID, address, taxonomy),
rendering provider (NPI, name, taxonomy), subscriber and patient (name, DOB, gender, address, member ID,
relationship), payer ID, group number, place of service, service dates, lines (`procedureCode` CDT,
`toothNumber`, `toothSurface`, `oralCavity`/`quadrant`, `charge`, `units`, `serviceDate`), prior-auth number,
and CDO's own claim key for correlation. **No diagnosis codes required** for routine dental.

**Security and operations**

- `X-Api-Key` compared in constant time + `X-Tenant-ID` on the client's allow-list (`cdoTenantIds`), exactly as
  provider eligibility. No `default-tenant` fallback.
- Refuse to start on the Mock gateway outside Development.
- **Durable stores** (Mongo) for transmissions, 277CA and 835. The in-memory default must be impossible in
  production.
- Logs carry tenant, payer, claim key and transmission ID only; never member IDs, names or DOB.
- Internal ingress; CDO reaches it over the Container Apps environment like provider eligibility. (The Stedi
  webhook is the separate public app above.)

**Before real claims**

1. One live 837D to a friendly payer on Stedi production; confirm the 277CA arrives through the webhook.
2. **ERA enrollment** with the practice's top payers (Stedi requires it for 835; CHO only tracks enrollment
   status). Until enrolled, payments are posted by hand from the paper/portal EOB.
3. Confirm Stedi pricing for claims and ERAs alongside eligibility.

---

## 4. CDO

**4a. Practice billing profile.** Add to the tenant's organization: legal name, Type 2 NPI, Tax ID (EIN),
billing address, phone, taxonomy. Required once, validated, shown on a settings page. Replaces every
hard-coded value in `EdiX12Service`.

**4b. Appointment → claim.**
- Link `Procedure` to the SchedulingService appointment by `Guid` (new nullable `SchedulingAppointmentId`;
  keep the legacy `int` column until it's retired).
- "Create claim" on a completed appointment: pulls its completed, unbilled procedures, the rendering provider,
  service date, and the coverage last verified for that appointment (`CoverageVerification`, #89). Staff review
  and edit before submitting; nothing is sent automatically.
- **v1 submits primary claims only** (proposed default; confirm, §6). Secondary insurance is billed manually outside CDO in v1 (staff
  send it with the primary EOB, as today). Automatic secondary claims with COB data from the primary ERA
  are a later step (§6).

**4c. Pre-submission checks.** A checklist the claim must pass, each with a staff-readable fix:
coverage `Verified` within 7 days for the service date (not `NotDental`/`Inactive`); dental payer ID present;
practice profile complete; rendering NPI; tooth/surface/quadrant present where the CDT code needs them;
charges > 0; no duplicate open claim for the same procedures.

**4d. Submission client.** Replace the X12 path for CHO-routed plans with a JSON client to
`provider-claims-api` (same pattern as `CloudHealthOfficeEligibilityClient`: HTTPS-only base URL, API key from
configuration/Key Vault, sanitized logs, typed failure → "fix the claim" vs "try again"). Store `transmissionId`
and CHO's claim ID.

*Generations and idempotency.* `Claim` has no submission version or frequency code today, so add two
persisted columns: `SubmissionGeneration` (int, starts at 1) and `FrequencyCode` (`1` original, `7`
replacement, `8` void). They go on the wire as `claimVersion` and `frequencyCode`, and the idempotency key is
`tenant : claimId : generation`.
- **A retry** after a timeout or `503` reuses the same generation, so CHO returns the existing transmission.
- **A correction** of an accepted claim increments the generation and sends `7` with the payer's claim control
  number; **a void** increments it and sends `8`. Each is a new, intentional submission.
- A claim the payer **rejected** at the front end (277CA reject, never adjudicated) is fixed and resent as a new
  generation with frequency `1`.

**4e. Status.** Reuse claim intelligence (#68) against the new endpoint; add a worker (the coverage
verification worker's lease pattern) that refreshes open claims until they reach a terminal state. Claim
states follow the existing lifecycle mapping (`ClaimIntelligenceService.cs:383-406`): Submitted, Accepted,
Rejected, Paid, **PartiallyPaid**, Denied. PartiallyPaid is *not* terminal for the claim while any line is
still open (pended, or awaiting a corrected claim); it is terminal once every line is resolved (§4f).

**4f. ERA posting: one owner.** `ClaimLifecycleService.RefreshAsync` already posts the charge, insurance
payment and contractual adjustment for a claim once, guarded by `Claim.FinancialsPostedAt`
(`ClaimIntelligenceService.cs:233-270`). **It stays the only thing that writes remittance money to the
ledger.** There is no second ERA poster.
- Extend it to post **per line** from the remittance detail (`/api/v1/remittances`, CARC/RARC codes) instead of
  claim totals: insurance payment, contractual adjustment (CO), and patient responsibility (PR: deductible,
  coinsurance, copay) per procedure.
- Idempotency moves from the claim-level `FinancialsPostedAt` flag to a posted-remittance record keyed on
  (tenant, claim, remittance trace number, line). Replaying the same ERA posts nothing; a later ERA for the same
  claim (a reprocessing or a payment on a pended line) posts only its own amounts. Claims already posted under
  the old flag are left as they are.
- **Line status**, not a blanket `Paid`: a procedure becomes `Paid` only when its insurance portion is settled
  (paid, or adjusted to zero) and any remaining balance has moved to patient responsibility; a **denied** line
  stays open with its CARC reason for staff (appeal, correct and resend, or bill the patient); a **pended** line
  stays `Billed`. The claim is `Paid` only when every line is `Paid`, and `PartiallyPaid` otherwise.
- ERAs that match no CDO claim go to a staff queue. This replaces `EraService`.

**4g. Retire.** Once live: remove `EdiX12Service`'s hand-built 837D and the `API`→`raw837` route for non-CHO
payers; keep SFTP only if a payer needs it.

---

## 5. PR sequence

| # | Repo | Scope | Unblocks |
|---|---|---|---|
| 1 | CDO | Practice billing profile (§4a) | Any real claim |
| 2 | CHO | `provider-claims-api`: submit, durable stores, auth, guard, Bicep with `cdoTenantIds` (§3) | Transmission |
| 3 | CHO | Intelligence + remittance read endpoints in the new service; the Stedi webhook as its own public app writing the same durable stores | Status and payments back to CDO |
| 4 | CDO | Appointment → claim draft, `SchedulingAppointmentId` on procedures (§4b) | Less re-typing |
| 5 | CDO | Pre-submission checks + JSON client + status refresh (§4c–e) | End-to-end submission |
| 6 | both | Live validation: one real 837D, 277CA, then one ERA after enrollment | Pilot |
| 7 | CDO | Line-level posting in `ClaimLifecycleService` and the unmatched queue (§4f); retire the X12 path (§4g) | Hands-off payments |

PRs 1–3 can run in parallel. The pilot needs 1–6; until 7, payments are posted by hand.

## 6. Open questions

- Which payers does 3rd Set Smiles bill most (the Ascend report), and which already have ERA enrollment
  elsewhere that would need moving?
- Attachments (X-rays, perio charts) for crowns and SRP: Stedi 275 exists in CHO's library; in scope for v1 or later?
- **v1 default, to confirm:** primary claims only; secondary billed manually (§4b). Later: automatic secondary
  claims with COB data from the primary ERA.
- **Whose Stedi account submits?** CHO's gateway has one `StediGatewayOptions.ApiKey` (CHO's account), while
  CDO eligibility uses the practice's own key from Key Vault (#78). ERA enrollment and Stedi billing belong to
  the account that submits. Options: CHO's account (no CHO change; CHO acts as the practice's clearinghouse),
  or a per-tenant Stedi key in CHO's gateway (one account per practice for eligibility, claims and ERAs).

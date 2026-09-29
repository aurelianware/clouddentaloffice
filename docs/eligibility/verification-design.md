# Coverage verification: design sketch

**Purpose.** Change eligibility from a button staff click into a workflow that runs by itself for every booked appointment. This matters most for Zocdoc bookings, which often arrive with medical insurance or none at all.

**Baseline.** `main` at `b72ec0c` ("Show when an active plan doesn't cover dental care", #86). This is a design sketch, not an implementation; file references are to that commit. PR 1 of §7 is #88. Revised after review: typed failure outcomes, worker tenant pinning and lease claims, appointment identity, portable columns, and the intake token.

---

## 1. What exists today

**Already works:**

- **Tenant-scoped eligibility on Stedi.** `ClearinghouseEligibilityAdapter` → `StediEligibilityClient`, with a per-practice key from `StediCredentialHandler`. The CHO gateway remains as the alternative path.
- **Request building.** `EligibilityRequestBuilder.Build` handles the subscriber/dependent split, validation, and messages written for staff.
- **Normalization.** `CloudHealthOfficeEligibilityMapper.ToResult` extracts the deductible, annual maximum, and "remaining" amounts, and sets `DentalCareNotCovered` (#86). That flag already catches the "Zocdoc gave us a medical card" case when the check runs.
- **Payer directory search.** `StediPayerSearchClient` searches Stedi's payer directory with the practice's own key.
- **Card OCR.** VisionService extracts the payer name, payer ID, member ID, and group number from card images.

**Gaps that matter for the Zocdoc problem:**

| # | Gap | Where |
|---|---|---|
| G1 | **Eligibility only runs when someone clicks.** The only callers are `PatientInsuranceDialog.razor:287` and `TreatmentPlanInsuranceEstimate.razor:187`. `docs/stedi/current-state.md` §3 confirms nothing runs in the background. | Portal |
| G2 | **Results aren't saved.** `EligibilityResult` is shown and then thrown away; only a `TransactionAuditRecord` (status + timing) is kept. There is no "last verified" on a coverage and no history. | `PayerTransactionRouter.AuditAsync` |
| G3 | **Zocdoc insurance is dropped.** `ZocdocAppointmentDto`/`ZocdocPatientDto` have no insurance fields, so whatever Zocdoc sends never reaches CDO. | `SchedulingService/Integrations/Zocdoc/ZocdocModels.cs:93-111` |
| G4 | **Only service type 35 is requested.** `NormalizedEligibilityRequest.ServiceTypeCodes` defaults to `["35"]` and the builder never sets it. The CHO path sends only the first code (`ToWire`, `CloudHealthOfficeEligibility.cs:291`). | `Models/PayerConnectivity.cs:45` |
| G5 | **Multi-code benefit lines are collapsed.** `ToBenefit` keeps only `ServiceTypeCodes.FirstOrDefault()`. A coinsurance line Stedi returns for `[24,25,26]` is recorded as restorative only. | `StediEligibility.cs:232` |
| G6 | **The coinsurance summary is never filled.** `EligibilityResult.Coinsurance`/`Copay` are set only by the Mock adapter. There's no preventive/basic/major breakdown. | `PayerConnectivity.cs:219` |
| G7 | **A card scan doesn't trigger a check.** There's a `TODO: Trigger eligibility check`, and the OCR'd "payer ID" is whatever is printed on the card, not a verified clearinghouse payer ID. | `VisionEndpoints.cs:274` |
| G8 | **The router needs an ambient tenant.** `PayerTransactionRouter.ValidateTenant` compares against `ITenantProvider.TenantId`, so a background worker has to set up a tenant scope for each row before calling it. | `PayerConnectivity.cs` |
| G9 | `src/Services/EligibilityService` is still a hard-coded stub. | Leave it alone or delete it; the portal path is the real one. |

---

## 2. The state machine

One `CoverageVerification` row per **(appointment, coverage slot)**, enforced by a unique index (§3). A patient with no dental coverage on file still gets a row for the primary slot, in `NeedsInfo`.

```
                 ┌────────────── coverage added / card scanned / Zocdoc hint resolved
                 ▼
 [NeedsInfo] ──► [Ready] ──► [Verifying] ──┬──► [Verified]           all key fields present
      ▲             ▲                      ├──► [VerifiedPartial]    active, but key fields missing
      │             │                      ├──► [NotDental]          active plan, dental not covered (#86)
      │             │                      ├──► [Inactive]           coverage termed for the service date
      │             │                      ├──► [DataRejected]       payer says member/name/DOB wrong
      │             │                      ├──► [ManualOnly]         payer not supported / enrollment required
      │             └── retry (backoff) ◄──┴──► [Unavailable]        timeout, 429, 5xx, credential problem
      │
      └── NotDental / Inactive / DataRejected send the patient back to NeedsInfo (ask for the dental card)

 [SelfPay]: staff or patient marks "no dental insurance". Terminal until a person changes it:
           timers never pick it up, and only a new coverage or an explicit undo leaves it.
 Any other terminal state goes back to Ready on: T-72h re-check, morning-of re-check, or edited coverage.
```

**How outcomes map to states.** Some of these need a contract change. Today the gateways fold several distinct failures into the same exception type:

- The builder's own validation, a Stedi 400/422, and a CHO 422 all throw a plain `TreatmentEstimateValidationException`.
- CHO's `ErrorCategory` is turned into a message by `PayerProblemMessage` and then dropped.
- A 271 with `Errors` becomes `CoverageStatus.Unknown`, with nothing marking it as a rejection.

So a worker can't tell missing data from payer rejection from "payer not supported" without parsing message text, and it must not parse message text. #88 therefore maps all of them to `NeedsInfo` / `Unconfirmed` for now. PR 2 adds a typed failure kind:

```csharp
public enum EligibilityFailureKind { MissingData, PayerRejectedData, PayerNotSupported, EnrollmentRequired, Transient, Configuration }
// Carried on TreatmentEstimateValidationException / TreatmentEstimateUnavailableException (new optional property),
// and as EligibilityResult.Rejected (bool) when a 271 comes back with Errors.
```

Each thrower sets the kind where it already knows it: the builder → `MissingData`, Stedi 400/422 and 271 `Errors` → `PayerRejectedData`, CHO `ErrorCategory` → the matching kind, HTTP 429/5xx and timeouts → `Transient`, credential and setup problems → `Configuration`. Staff messages stay as they are.

| Signal (after the contract change) | State |
|---|---|
| `MissingData` | `NeedsInfo`, with the builder's message as the reason |
| `PayerRejectedData`, or `EligibilityResult.Rejected` | `DataRejected` |
| `PayerNotSupported` / `EnrollmentRequired` | `ManualOnly` |
| `Transient` | `Unavailable`, retried with backoff; after 3 failures, surfaced |
| `Configuration` | `Unavailable`, not retried; alerts support, since retrying won't help |
| `CoverageStatus.Inactive` | `Inactive` |
| `Active` && `DentalCareNotCovered` | `NotDental` |
| `Active` && completeness < threshold (§4) | `VerifiedPartial` |
| `Active` && complete | `Verified` |

**Triggers:**

1. **Appointment created** (Zocdoc webhook consumer, booking consumer, or staff booking). Create the row. If a primary `PatientInsurance` exists, start in `Ready`; otherwise start in `NeedsInfo` and send the intake request (§5).
2. **Coverage added or edited, or a card scan is accepted.** Move to `Ready`.
3. **Timer worker, about every 15 minutes.** Pick up `Ready` rows, plus rows at the **T-72h** and **morning-of** checkpoints that are older than their last check.
4. **Manual "Check eligibility" button.** Uses the same code path, writes the same row, and keeps today's UI.

**The worker** (G8) follows `ReviewOutreachDispatcher`: select due IDs with `IgnoreQueryFilters`, then **claim each row atomically** before doing any external work. The claim is an `ExecuteUpdate` that sets `LockId` and `LockedUntil = now + lease` only where the row is still due and unlocked; skip the row if the update affected 0 rows. The follow-up writes (result, `Attempts`, `NextCheckAt`) are all conditioned on `LockId`. This way overlapping ticks or multiple replicas never send duplicate billed checks or race the attempt counter. An expired lease makes the row claimable again.

**Tenant in the worker.** A plain DI scope isn't enough. `ITenantProvider` is registered as `BlazorTenantProvider`, which reads the tenant from authentication state and falls back to `demo`, so `ValidateTenant` would reject every real row, and any entity the worker saves could be stamped `demo`. Instead:

- Add a scoped `WorkerTenantContext` with a one-time `Pin(tenantId)`. `Pin` throws if called twice or with a blank tenant.
- Register `ITenantProvider` as a factory that returns the pinned value when `WorkerTenantContext` is pinned, and otherwise `BlazorTenantProvider`.
- For each claimed row, the worker creates a scope, pins `row.TenantId`, and **only then** resolves `CloudDentalDbContext` and `IPayerTransactionRouter` from that scope. A test asserts that resolving either before pinning throws inside the worker scope.
- Don't loosen `ValidateTenant`.

**Cost control.** Each Stedi check is billed to the practice's account, so:

- Skip the T-72h check if a `Verified` result is less than 7 days old and the plan dates cover the service date.
- Run the morning-of check only for patients new to the practice.
- Never check `SelfPay` rows.

---

## 3. Persistence

**Already in #88:** `EligibilityVerifications`, with one row per check, keyed by coverage and carrying `ServiceDate`, `State`, `Reason`, `CorrelationId`, `Source`, `CheckedAt` and the normalized `ResultJson`. `PatientInsurance` gained `LastVerifiedAt` / `LastVerificationState`. That table replaces the `EligibilitySnapshot` in the first draft of this doc. It already records the service date, so checks for different appointment dates stay distinguishable. PR 2 adds two nullable columns to it: `Profile` (`Basic`/`Detailed`, §4a) and `CoverageVerificationId`, so each check links back to the appointment it was run for.

**New in PR 2**, the per-appointment workflow row:

```csharp
public class CoverageVerification : ITenantEntity
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";

    // SchedulingService's canonical appointment ID (Guid), the same key ReviewOutreach uses.
    // Portal's Appointment model only carries this in the non-persisted ExternalId, so it isn't usable here.
    public Guid SchedulingAppointmentId { get; set; }
    public int CoverageSlot { get; set; }           // 1 = primary, 2 = secondary; matches PatientInsurance.SequenceNumber
    public int PatientId { get; set; }
    public int? PatientInsuranceId { get; set; }    // null while NeedsInfo

    public DateOnly ServiceDate { get; set; }
    public VerificationState State { get; set; }
    public string? Reason { get; set; }             // staff-readable; never echoes member IDs
    public string? MissingFieldsJson { get; set; }  // JSON array, e.g. ["Basic coinsurance","Annual maximum"]
    public int Attempts { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? NextCheckAt { get; set; }
    public Guid? LockId { get; set; }               // atomic claim (§2)
    public DateTimeOffset? LockedUntil { get; set; }
    public string? Source { get; set; }             // "Zocdoc", "Website", "Staff"
    public string? IntakeHint { get; set; }         // e.g. "Zocdoc: patient selected Aetna (medical)"
    public DateTimeOffset? IntakeSentAt { get; set; }
}
```

- **Unique index** on `(TenantId, SchedulingAppointmentId, CoverageSlot)`. Creation is an idempotent insert that treats a unique-violation as "already exists". Webhook retries and multiple appointment-created handlers therefore can't create duplicate rows, and the intake message is sent only when `IntakeSentAt` is null, set in the same conditional update that claims the send.
- **`MissingFieldsJson` is a string column** holding a JSON array, not a `string[]`. The Portal runs on SQLite, PostgreSQL and SQL Server, and only Npgsql maps arrays natively. `ResultJson` in #88 uses the same approach.
- **The appointment-created signal crosses services.** SchedulingService owns appointments, and the Zocdoc and booking consumers run there. PR 2 publishes an `AppointmentScheduled` integration event (tenant, appointment Guid, patient ID, start time, source) on the existing Service Bus. The Portal consumes it to create the row, with the tenant taken from the payload, as the other consumers do.
- **Still no raw 271 by default.** Keep an opt-in debug capture for the payer-quality study (§6), with a short retention period.

---

## 4. Service-type codes and the benefit summary

### 4a. Request profiles (G4)

Add `EligibilityDetail` to the builder:

| Profile | Codes | When |
|---|---|---|
| `Basic` | `35` | Initial check on a new or unknown coverage (cheapest, most widely supported) |
| `Detailed` | `35, 23, 41, 25, 26, 24, 40, 39, 36, 27, 38` | After `Basic` returns `Active` and not `NotDental`; also on the treatment-plan screen |

- The `Detailed` codes are exactly the union of the §4c categories (Preventive 41/23, Basic 25/26/24/40, Major 39/36/27, Ortho 38) plus 35. A test keeps the two lists in sync, so no category depends on a code that's never requested.
- `Detailed` runs **only on the direct Stedi gateway**, because CHO's `ToWire` sends one code. Either extend CHO's contract or keep the CHO path on `Basic`.
- Payers vary: some ignore extra codes, some answer only the first, and a few reject multi-code inquiries. If a `Detailed` request fails with a 400/422 or `Errors`, retry once with `Basic` and record the payer in a per-payer `SupportsMultiStc = false` cache (a column on `InsurancePlan`, or a small table keyed on payer ID). Build this list from real responses (§6), not from assumptions.

### 4b. Fix the benefit fan-out (G5)

**Done in #88.** In `StediEligibilityWire.ToBenefit`, emit **one `ChoBenefit` per service type code** on the line (same amounts and percents) instead of keeping `FirstOrDefault()`. It's a small change with a test: a coinsurance line for `[24,25,26]` should count toward periodontics, restorative, and endodontics.

### 4c. `DentalBenefitSummary` (G6)

A new projection built from `EligibilityResult.Benefits`. It's added to `EligibilityResult` so existing consumers keep working.

```csharp
public sealed record DentalBenefitSummary
{
    public CategoryCoverage Preventive { get; init; }  // 41, 23
    public CategoryCoverage Basic { get; init; }       // 25, 26, 24, 40
    public CategoryCoverage Major { get; init; }       // 39, 36, 27
    public CategoryCoverage Orthodontics { get; init; }// 38, lifetime max, kept separate as the mapper already does
    public decimal? AnnualMaximum { get; init; }  public decimal? AnnualMaximumRemaining { get; init; }
    public decimal? Deductible { get; init; }     public decimal? DeductibleRemaining { get; init; }
    public IReadOnlyList<string> PayerNotes { get; init; } = []; // MSG / additionalInformation, deduplicated
    public IReadOnlyList<string> MissingFields { get; init; } = [];
}
public sealed record CategoryCoverage(decimal? PlanPaysPercent, bool? Covered, IReadOnlyList<string> Notes);
```

- **Coinsurance direction.** In X12, EB01 `A` is the *patient's* share. The summary shows "plan pays %" = 1 − patient share, which is what front desks expect ("100/80/50"). Test this with real payer responses; some payers send the plan-pays figure in the patient-share slot.
- **Category rollup.** Use a line specific to the category if one exists. If not, fall back to a `35` line and mark that category's note "from general dental line."
- **Completeness.** A result is `Verified` when it has: active status, annual maximum (or remaining), deductible, and plan-pays % for Preventive and Basic. Anything missing goes into `MissingFields`, and the state becomes `VerifiedPartial`. The front-desk task then reads, for example, "Call Guardian: missing Basic %, annual max."
- **Frequencies and limitations** (for example "prophy 2 per calendar year", "bitewings 1 per 12 months") often come back as `F` lines with quantity qualifiers, or only as MSG free text. **Phase 1:** show `PayerNotes` verbatim under each category. **Phase 2:** add Stedi's quantity and procedure-code fields to `StediBenefit`. Add them only after the §6 capture shows which fields the Arizona payers actually populate; don't add wire fields speculatively.

---

## 5. Getting a usable dental coverage for Zocdoc patients

**Step 1: Capture Zocdoc's insurance as a hint (G3).**

- Pull a real appointment from the sandbox or production and add whatever insurance object Zocdoc actually returns to `ZocdocAppointmentDto`/`ZocdocPatientDto`. Check the real payload first; the field names aren't known from this repo.
- Store it as `CoverageVerification.IntakeHint` only.
- **Never automatically create a `PatientInsurance` from it.** It's usually medical, and #86 exists because medical plans come back "active, dental not covered."
- If the hint names a carrier with a dental line (Cigna, Aetna, Humana, UHC, MetLife), the intake message can say "If your dental plan is also through Cigna, snap a photo of the dental card."

**Step 2: Post-booking intake link.**

- `docs/THIRD_SET_SMILES_BOOKING_RUNBOOK.md` deliberately rejects member IDs and card images on the **public** booking endpoint. Keep that.
- Instead, send a link backed by its **own intake token**, separate from the patient portal. `AddPatientPortalBillingIdentity` only maps an authenticated issuer/subject to a patient, and the billing endpoints require the `Patient` role from that sign-in. It isn't a link mechanism, and reusing it would weaken that trust boundary.
- **The token.** Store a random 256-bit value hashed (SHA-256) in an `IntakeTokens` row, bound to tenant + `CoverageVerification.Id` + patient. Each row has an expiry (e.g. 7 days or the appointment start, whichever is sooner), a `UsedAt`, and a revocation flag. Only the raw token goes in the SMS/email. `PublicWebsiteScheduling.Protect` (AES-GCM) is a precedent for opaque tokens, but a DB-backed hash is needed here for single-use and revocation.
- **Authorization.** An anonymous endpoint on IntakeService accepts the token plus a second factor the patient knows: their date of birth, compared server-side and rate-limited per token and per IP. The token lets the patient write **only** to that verification row's pending intake: card images and the fields they type. It grants no read access to existing coverage or chart data and no access to the patient portal. Staff review the submission before it becomes a `PatientInsurance`.
- The page has three choices: *upload dental card (front/back)*, *type it in*, or *I don't have dental insurance* (→ `SelfPay`, which staff can undo).
- Send it via the existing outreach channel (SMS/email) when the row enters `NeedsInfo`, and remind once at T-48h.

**Step 3: Card scan → resolved payer → check (G7).**

- VisionService OCR gives the payer name and printed payer ID.
- Call `StediPayerSearchClient.SearchAsync(tenantId, new StediPayerSearchRequest { Query = ocr.PayerName, State = patient.State })`. `DentalOnly` and `EligibilityOnly` already default to true. The `State` filter keeps payers operating in the patient's state or nationally, which separates most regional entities, such as Delta Dental's state plans. With one confident match, prefill `InsurancePlan.PayerId`. With several ("Cigna" vs "Cigna Dental", or a patient covered through an out-of-state employer plan), staff pick from a short list; if the state-filtered search is empty, rerun it without `State` before showing "no match". Show the patient's state and the card's customer-service phone number to help them choose.
- Once staff accept, the coverage is saved and the row moves to `Ready`. The worker handles the rest; this replaces the TODO at `VisionEndpoints.cs:274`.

**Step 4 (later): a mapping table that learns.**

- Each `Verified` result for a Zocdoc patient records (Zocdoc hint carrier, employer/group if known → dental payer ID that worked), per tenant.
- Once a mapping has enough history, the intake link can prefill a suggested carrier.
- Seed it with the Arizona AHCCCS and Medicare Advantage dental carve-outs the practice actually sees.

**Worth testing, not assuming:** Stedi offers coverage-discovery-style lookups (finding a plan from demographics). Check whether it returns dental plans for the Arizona payer mix before designing around it.

---

## 6. Before promising anything to 3rd Set Smiles: payer quality study

1. Pull 3rd Set Smiles' top ~10 dental payers by patient count from the Dentrix Ascend export.
2. For 2–3 real patients per payer (with consent, on the practice's own Stedi account), run `Basic` and `Detailed` and capture the full 271 JSON (debug capture, 14-day retention).
3. For each payer, record: accepts multi-STC? category percents present? annual max / remaining present? frequencies structured or MSG-only?
4. The result sets the `SupportsMultiStc` cache and shows how large the `VerifiedPartial` queue will be. That number is the honest pitch: "X% of patients verified with no phone calls."

---

## 7. Suggested PR sequence

| PR | Scope | Unblocks |
|---|---|---|
| 1 (#88) | `EligibilityVerifications` history; manual checks write to it; `LastVerifiedAt` on coverage; **G5 fan-out fix** with tests | History, and the basis for everything below |
| 2 | Typed `EligibilityFailureKind`; `CoverageVerification` (unique per appointment + slot); `AppointmentScheduled` event; `WorkerTenantContext`; lease-claimed `CoverageVerificationWorker` (triggers, backoff, T-72h/morning-of, never `SelfPay`); "Insurance to verify" queue page listing non-`Verified` rows for the next 3 days | Hands-off verification |
| 3 | `Detailed` profile on the Stedi gateway, `Basic` fallback, `DentalBenefitSummary`, completeness → `VerifiedPartial` + `MissingFields` | Useful benefit breakdown; better estimates. **Partly done:** `DentalBenefitSummary` with `MissingFields`, and the `Detailed` inquiry with a one-time `Basic` retry on HTTP 400/422 behind `Stedi:RequestDetailedDentalBenefits` (off until §6). Still open: `VerifiedPartial`, the `SupportsMultiStc` cache, and retrying on payer `Errors` |
| 4 | Zocdoc insurance hint (G3) + intake token/endpoint (§5) + `SelfPay` | Fixes the Zocdoc intake gap |
| 5 | Card scan → Stedi payer resolution → auto-`Ready` (G7) | Removes staff lookup of payer IDs |
| 6 | Learned payer-mapping table + AHCCCS/MA seed | Fewer `NeedsInfo` over time |

PRs 1–2 plus a minimal version of 4 (the intake link with typed entry, no OCR) are the realistic target for the October pilot. PR 3 depends on the §6 study.

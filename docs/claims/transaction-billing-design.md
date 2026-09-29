# Billing practices for payer transactions: design

**Status.** Proposed, 2026-09-29. Compares what Cloud Dental Office (CDO) and Cloud Health Office (CHO) already do
against Stedi's guide [How to bill providers for payer transactions](https://www.stedi.com/blog/how-to-bill-providers-for-payer-transactions),
and proposes how Aurelianware meters and charges practices for eligibility checks, claims and ERAs.

**Baselines.** CDO `main` at `359ec49` (#95). CHO `main` at `15c050f`. File references are to those commits.

---

## 1. What Stedi's guide says

**Account models.**

| Model | Who Stedi bills | Who tracks per-practice usage |
|---|---|---|
| Single shared account | The platform, one invoice | The platform |
| Integrated accounts, consolidated billing | The platform, one invoice with a per-practice breakdown (custom pricing from Stedi) | Stedi |
| Integrated accounts, provider billing | Each practice, directly | Stedi |

**How platforms charge practices.** Pass-through at Stedi's rate; a per-transaction markup (fixed or percentage);
or a bundled monthly fee covering expected volume, with overage pricing above it.

**What a shared account must record per transaction:** the provider's NPI or TIN, the transaction type, the
payer ID (for passthrough fees), the date, and whether the transaction is billable.

**Billability rules.**
- Eligibility checks rejected with AAA **42** (unable to respond now), **79** (invalid participant
  identification) or **80** (no response received) are not billed. Other AAA rejections are billed.
- Test-mode requests (API keys starting `test_`) are not billed.
- A few payers add **passthrough fees**, billed at cost on top of Stedi's rates.

**Pricing shape.** Usage-based, with volume tiers that apply within a calendar month and reset each month.
No setup, per-provider or per-location fees.

---

## 2. Where we are

**Account model: single shared account, for now.** Claims go out on Aurelianware's CHO Stedi account and Stedi
bills Aurelianware (decided 2026-09-28, [submission-design.md §6](submission-design.md)). Eligibility follows
each practice's connection mode: `Shared` uses Aurelianware's key and `Integrated` uses the practice's own
([credentials.md](../stedi/credentials.md)). Eligibility routed through CHO (`EligibilityGateway = CloudHealthOffice`)
also runs on Aurelianware's account.

| Piece | Repo | State |
|---|---|---|
| Stedi key | CHO | One per deployment, `StediGatewayOptions.ApiKey` (`Gateways/Stedi/StediGatewayOptions.cs:45`). The tenant ID travels with each request and transmission, so usage can be attributed. |
| Per-practice Stedi keys | CDO | `TenantClearinghouseConnection` + Key Vault `stedi-apikey-{tenantId}`; shared key only behind `Stedi:SharedAccount:Enabled`. |
| Usage counters | CHO | `UsageMetrics` on the tenant (`tenant-service/Models/Tenant.cs:259`): claims, prior auths, eligibility checks, API calls this month; `GET tenants/{id}/usage`. **Nothing calls `UpdateUsageAsync`** (`TenantManagementService.cs:269`), so every tenant reads zero. The monthly reset compares only the month, not the year. |
| Subscription billing | CHO | Stripe tiers `starter_monthly` and `professional_monthly` (`tenant-service/Services/StripeService.cs:306`), upcoming-invoice and invoice endpoints. No metered or overage price. |
| Subscription billing | CDO | `Organization.Plan` plus Stripe IDs; no transaction component. |
| Telemetry | CHO | `cho.edi.transactions.total` by transaction type (`Observability/ChoMetrics.cs:41`). Good for dashboards, not a billing record. |
| Cost estimate | CDO | Pilot Metrics: billable eligibility checks × `PilotMetrics:EligibilityCheckPrice` ($0.30). Since this PR, answers rejected with AAA 42, 79 or 80 are left out. Covers only checks CDO sent itself. |

**Gaps against the guide.**
1. No usage record per transaction anywhere: nothing carries NPI/TIN, payer ID or a billable flag.
2. Claims, claim status (276/277), attachments (275) and ERAs (835) aren't counted at all.
3. Passthrough fees aren't tracked.
4. Test-mode traffic isn't told apart from production.
5. CDO's estimate and CHO's counters measure the same thing separately, and CHO's reads zero.

---

## 3. Proposal

**Charge a bundled monthly fee during the pilot.** Keep the Stripe plan as the only thing a practice pays. Use
the usage ledger below to see what each practice actually costs, and set the bundle size and any overage price
from real volumes. Revisit pass-through or a per-transaction markup once there are several practices; the ledger
supports all three models without changes.

**Move to integrated accounts later only if needed.** A practice that wants its own Stedi account already has a
path (`Mode = Integrated`); its transactions are then billed by Stedi to the practice and are recorded in the
ledger as `Billable = false` for Aurelianware.

### 3a. One usage ledger, in CHO

Claims leave from CHO, so CHO owns the ledger. The Stedi gateway (`StediHealthcareGateway`) writes one row per
transaction it sends on Aurelianware's account, in the same durable store as transmissions:

| Field | Notes |
|---|---|
| `TenantId` | From the request, as today |
| `TransactionType` | `270`, `837D`, `276`, `275`, `835` |
| `ProviderNpi`, `ProviderTaxId` | Billing provider on the transaction |
| `PayerId` | Stedi payer ID, for passthrough fees |
| `OccurredAt` | UTC |
| `Billable` | False for AAA 42/79/80, test-mode keys, and idempotent replays that weren't re-sent |
| `NotBilledReason` | `PayerUnavailable`, `TestMode`, `Replay`, `PracticeAccount` |
| `CorrelationId` / `TransmissionId` | Joins back to the transmission; no PHI |

Rules:
- Written in the same code path that records the transmission, keyed by transmission or correlation ID so a
  retry never counts twice.
- Only IDs and codes, never member names, member IDs or dates of birth.
- 835s are counted when received; confirm with Stedi how ERAs are billed.

`UsageMetrics` becomes a monthly roll-up of this ledger (fixing the year-blind reset), and
`GET tenants/{id}/usage` returns it.

### 3b. CDO reads usage from CHO

Once the ledger exists, Pilot Metrics shows CHO's usage for the practice (all transaction types) instead of
counting CDO's own eligibility rows. Until then, CDO's own estimate stays, now excluding AAA 42/79/80.

### 3c. Charging

- **Pilot:** bundled fee on the existing Stripe plan. The ledger is for Aurelianware's margin view only.
- **Later, if overage or markup is wanted:** report monthly billable counts per tenant to Stripe as metered
  usage from the ledger roll-up. Passthrough fees go on the invoice at cost as their own line.

---

## 4. PR sequence

| # | Repo | Scope |
|---|---|---|
| 1 | CDO | Leave AAA 42/79/80 out of the Pilot Metrics cost estimate (this PR) |
| 2 | CHO | Usage ledger store and writes from the Stedi gateway for eligibility and claims; billable rules; tests |
| 3 | CHO | Roll-up into `UsageMetrics` with a correct monthly reset; 835/276/275 counting |
| 4 | CDO | Pilot Metrics reads usage from CHO |
| 5 | CHO | Optional: Stripe metered price and passthrough fee lines |

## 5. Open questions

- The bundle size and price per practice. Needs a month or two of ledger data.
- Stedi's current rates for claims, claim status and ERAs (already a pre-pilot step in
  [submission-design.md §3](submission-design.md)).
- Whether any of 3rd Set Smiles' top payers charge passthrough fees.
- Whether consolidated billing on integrated accounts (Stedi custom pricing) is worth asking Stedi about once
  there are several practices, since it gives aggregate volume discounts and Stedi-side usage tracking.

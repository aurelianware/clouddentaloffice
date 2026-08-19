# Patient accounts and billing ledger

CloudDentalOffice is the system of record for patient financial responsibility. Payment processors such as Stripe may move money in a future integration, but they do not calculate or own balances.

```text
Clinical / Claims systems
          ↓
Patient Financial Events
          ↓
 Immutable Patient Ledger
          ↓
 Statements / Payments
```

## Account and ledger ownership

A `PatientAccount` is created lazily with the first posted financial transaction. It is unique by tenant and patient. `PatientId` is never sufficient to retrieve an account: every operation requires the authenticated tenant and every relationship and index retains `TenantId`.

`PatientLedgerEntry` is append-only. Once posted, an entry cannot be updated or deleted through the application DbContext. Corrections use a second entry whose amount negates the original and whose `ReversalOfEntryId` points to it. A unique reversal index ensures an entry is reversed at most once.

Each entry includes a controlled type, decimal amount, ISO currency, effective date, controlled source type and bounded source identifier, safe description code, creation time, and actor/source system. There are no patient demographics, treatment narratives, card data, or processor secrets in the ledger or its operational logs.

## Money and signs

`Money` validates decimal amounts to two fractional digits and normalizes three-letter ISO currency codes. An account cannot mix currencies. Normal postings use a positive amount; only a generated reversal uses the negative of the original.

Amount due is derived, never stored as the accounting source of truth:

```text
charges
+ refunds
+ debit adjustments
+ inbound transfers
- insurance payments
- patient payments
- contractual adjustments
- write-offs
- credits
```

A negative amount due is a patient credit balance. A transfer into an account is posted as `Transfer`; the corresponding transfer out is represented by a `Credit` tied to the same business transfer source. This keeps direction explicit while preserving positive normal postings.

## Idempotency and sources

The unique key `(TenantId, SourceType, SourceId, EntryType)` prevents a procedure, ERA component, payment, or staff operation from posting the same financial effect twice. One ERA can legitimately produce both an insurance payment and a contractual adjustment because their entry types differ. Source references are identifiers only, avoiding tight database coupling to clinical, claim, and ERA stores.

Posting currently supports `Procedure`, `Encounter`, `Claim`, `Era`, `StaffAdjustment`, `PatientPayment`, `Refund`, `Transfer`, and `SystemReversal` sources. Integration from those producer workflows is deliberately follow-up work; this PR establishes the accounting system of record without silently changing existing claim/ERA behavior.

## Staff APIs

The Portal exposes authenticated staff-only reads:

- `GET /api/patient-accounts/patients/{patientId}/summary`
- `GET /api/patient-accounts/patients/{patientId}/ledger`

Tenant identity comes from authenticated claims and is not accepted as a query, route, or request-header parameter. There are no public patient portal or payment-processor endpoints in this foundation.

## Follow-up work

- post procedure charges and adjudicated ERA/835 payments and adjustments through the application service
- introduce patient payment and refund commands with payment allocations
- generate immutable statement snapshots and aging buckets
- add account-to-account transfer orchestration
- connect a processor adapter such as Stripe without delegating ledger ownership
- baseline provider-specific PostgreSQL migrations as described by the Portal database bootstrap policy

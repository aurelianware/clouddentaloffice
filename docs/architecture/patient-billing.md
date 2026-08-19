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

## Payment processing boundary

Patient payments use a vendor-neutral boundary:

```text
Patient Billing
      ↓
Payment Gateway
      ↓
Stripe / future processors
```

`IPaymentProcessor` is the adapter contract. `IPaymentCheckoutService`, `IPaymentRefundService`, `IPaymentReconciliationService`, and `IPaymentAllocationService` orchestrate canonical CDO models without exposing processor DTOs. No production patient-payment processor is registered yet, so checkout resolution fails closed. The pre-existing `IStripeService` supports organization subscription provisioning and is not part of the patient-payment boundary.

`PaymentProcessorConfiguration` is tenant-scoped and stores only provider, enabled state, sandbox/production environment, an opaque credential reference, and an optional connected-merchant reference. It never stores credentials. Exactly one enabled configuration is required at resolution time; missing, disabled, ambiguous, or unregistered configurations are rejected before any processor call.

`PatientPayment` is the CDO-owned payment record. It stores amount, ISO currency, method category, processor name, bounded external identifiers, internal reference, status, and timestamps—never card numbers, bank account details, CVC, routing data, or processor payloads. A successful canonical reconciliation event posts one `PatientPayment` ledger entry, so the patient ledger remains the balance source of truth.

Processor event idempotency uses the unique tuple `(TenantId, Processor, ExternalEventId)`. Patient payment identity also protects `(TenantId, InternalPaymentReference)` and non-null `(TenantId, Processor, ExternalPaymentId)`. Duplicate delivery returns the previously reconciled result and does not post another ledger entry.

`PatientPaymentAllocation` links a succeeded payment to one or more charge/debit ledger entries. Allocations may be partial and cannot exceed the payment. The unallocated remainder is explicit unapplied cash; an overpayment does not require inventing a charge or forcing immediate allocation. Allocation records are append-only.

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

## Patient responsibility

Responsibility has an explicit provenance:

- `Estimated` responsibility is calculated from prospective charge, expected insurance payment, and expected adjustment inputs. It is advisory and does not post ledger entries or become collectible debt.
- `Finalized` responsibility is derived only from the immutable patient ledger after trusted financial events have been posted.

For example, an $850 charge, $422.60 finalized insurance payment, and $100 contractual adjustment produce $327.40 finalized patient responsibility. Existing claim and 835 models contain responsibility-related fields, but their current processing does not yet provide a complete adjudication-to-ledger pipeline. Those fields are therefore not silently promoted to authoritative patient debt. The treatment-plan estimate UI remains explicitly estimated.

## Statement snapshots

`PatientStatement` and its patient-safe detail lines are immutable financial snapshots. Statement selection uses ledger `CreatedAt` as its cutoff rather than service/effective date, ensuring a backdated entry posted after a prior statement appears on the next statement. Lines retain the ledger identifier and a controlled description such as “Dental services” or “Insurance payment”; claim narratives, procedure details, and source identifiers are not copied into patient-facing descriptions.

The distinction between balance and statement is intentional:

```text
Patient Account Balance       = current immutable-ledger state
Patient Statement Amount Due  = historical snapshot at statement creation
```

They may differ after later payments, reversals, or adjustments. Later ledger activity never rewrites a statement. A subsequent statement starts from the most recent active statement's `AmountDue` as balance forward, then snapshots ledger postings after that statement's cutoff. Corrections to a statement use `Voided` or `Superseded`; records and lines are not deleted.

Supported lifecycle states are `Draft`, `Ready`, `Sent`, `PartiallyPaid`, `Paid`, `Superseded`, and `Voided`. Status changes follow a narrow state machine. `PartiallyPaid` and `Paid` require balance-reducing ledger activity after the snapshot cutoff that reduces or satisfies the statement balance; a UI status change alone cannot claim that money was received. Later charges, refunds, debits, and transfers are new account activity and do not prevent a prior snapshot from being marked paid. Explicit payment allocations remain follow-up work, and status transitions never mutate statement amounts.

## Staff APIs

The Portal exposes authenticated staff-only reads:

- `GET /api/patient-accounts/patients/{patientId}/summary`
- `GET /api/patient-accounts/patients/{patientId}/ledger`

It also exposes authenticated statement administration:

- `POST /api/patient-statements/preview`
- `POST /api/patient-statements`
- `GET /api/patient-statements?patientId={id}`
- `GET /api/patient-statements/{statementId}`
- `POST /api/patient-statements/{statementId}/finalize`
- `POST /api/patient-statements/{statementId}/status`
- `POST /api/patient-statements/{statementId}/void`
- `POST /api/patient-statements/{statementId}/supersede`

Tenant identity comes from authenticated claims and is not accepted as a query, route, or request-header parameter. `patientId` is only a filter within that trusted tenant. There are no anonymous statement URLs, patient portal, or payment-processor endpoints in this foundation. The existing `/billing` page still uses placeholder invoice data and is not presented as an authoritative statement UI.

## Follow-up work

- post procedure charges and adjudicated ERA/835 payments and adjustments through the application service
- add authenticated staff payment/allocation APIs and durable refund records
- replace the placeholder Billing page with the authenticated statement APIs and add aging buckets
- render/print statements and add controlled delivery tracking
- add account-to-account transfer orchestration
- connect a processor adapter such as Stripe without delegating ledger ownership
- baseline provider-specific PostgreSQL migrations as described by the Portal database bootstrap policy

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
- `CloudHealthOffice`: prospective payment estimates through the server-side adapter from PR #20.
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

Future capability flags reserve architectural space for 837D claim submission, 276/277 claim status, 835 remittance, predetermination, and Advanced EOB. Those transactions are not implemented by this change.

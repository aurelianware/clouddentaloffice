# Claim lifecycle

CloudDentalOffice is the practice surface for dental claims. Cloud Health Office
owns payer connectivity and composes 837 / 277CA / 276/277 / 275 / 835 into a
**claim intelligence** read model. CloudDentalOffice consumes that API. It does
not speak EDI to staff, and it does not call Stedi or Availity.

```text
CloudDentalOffice
        |
        v
Claim Intelligence API
GET /api/claims/{claimId}/intelligence
Header: X-Tenant-ID
        |
        v
CloudHealthOffice
```

## What staff see

- Lifecycle status (Draft, Submitted, Accepted, Processing, Information needed,
  Denied, Paid, Partially paid)
- Timeline of practice-language events (submitted, payer accepted, payment
  ready to post, posted)
- Patient responsibility from the remittance summary
- Posted financials on the patient ledger (charge, insurance payment,
  contractual adjustment)

Next-action copy is practice-facing: waiting for payer, provide information,
correct and resubmit, post payment. Transaction names, control numbers, and
vendor names are not rendered.

## Identity

Submission stores the Cloud Health Office claim id on
`Claim.CloudHealthOfficeClaimId`. Refresh uses that id, then the local claim
number, and never crosses tenants.

Missing tenant on the intelligence call is rejected. An unknown or
other-tenant CHO claim is treated as not found (HTTP 404). Logs record tenant,
local claim id, lifecycle status, and whether financials posted — never names,
member ids, or remittance payloads.

## Posting

Claim intelligence is informational. CloudDentalOffice is the system of record
for patient balances. When intelligence reports a remittance for a paid or
partially paid claim, CDO posts:

| Ledger type | Amount |
| --- | --- |
| Charge | Submitted (billed) amount |
| Insurance payment | Paid amount |
| Contractual adjustment | Submitted − allowed (or submitted − paid − patient responsibility) |

Source identity is `(Claim, claim:{claimId}, entry type)`. Duplicate refresh is
a no-op. Denied remittances do not post.

## Configuration

```text
CloudHealthOffice__Enabled=true
CloudHealthOffice__BaseUrl=https://cloudhealthoffice.example
CloudHealthOffice__IntelligencePath=/api/claims/{claimId}/intelligence
CloudHealthOffice__IntelligenceBaseUrl=
CloudHealthOffice__ApiKey=<secret>
```

The path must be a relative URI. `{claimId}` is substituted and escaped. The
browser never calls this API.

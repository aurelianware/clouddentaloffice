# Patient payments operations

## Architecture and ownership

CloudDentalOffice owns patient accounts, immutable ledger entries, statement
snapshots, payments, allocations, refunds, and reconciliation state. Stripe is the
connected practice's processor and hosted card-entry surface.

```text
Authenticated patient -> CDO amount validation -> Stripe-hosted Checkout
Stripe signed event -> Intake durable inbox -> Service Bus -> CDO atomic posting
CDO ledger -> allocation -> statement/payment history -> generic notification
```

A browser redirect is never evidence of payment. Only a verified, tenant-routed
webhook posts a payment. Unique event and ledger-source constraints make replays
idempotent. Refunds remain pending until a signed Stripe event confirms them;
original financial entries are never deleted.

## Sandbox setup

1. Create a protected `stripe-sandbox` GitHub environment with reviewer approval
   and secrets `STRIPE_TEST_SECRET_KEY`, `STRIPE_CONNECTED_ACCOUNT_ID`,
   `STRIPE_WEBHOOK_SECRET`, and `STRIPE_TEST_TENANT_ID`.
2. Configure a synthetic-data tenant as `Sandbox` and add only its ID to
   `Payments:StripePosting:AllowedSandboxTenantIds`.
3. Configure a Connect event destination for connected-account events and inject
   its endpoint secret into IntakeService.
4. Run **Stripe Pilot Validation**, then complete a Stripe-hosted Checkout using
   Stripe test data and verify the signed event, one ledger post, allocation,
   reduced balance, patient history, partial refund, and clean reconciliation.

The workflow never runs on pull requests and receives secrets only through its
protected environment. Normal CI continues to run deterministic mocked tests.

## Live pilot checklist

- [ ] Practice onboarding complete; charges and payouts enabled.
- [ ] Live restricted/secret key stored in Key Vault and credential reference set.
- [ ] Live webhook destination uses a distinct secret and connected-account events.
- [ ] HTTPS return URLs and authenticated patient Billing access verified.
- [ ] Live tenant is absent from the sandbox allowlist.
- [ ] Generic notification copy, sender domain, and patient email consent reviewed.
- [ ] Recent signed event, zero failed inbox records, and clean reconciliation shown.
- [ ] Small real payment, history, allocation, refund, and payout verified by authorized staff.
- [ ] Support, finance, security, and rollback owners named.

## Routine operations

Review readiness and reconciliation daily during the pilot. Investigate pending
inbox age, failed events, payments pending over 24 hours, mismatches, and
disconnected accounts. Retry durable events only after correcting the safe failure
cause. Never paste event bodies, card details, or patient information into logs or
tickets.

Notifications use generic text and authenticated portal links. Disable
`Payments:Notifications:Enabled` if delivery is suspect; financial records remain
intact. SMS is not currently supported.

## Incident response and disabling payments

1. Disable the tenant processor locally to stop new Checkout creation.
2. Preserve inbox, attempt, ledger, refund, audit, and reconciliation evidence.
3. Roll a suspected key in the secret manager, deploy the replacement, validate,
   then expire the old key. Rotate webhook secrets with an overlap window.
4. Verify destination health, signature configuration, clock, request-size limit,
   connected-account routing, Service Bus, and inbox/dead-letter state without
   logging payloads.
5. Reconcile from the last clean time. Mismatches become `ReviewRequired`; they do
   not automatically mutate the ledger.
6. Restore payments only when Connect state, webhook health, inbox, and
   reconciliation are clean.

## Reliability and limitations

Durable intake precedes acknowledgement; leases recover restarts; transient broker
and processor failures retry; permanent failures remain visible; payment posting
uses a serializable transaction and database uniqueness. Multiple Checkout
sessions may exist, but confirmed payment events cannot post twice.

The protected workflow validates real sandbox account connectivity and the mocked
CDO lifecycle. Stripe-hosted Checkout completion and payout verification are
manual pilot steps. A newly configured tenant reports action required until its
first recent signed event and clean reconciliation exist.

# Stripe Connect

CloudDentalOffice uses Stripe Connect as a SaaS platform. Each dental practice has its own connected account and patient payments will use **direct charges**, so payments settle in the practice's Stripe balance rather than an Aurelianware merchant balance.

## Account model

The onboarding integration follows Stripe's current [SaaS platform guidance](https://docs.stripe.com/connect/saas) and Accounts v2 API:

- the account has the `merchant` configuration and requests card payments;
- the practice receives full Stripe Dashboard access;
- Stripe collects processing fees and is responsible for connected-account losses;
- Stripe-hosted onboarding collects business, identity/KYC, tax, payout, and bank details;
- CDO stores only the opaque `acct_...` account ID and a credential reference.

Stripe-specific HTTP models remain inside `StripeApiClient`. Patient Billing depends on the neutral `IPaymentProcessor` boundary. Connect onboarding alone does not enable patient checkout or refunds; those operations fail closed until their Stripe adapter work is completed.

## Secrets and environments

Create one tenant-scoped `PaymentProcessorConfiguration` with provider `Stripe`:

- `Environment`: `Sandbox` or `Production`
- `CredentialReference`: configuration/secret-provider key name, never the secret value
- `Enabled`: must be true before onboarding

The referenced value is loaded server-side through configuration, which in production can be backed by Azure Key Vault. Sandbox credentials must be a `sk_test_` or least-privilege `rk_test_` key; production credentials must be a matching `sk_live_` or `rk_live_` key. Prefer a restricted key containing only the permissions required by Connect accounts, Account Links, Checkout Sessions, PaymentIntents, and Refunds. Keys, identity data, and bank data must never be entered into CDO forms or stored in its database.

The version headers follow Stripe's current examples: account creation uses `2026-07-29.preview`, while account reads and Account Links use `2026-07-29.dahlia`. They can be pinned independently with `Stripe:Connect:AccountsCreateApiVersion`, `Stripe:Connect:AccountsReadApiVersion`, and `Stripe:Connect:AccountLinksApiVersion` after reviewing Stripe's changelog.

## Onboarding

An authenticated tenant administrator opens **Settings → Payments → Stripe** and selects **Connect Stripe**. CDO creates or reuses that tenant's connected account and generates a temporary, single-use [Account Link](https://docs.stripe.com/api/v2/core/account-links/create). The administrator completes Stripe-hosted onboarding.

The refresh URL creates a new single-use link. Returning to CDO does not itself prove completion; CDO retrieves account status and maps merchant capability and requirements state into:

- `Pending`
- `Restricted`
- `Enabled`
- `Disabled`

Production return and refresh URLs must use HTTPS. An account is operational only when both card charges and payouts are active. Disabling the integration is local: CDO retains the connected-account ID and does not close or delete the Stripe account.

## Operations

- Rotate a key in the secrets manager without changing the connected account.
- Update `CredentialReference` only when the secret-provider path changes.
- Refresh status after onboarding and when Stripe reports requirement changes.
- Do not log Stripe bodies, authorization headers, KYC data, or bank information.
- Review pinned API versions against Stripe's changelog before a version change and validate upgrades in sandbox first.

Official references: [Accounts v2](https://docs.stripe.com/connect/accounts-v2), [create a SaaS connected account](https://docs.stripe.com/connect/saas/tasks/create), [direct charges](https://docs.stripe.com/connect/charges), and [Stripe-hosted onboarding](https://docs.stripe.com/connect/hosted-onboarding).

## Patient balance Checkout

CDO creates Stripe-hosted Checkout Sessions as direct charges on the practice connected account. The server sends the `Stripe-Account` header and uses a server-generated payment reference as the idempotency key. Checkout is available only when the tenant configuration, card-payment capability, and payout capability are enabled.

The selectable amount is recalculated by CDO immediately before session creation:

- full balance comes from the current immutable patient ledger;
- statement balance comes from the statement snapshot less succeeded payments linked to it;
- a custom partial amount must be positive, within the current account balance unless overpayments are enabled, and no greater than `Payments:Checkout:MaximumAmount`.

Configure `Payments:Checkout:PublicBaseUrl` as the HTTPS Portal origin. The only Stripe-facing product name is `Account payment`. Session and PaymentIntent metadata contain only `payment_reference`, an opaque random CDO reference. Names, patient IDs, dates of birth, diagnoses, procedures, insurance and claim information are prohibited. See Stripe's [metadata security guidance](https://docs.stripe.com/metadata/use-cases).

CDO persists a `PatientPaymentAttempt` before contacting Stripe, then records the Checkout Session, optional PaymentIntent, and connected account IDs. Multiple sessions are allowed and receive distinct references. The success redirect is display-only and never posts a payment. A verified Connect webhook reconciles successful direct-charge events through the processor-event idempotency and ledger-posting boundary. Direct-charge objects must be retrieved in the connected-account context using `Stripe-Account`.

## Payment webhooks and ledger posting

Configure a Stripe Connect event destination for **events on connected accounts** at:

```text
POST https://<public-intake-host>/api/integrations/stripe/webhooks
```

Subscribe only to `checkout.session.completed`,
`checkout.session.async_payment_succeeded`, and
`checkout.session.async_payment_failed`, plus `refund.created`, `refund.updated`,
and `refund.failed`. A completed session is posted only when
`payment_status=paid`; delayed methods wait for the async success event.

IntakeService verifies `Stripe-Signature` against the unmodified body using the
secret for this exact endpoint. It resolves the tenant from the signed top-level
Connect `account` and a server-owned mapping. Tenant metadata is never trusted.

```text
StripeWebhooks__EndpointSecret=<injected whsec value>
StripeWebhooks__ToleranceSeconds=300
StripeWebhooks__Accounts__0__TenantId=<CDO tenant>
StripeWebhooks__Accounts__0__ConnectedAccountId=acct_...
StripeWebhooks__Accounts__0__LiveMode=false
StripeWebhooks__Accounts__0__Enabled=true
ServiceBus__StripeWebhookTopic=stripe-webhooks
ServiceBus__StripeWebhookSubscription=portal
Payments__StripePosting__AllocateStatementPayments=true
Payments__StripePosting__AllowedSandboxTenantIds__0=<explicit test tenant>
```

Never commit the secret. Dashboard, test/live, and Stripe CLI endpoints each have
distinct secrets.

Sandbox events are refused by the Portal ledger processor unless their tenant is
explicitly listed in `AllowedSandboxTenantIds`. Never add a live patient tenant to
that list. The Stripe administration page labels the environment as either
**SANDBOX / TEST** or **LIVE**.

```text
Stripe Connect webhook
  -> raw-body signature verification
  -> IntakeService durable inbox (unique Stripe event ID)
  -> Service Bus stripe-webhooks topic
  -> Portal payment processor
  -> canonical PatientPayment
  -> immutable patient-payment ledger entry
  -> configured FIFO statement allocation/status update when applicable
```

HTTP acknowledgement follows the inbox commit. The existing inbox lease/retry
worker recovers broker outages and restarts. Processor-event and ledger-source
unique constraints prevent duplicate posting.

CDO verifies the amount, currency, Checkout Session, PaymentIntent, connected
account, environment, and opaque reference. Mismatches become `Conflict` /
`ReviewRequired` without ledger activity. Unknown account mappings are rejected;
unknown payment references are dead-lettered for operator review. Raw payloads,
payment method details, and patient identifiers are never logged.

PHI-free metrics are `stripe.events.received`, `stripe.events.persisted`,
`stripe.webhook.validation_failures`, `stripe.payments.succeeded`,
`stripe.payments.failed`, `stripe.payments.conflicts`,
`stripe.events.dead_lettered`, and `stripe.payment.posting_latency`.

For signature failures, verify the endpoint-specific secret, system clock, and
tolerance without logging the request body. For `ReviewRequired`, compare only
opaque CDO and Stripe identifiers. After correcting a permanent configuration or
reference error, use the established inbox/dead-letter recovery procedure.

## Refunds and reconciliation runbook

An authorized payment administrator can request a full or partial refund from
the staff Billing screen. CDO writes a durable `PatientRefund` intent before it
calls Stripe, scopes the request to the practice's connected account, and uses
the opaque refund reference as its idempotency key. Pending and completed refund
amounts reserve refundable value, so cumulative refunds cannot exceed the
settled payment. A failed request with no Stripe refund ID may be retried safely.

Synchronous Stripe acceptance does **not** change the patient ledger. Only a
verified `refund.*` webhook with `status=succeeded`, matching connected account,
PaymentIntent, amount, currency, and opaque reference posts the immutable Refund
ledger entry. The original payment remains intact. Active allocations are
unapplied or replaced with a reduced active allocation so their complete history
is retained. Failed refunds create no refund ledger entry. Mismatches are marked
`ReviewRequired`.

Use **Billing → Stripe reconciliation → Run 30-day reconciliation** to compare
CDO payments/refunds with Stripe direct-charge objects. It reports missing or
unknown payments, amount/currency mismatches, payments pending over 24 hours,
refund mismatches, and disconnected accounts. The view contains only sanitized
opaque reference suffixes. Reconciliation never repairs or posts financial
records automatically; investigate every discrepancy before taking a separate,
audited action.

Operational response:

1. For `stripe-account-disconnected`, restore the tenant's Connect configuration
   before retrying remote operations.
2. For amount/currency/refund mismatches, compare the connected-account Stripe
   object and the CDO immutable record; do not edit ledger history.
3. For failed refunds, verify the safe failure code and Stripe balance, then use
   an authorized retry only when no external refund ID exists.
4. For dead-lettered refund events, fix configuration/reference mapping and use
   the existing durable-inbox replay procedure. Never paste webhook bodies,
   patient details, or card data into logs or support tickets.

Stripe allows multiple partial refunds up to the original charge and may report
refunds as pending, succeeded, failed, or canceled. Because CDO uses Connect
direct charges, refund API reads and writes must include the connected-account
context. See Stripe's [refund guidance](https://docs.stripe.com/refunds) and
[Connect direct-charge guidance](https://docs.stripe.com/connect/direct-charges).

## Patient notifications

When `Payments:Notifications:Enabled` is true, CDO queues durable, idempotent email
notifications for a new statement, due balance, received payment, or failed
payment. Messages name the practice and direct the patient to authenticated CDO
Billing. They contain no balance, procedure, diagnosis, treatment, insurance,
claim, patient name, or patient identifier. Delivery uses the existing SMTP
configuration and lease/retry worker. Missing or invalid email addresses are
suppressed without logging the address. The repository has no SMS sender, so SMS
is intentionally not simulated through an ungoverned provider.

No anonymous payment-link tokens are issued. Patients authenticate to the portal
before CDO creates a short-lived Stripe-hosted Checkout Session.

## Stripe data-flow inventory

| Operation | Data sent to Stripe |
|---|---|
| Connect onboarding | Practice administrator contact email; connected-account configuration/capabilities; HTTPS return/refresh URLs |
| Checkout | Connected account ID; amount in minor units; ISO currency; generic `Account payment` product; safe return URLs; opaque random payment reference/idempotency key |
| Refund | Connected account ID; Stripe PaymentIntent ID; amount; supported generic reason; opaque refund reference/idempotency key |
| Reconciliation | Connected account context, creation-time filter/cursor, and Stripe object IDs |

CDO does not send patient name/email, CDO patient/account/statement IDs, date of
birth, diagnosis, procedure or tooth data, insurance/claim data, or clinical text
in Stripe metadata, descriptions, statement descriptors, or idempotency keys.
Stripe returns object state through signed webhooks. Raw webhook bodies and payment
method details are not logged.

## PCI and security controls

- Stripe-hosted Checkout collects payment details. CDO never renders PAN/CVC fields
  and never receives or stores raw card data.
- API and webhook secrets are server-only environment/Key Vault values; database
  rows contain credential references and connected account IDs only.
- Webhook verification uses the unmodified body, endpoint-specific secret, signed
  timestamp, and replay tolerance before durable inbox acceptance.
- Rotate API keys by deploying a replacement from the secret manager, validating
  readiness, then expiring the old key. Rotate webhook secrets using Stripe's
  overlap window. Review live-key access and Stripe audit logs periodically.
- Use restricted keys and IP restrictions where deployment architecture permits.

## Pilot readiness

**Settings → Payments → Stripe** reports Connect/charges/payouts state, webhook
health, last successful payment event, pending and failed Stripe inbox counts, and
the last reconciliation result. Counts come from IntakeService through a
tenant-bound service credential with `channel=Stripe`, so unrelated integration
events cannot affect Stripe status.

Configure `Payments:StripeReadiness:IntakeServiceBaseUrl` and inject
`Payments:StripeReadiness:IntakeServiceKey` from a secret provider. A clean
reconciliation and recent signed event are required for pilot-ready status. The
manual **Stripe Pilot Validation** GitHub workflow is protected by the
`stripe-sandbox` environment. It verifies sandbox account access and runs the
mocked CDO lifecycle tests without exposing secrets to pull requests. Hosted
Checkout completion remains a manual pilot-checklist step.

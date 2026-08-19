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

The referenced value is loaded server-side through configuration, which in production can be backed by Azure Key Vault. Sandbox credentials must start with `sk_test_`; production credentials must start with `sk_live_`. Keys, identity data, and bank data must never be entered into CDO forms or stored in its database.

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
- A future webhook integration should consume Accounts v2 requirement-update events and verify signatures before updating cached status.

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
`checkout.session.async_payment_failed`. A completed session is posted only when
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
```

Never commit the secret. Dashboard, test/live, and Stripe CLI endpoints each have
distinct secrets.

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

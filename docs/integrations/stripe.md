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

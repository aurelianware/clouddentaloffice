# Patient email

CDO emails patients for billing notices, review invitations and coverage intake links.
All three go out over SMTP from one platform address, shown under the practice's
name, with replies going to the practice:

```
From:     3rd Set Smiles <no-reply@clouddental.io>
Reply-To: info@3rdsetsmiles.com
```

Each practice's mail therefore needs no mailbox credentials or DNS changes of its own.

## Why clouddental.io

- Patients see the practice's name first, and replies reach the practice.
- `clouddental.io` carries no other mail, so its reputation is this mail's alone, and
  company mail on `aurelianware.com` isn't put at risk.
- A domain may have only one SPF record. If mailboxes are ever added on `clouddental.io`
  (e.g. Google Workspace), combine them into it:
  `v=spf1 include:spf.protection.outlook.com include:_spf.google.com -all`.
- These emails reveal that someone is a patient with an appointment, so the provider
  must be covered by a BAA. Azure Communication Services is covered by Microsoft's.

## Setup (Azure Communication Services Email)

1. In Azure, create an **Email Communication Service** (`clouddental` in `cdo-prod-rg`,
   United States data), then add the custom domain `clouddental.io`.
2. In Cloudflare DNS for `clouddental.io`, add the records Azure shows: the verification
   TXT, the SPF TXT, and the two DKIM CNAMEs (**DNS only**, not proxied). Then verify
   each record in Azure.
3. Add DMARC at `_dmarc.clouddental.io`, starting with
   `v=DMARC1; p=none; rua=mailto:<reports address>`. Tighten it to `p=quarantine` once
   the reports show SPF and DKIM passing.
4. Create a **Communication Services** resource and connect the verified domain to it.
   Add a sender username `no-reply`. (Production: `cdo-comms`, SMTP username
   `cdo-portal-smtp`, Entra app `cdo-acs-smtp`, secret expiring 2028-09-28.)
5. For SMTP: create an Entra app registration with a client secret, give it the
   Communication Services role for sending email on that resource, and create an
   **SMTP Username** for it under the resource's Email → SMTP settings.
6. Set these as GitHub **repository** variables and secret (the deploy workflow has no
   environment), then deploy:

| Name | Kind | Value |
|---|---|---|
| `EMAIL_SMTP_HOST` | variable | `smtp.azurecomm.net` |
| `EMAIL_SMTP_PORT` | variable | `587` (default) |
| `EMAIL_SMTP_USERNAME` | variable | The SMTP username from step 5 |
| `EMAIL_SMTP_PASSWORD` | secret | The Entra app's client secret |
| `EMAIL_FROM_ADDRESS` | variable | `no-reply@clouddental.io` |
| `PRACTICE_REPLY_TO` | variable | `info@3rdsetsmiles.com` (the initial practice) |

With `EMAIL_SMTP_HOST` or `EMAIL_FROM_ADDRESS` empty, email stays off (mode `Disabled`).
Each feature still has its own switch: billing notifications
(`Payments:Notifications:Enabled`), review outreach (per-practice settings) and
coverage intake (`COVERAGE_INTAKE_ENABLED`).

## Check it

Send a coverage intake or billing email to an outside mailbox. In the raw message
("Show original" in Gmail), SPF, DKIM and DMARC should all say PASS for
`clouddental.io`. The From line should show the practice's name, and Reply-To the
practice's address.

## Adding practices

The Bicep sets the reply-to for the initial practice only. For another practice, add
`PracticeEmail__Practices__<n>__TenantId` and `PracticeEmail__Practices__<n>__ReplyTo`
on the portal. Without one, patients can still receive mail but replies go nowhere
useful, so set it before enabling features for that practice.

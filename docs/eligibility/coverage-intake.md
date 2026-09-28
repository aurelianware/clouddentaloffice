# Coverage intake links

When an upcoming appointment has no dental coverage on file, the Portal emails the
patient a signed link. The link opens a short form on the public IntakeService where
the patient types in their dental plan or says they don't have one. This is PR 4 in
[verification-design.md](verification-design.md), without card photos (PR 5).

## Flow

```
Portal coverage worker ── email with signed link ──► patient
patient ── GET/POST /coverage/{token} ──► IntakeService (public)
IntakeService ── CoverageIntakeSubmittedEvent ──► Service Bus topic coverage-intake
Service Bus subscription portal ──► Portal CoverageIntakeConsumer ──► CoverageIntakeRequests
```

- **The link is the credential.** New patients have no portal login. The token is
  HMAC-signed with a key both services share and carries only the tenant slug, the
  practice name, a request ID and an expiry (7 days by default). It never contains
  patient data.
- **The staff portal stays private.** Only IntakeService serves the page, like the
  website booking path.
- **IntakeService stores nothing.** The answer is published straight to Service Bus;
  if the broker is down the patient sees "please try again shortly". The intake
  database gets no patient data.
- **The Portal re-checks the token** when it consumes the answer, accepts the first
  answer per request, and ignores later ones. Forged, mismatched or stale answers are
  dead-lettered.

## What staff see on Insurance to Verify

| Situation | Status | What to do |
|---|---|---|
| Emailed, no answer yet | Needs correction | "Asked the patient by email on 10/5; no answer yet." |
| One reminder | | Sent once, 48 hours before the visit, if unanswered |
| No email on file | Needs correction | Call the patient |
| Email couldn't be delivered (after 3 tries) | Needs correction | Call the patient |
| Patient typed a plan | Needs correction | Open **Insurance**, review, **Fill in these details**, pick the payer, add |
| Patient has no dental insurance | Self-pay | Hidden from the list; lasts 180 days or until coverage is added |

A typed plan is never turned into coverage automatically: carrier names don't map
safely to clearinghouse payer IDs without a person.

## Turning it on

Requires the coverage verification worker (repository variable `COVERAGE_VERIFICATION_ENABLED=true`,
which the deploy passes to the Portal as `CoverageVerification__Enabled`) and an email transport
(`ReviewOutreach:Email`, shared with billing notifications). The worker checks only practices
with an active clearinghouse connection.

The deploy workflow wires everything; set these on the GitHub environment:

| Name | Kind | Value |
|---|---|---|
| `COVERAGE_INTAKE_SIGNING_KEY` | secret | Random, at least 32 bytes. Shared by both apps. |
| `COVERAGE_INTAKE_LINK_BASE_URL` | variable | IntakeService's public HTTPS origin, e.g. `https://book-api.3rdsetsmiles.com`. No path, query or credentials. |
| `COVERAGE_INTAKE_ENABLED` | variable | `true` |
| `COVERAGE_VERIFICATION_ENABLED` | variable | `true` (the worker that also sends the links) |

`main.bicep` creates the `coverage-intake` topic, its `portal` subscription, and a
listen-only rule `portal-listen` on that topic. The workflow passes that rule's
connection to the Portal as `CoverageIntake__ServiceBusConnectionString`, so the
Portal's other Service Bus consumers stay off. IntakeService publishes with its
existing send connection.

Use a branded domain for the link; a generated Container Apps hostname looks like
phishing in an email asking for a member ID.

**Switching it off** stops the emails and the Portal's consumer. Answers already
submitted wait on the subscription and are applied if intake is switched back on
within 14 days; after that they move to the dead-letter queue.

Rotating the key invalidates links already sent; patients who haven't answered get a
fresh link after their current one expires.

## Limits

- Email only; no SMS yet.
- The Zocdoc insurance hint isn't captured yet. It needs a real Zocdoc payload first.
- A plan the patient typed for the wrong person or a medical plan still reaches staff
  as typed; the check after staff add it catches "not dental" as usual.

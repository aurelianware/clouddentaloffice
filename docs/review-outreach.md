# Post-visit review outreach

CloudDentalOffice schedules a neutral review invitation only when the authoritative Portal appointment changes to `Completed`.

`Completed appointment → eligibility → ReviewOutreach row → tenant delay → revalidation → email → tenant landing page → Google`

The database row is the durable job. A unique `(TenantId, AppointmentId, Campaign)` index makes repeated completion processing idempotent. The worker leases due rows, survives restarts, and retries transient delivery failures with bounded exponential backoff (three attempts by default). It reads the current appointment, patient contact, patient status, and tenant settings immediately before delivery. A reversed appointment, disabled tenant, inactive patient, or removed/invalid email suppresses the pending message.

## Tenant configuration

Create one `ReviewOutreachSettings` row per tenant. `Enabled` is the kill switch; disabling it prevents new scheduling and suppresses pending work at send time. Configure `DelayMinutes`, `SenderName`, `ReviewLandingPageUrl`, and `GoogleReviewUrl`. Both URLs must be absolute HTTP(S) URLs. The Google URL is validated as required configuration even though email links to the neutral landing page. No real Google URL is committed for 3rd Set Smiles; deployment must supply the verified destination before enabling the feature.

The `InitialTenant:ReviewOutreach` section can bootstrap a new tenant. It defaults disabled. Production email uses `ReviewOutreach:Email` with `Mode=Smtp`, `Host`, `Port`, `EnableSsl`, `FromAddress`, and optional credentials supplied through secrets. Missing transport configuration fails safely. Development uses a log-only sink and never sends mail.

## Privacy and policy

The invitation is identical for every eligible active patient and does not evaluate satisfaction, treatment, provider judgment, payment, or predicted sentiment. Review gating is prohibited. Subject and body contain only the practice name and neutral wording; diagnosis, procedure, reason, patient/appointment IDs, insurance data, and patient identity are absent from URLs and logs. The system records scheduling/delivery state but does not infer who posted a Google review or scrape reviews.

There is no communication opt-out field in the current patient model. That policy is isolated in `IReviewOutreachEligibilityService` so a future consent/suppression model can be honored centrally. Delivery is behind `IReviewOutreachSender`; SMS can be added as another channel without changing scheduling or eligibility.

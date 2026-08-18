# Zocdoc partner and certification checklist

Record evidence links and dates; never paste credentials, tokens, webhook bodies, or
patient information into this document or a ticket.

## Partner access

- [ ] Sandbox Calendar Integration credentials and required scopes issued
- [ ] Production access, certification expectations, rate limits, and support path confirmed
- [ ] Sandbox and production webhook URLs registered by Zocdoc
- [ ] Webhook signing keys delivered through an approved secret channel
- [ ] Supported appointment actions and insurance requirements confirmed for this partner product

## Automated evidence

- [ ] `dotnet test src/Services/Zocdoc.IntegrationTests/Zocdoc.IntegrationTests.csproj` passes
- [ ] SchedulingService and IntakeService unit tests pass
- [ ] Readiness is green with `probeAuthentication=true`
- [ ] Reconciliation contains no unexplained failed, pending, dangling, stale, or conflict records

## Partner-assisted sandbox scenarios

- [ ] New-patient booking created and confirmed
- [ ] Existing-patient booking created and confirmed without duplicate patient creation
- [ ] Booking failure is visible and sanitized
- [ ] Cancellation works in both directions
- [ ] Reschedule works in both directions
- [ ] No-show and arrived status updates follow Zocdoc timing rules
- [ ] Multiple locations map and publish independently
- [ ] Required-insurance, self-pay-not-accepted, in-network-only, and invalid-plan cases verified if enabled
- [ ] Unavailable/racing slot is rejected without duplicate appointment creation
- [ ] Duplicate/replayed webhook is idempotent
- [ ] Malformed and invalid-signature webhooks are rejected
- [ ] Invalid/expired OAuth credentials fail closed and alert
- [ ] Mock webhook reaches IntakeService, Service Bus, SchedulingService, and persisted appointment/reference

## Production pilot gate

- [ ] One pilot location and named providers/visit reasons selected
- [ ] Credential rotation owner and expiry reminder recorded
- [ ] Azure Monitor alerts cover API latency/failure, webhook validation, conflicts, and dead-letter depth
- [ ] Daily reconciliation owner and escalation path assigned
- [ ] Rollback is documented: disable tenant integration while retaining local scheduling
- [ ] Zocdoc partner approval/certification sign-off received

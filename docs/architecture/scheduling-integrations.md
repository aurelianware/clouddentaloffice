# Scheduling integrations

CloudDentalOffice exposes a vendor-neutral scheduling integration boundary for
external channels such as practice websites, Google booking, Zocdoc, and future
healthcare marketplaces. This foundation does not implement any vendor API.

```text
External scheduling channel
          ↓
ISchedulingChannelAdapter
          ↓
Canonical scheduling models
          ↓
ISchedulingAvailabilityService / ISchedulingBookingService
          ↓
CloudDentalOffice appointment, patient, provider, and location domains
```

## Boundary rules

- An adapter translates a vendor protocol into canonical providers,
  appointment types, availability slots, and booking commands.
- Adapters are registered through dependency injection and selected by
  `ISchedulingChannelAdapterResolver`. Adding a Zocdoc adapter must not require
  changes to core appointment application services.
- Vendor request shapes, credentials, webhook fields, and marketplace IDs must
  remain outside the core Appointment model.
- `PatientRelationship` (`New`, `Existing`, or `Unknown`) is routing input only.
  It is never proof of patient identity. `SchedulingBookingCommand` therefore
  requires a positive, internally resolved patient ID before an appointment can
  be created.
- Every configuration, mapping, event, and lookup includes `TenantId`. An
  external identifier alone can never select a tenant.

## Persistence

SchedulingService owns the integration and availability tables:

- `SchedulingIntegrationConfigurations` enables a channel per tenant and stores
  an environment plus a reference to credentials. Secrets are not stored in
  this table.
- `ExternalSchedulingResourceMappings` maps external provider, location, and
  visit-reason IDs to canonical internal IDs. Each mapping is tenant- and
  channel-scoped, has active/inactive lifecycle state, and may retain the
  external display name shown during setup.
- `SchedulingAppointmentTypes` stores duration, optional provider/location
  applicability, new/existing-patient eligibility, and active state for the
  canonical visit type.
- `SchedulingProviderWorkingHours` stores provider/location working intervals
  as practice-local wall-clock times and a day of week.
- `SchedulingBlockedTimes` stores tenant-scoped UTC intervals that can apply to
  a provider, a location, or the whole practice.
- `ExternalAppointmentReferences` associates appointments with external channel
  identifiers without adding vendor fields to Appointment.
- `SchedulingIntegrationEvents` enforces idempotency with a unique
  `(TenantId, Channel, ExternalEventId)` key. Retries return the existing lease
  and cannot create another appointment.

## Entity mappings

`ISchedulingEntityMappingService` is the channel-neutral application boundary
for forward and reverse lookup, upsert, deactivation, unmapped entities, and
invalid/stale mappings. Provider IDs are positive integers, location IDs are
GUIDs, and appointment-type IDs are bounded canonical strings. The service
validates the internal entity in the same tenant before writing and prevents an
active external identifier from selecting more than one internal entity.

Example:

```text
CDO appointment type: New Patient Comprehensive Exam
Duration: 90 min
New patient: yes
Existing patient: no

External mapping:
Channel: Zocdoc
Visit reason: zocdoc-visit-reason-101
```

Authenticated administration endpoints under
`/api/scheduling-integrations/{channel}/mappings` support listing, forward and
reverse lookup, create/update, deactivation, and unmapped/stale reports. Tenant
scope is taken from the authenticated token's tenant claim; callers cannot
choose a tenant in the URL or request body. These routes are never anonymous.

## Canonical external availability

`ISchedulingAvailabilityService` is the single CDO-owned calculation boundary
used by every external channel. It does not expose arbitrary calendar gaps and
does not call a marketplace API. The calculation:

1. requires an enabled tenant/channel configuration and applies its minimum
   lead time, maximum horizon, and IANA practice timezone;
2. selects active appointment types eligible for the requested
   `PatientRelationship`;
3. requires active channel mappings for the appointment type, provider, and
   location;
4. expands provider/location working hours into UTC using the practice
   timezone, including daylight-saving transitions;
5. fits the appointment-type duration at 15-minute candidate increments; and
6. removes overlaps with non-cancelled appointments and active blocked time.

Only approved booking requests create an Appointment and therefore block
availability. An unapproved `BookingRequest` is an intake/review record, not a
calendar hold. Legacy `AppointmentStatus.Requested` rows are likewise excluded
from collision blocking so current booking-request semantics remain unchanged.

The authenticated troubleshooting endpoint is:

```text
GET /api/scheduling-integrations/{channel}/availability
    ?from=<ISO-8601 offset>&to=<ISO-8601 offset>
    &providerId=<optional>&locationId=<optional>
    &appointmentTypeId=<optional>&patientRelationship=New|Existing
```

Slots remain channel-neutral and contain only tenant, provider, location,
appointment type, UTC start/end, and patient relationship. Structured summary
logs contain rule and collision counts but no patient-identifying fields.

## Existing public website flow

This boundary does not change the public booking workflow:

```text
3rdSetSmiles → IntakeService → Service Bus → BookingRequest → staff review
             → patient match → explicit approval → Appointment
```

Public website requests still create `BookingRequest` records, never placeholder
patients or appointments. The immutable request/event identifier remains the
idempotency key. SchedulingService continues to consume those events privately,
and 3rdSetSmiles remains a thin external client.

## Adding a future adapter

1. Implement `ISchedulingChannelAdapter` in an integration-specific assembly.
2. Register the adapter with DI.
3. Configure the channel for the tenant with a secure credential reference.
4. Translate external resources into canonical models and use the scheduling
   application-service contracts.
5. Use `ISchedulingIntegrationIdempotencyStore` before processing each webhook
   or booking retry, and persist an `ExternalAppointmentReference` after the
   internally validated booking succeeds.

The first Zocdoc implementation must add protocol-specific validation,
authentication, availability publication, webhook handling, reconciliation,
and contract tests without leaking Zocdoc types into the scheduling domain.

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

SchedulingService owns four integration tables:

- `SchedulingIntegrationConfigurations` enables a channel per tenant and stores
  an environment plus a reference to credentials. Secrets are not stored in
  this table.
- `ExternalSchedulingResourceMappings` maps external provider, location, and
  visit-reason IDs to canonical internal IDs.
- `ExternalAppointmentReferences` associates appointments with external channel
  identifiers without adding vendor fields to Appointment.
- `SchedulingIntegrationEvents` enforces idempotency with a unique
  `(TenantId, Channel, ExternalEventId)` key. Retries return the existing lease
  and cannot create another appointment.

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

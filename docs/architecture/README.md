# Architecture

## Create-appointment flow

![Create-appointment flow: a website booking crosses the trust boundary once as a published event and becomes a staff-reviewed booking request on the private network](./create-appointment-flow.svg)

A booking submitted on a practice website (e.g. [3rd Set Smiles](https://github.com/aurelianware/3rdsetsmiles))
becomes a staff-reviewed booking request **without patient data ever crossing to the public internet**:

1. The website posts to the public **IntakeService**, which authenticates
   (constant-time API key), validates, rate-limits, and **publishes a
   `BookingRequestedEvent`** to the `booking-requests` Service Bus topic — then
   returns `202 Accepted`. It has **no database and no PHI**.
2. On the private network, the **SchedulingService** `BookingRequestConsumer`
   reads the `scheduling` subscription and persists a durable **`BookingRequest`**
   (`Status = New`) for explicit staff review. It **never creates an appointment**
   and needs no placeholder patient/provider. Duplicate events are ignored
   (idempotent by event id); invalid or unhandled messages are dead-lettered.
3. Staff review the request in the Portal — match the patient and approve —
   which creates the appointment (`Scheduled`).

Only one arrow crosses the trust boundary — the published event — and nothing
flows back up, so the internet-facing tier carries no PHI.

**Resilience.** If the SchedulingService is down, the event waits durably in the
subscription (nothing is lost). If the broker is unreachable or unconfigured,
IntakeService returns `503` and the website falls back to emailing the practice.

### Source

- `src/Services/IntakeService` — the public endpoint
- `src/Services/SchedulingService/BookingRequestConsumer.cs` — the private consumer
  (persists via `BookingRequestWorkflow`); staff review through the
  `/api/booking-requests` admin API and Portal
- `src/Shared/CloudDentalOffice.Messaging` — the Service Bus publisher abstraction
- `src/Shared/CloudDentalOffice.Contracts/Events/IntegrationEvents.cs` — `BookingRequestedEvent`

> The diagram is a hand-authored SVG (`create-appointment-flow.svg`) themed to
> match the Sentinel Portal. Edit the SVG directly to update it.

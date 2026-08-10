# Cloud Dental Office



**Modern SaaS Practice Management Platform for Dental Providers**

A cloud-native, microservices-based dental practice management system built from the ground up with .NET 8, Blazor Server and deep payer interoperability.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](https://opensource.org/licenses/Apache-2.0)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com)
[![Blazor Server](https://img.shields.io/badge/Blazor_Server-Powered-blue)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![Architecture](https://img.shields.io/badge/Architecture-Microservices-green)](https://microservices.io)

---

## Architecture

Cloud Dental Office uses a **microservices architecture** with each bounded context deployed as an independent service:

```
┌─────────────────────────────────────────────────────────────┐
│                    Blazor Server Portal                      │
│         (MudBlazor · Dark Theme · Real-time · AI Vision)     │
└──────────────────────────┬──────────────────────────────────┘
                           │
                    ┌──────┴──────┐
                    │ API Gateway │  ← YARP Reverse Proxy
                    │   :5200     │
                    └──────┬──────┘
         ┌─────────┬───────┼───────┬──────────┬──────────┬──────────┐
         │         │       │       │          │          │          │
    ┌────┴───┐ ┌───┴────┐ ┌┴─────┐ ┌┴────────┐ ┌┴──────┐ ┌┴─────┐ ┌┴─────────┐
    │Patient │ │Schedule│ │Claims│ │Eligiblty│ │  ERA  │ │ Auth │ │Rx (EPCS) │
    │Service │ │Service │ │Svc   │ │Service  │ │Service│ │  Svc │ │ :5107    │
    │ :5101  │ │ :5102  │ │:5103 │ │ :5104   │ │ :5105 │ │:5106 │ └─────┬────┘
    └────┬───┘ └───┬────┘ └┬─────┘ └┬────────┘ └┬──────┘ └┬─────┘       │
         │         │       │        │           │         │             │
         │         │       │        │           │     ┌───┴──────┐      │
         │         │       │        │           │     │AI Vision │      │
         │         │       │        │           │     │ Service  │      │
         │         │       │        │           │     │ :5108    │      │
         │         │       │        │           │     └────┬─────┘      │
         │         │       │        │           │          │            │
         └─────────┴───────┴────────┴───────────┴──────────┴────────────┘
                        PostgreSQL (per-service DB)
              ┌──────────────────────┴────────────────────┐
              │  privaseeAI Edge Devices (IP Cameras,     │
              │  Tablets, Raspberry Pi) + Azure AI Vision │
              └───────────────────────────────────────────┘
```

### Services

| Service | Port | Description |
|---------|------|-------------|
| **Portal** | 5000 | Blazor Server UI — dashboard, patient management, claims, scheduling, e-prescribing, AI vision |
| **API Gateway** | 5200 | YARP reverse proxy routing to all backend services |
| **PatientService** | 5101 | Patient demographics, insurance/subscriber info, search |
| **SchedulingService** | 5102 | Appointments, operatory management, provider calendars |
| **ClaimsService** | 5103 | Claim lifecycle (draft → submit → adjudicate), 837D generation |
| **EligibilityService** | 5104 | Real-time 270/271 eligibility verification |
| **EraService** | 5105 | 835 ERA file processing, claim matching, auto-posting |
| **AuthService** | 5106 | JWT authentication, OpenID Connect, multi-tenant identity |
| **PrescriptionService** | 5107 | e-Prescribing with DoseSpot integration, EPCS compliance, Surescripts certified |
| **VisionService** | 5108 | AI vision platform — privaseeAI integration, insurance card OCR (Azure AI Vision), narcotics cabinet monitoring, consent recording, clinical note generation |

### Shared Libraries

- **CloudDentalOffice.Contracts** — DTOs, integration events, and API contracts shared across services
- **CloudDentalOffice.EdiCommon** — Clean-room X12 EDI parser and generators (837D, 270/271, 835)

---

## Public Website Booking Intake

The `SchedulingService` exposes an internet-facing endpoint for practice
websites (e.g. [3rd Set Smiles](https://github.com/aurelianware/3rdsetsmiles)) to
submit appointment requests, **without** exposing the internal admin endpoints.

```
POST /api/public/booking-requests
```

How it differs from the internal `POST /api/appointments`:

- **Disabled by default** — returns `404` unless `PublicBooking:Enabled` is `true`.
- **Requires an API key** — `Authorization: Bearer <key>` or `X-Api-Key: <key>`,
  compared in constant time. Missing/wrong key → `401`.
- **No client-supplied identifiers** — the request body carries only patient
  contact details and a preferred time. Provider, location, and the placeholder
  "web intake" patient are resolved **server-side** from configuration, so a
  caller cannot target arbitrary records.
- **Marks intakes as `Requested`** (a new `AppointmentStatus`) — unconfirmed
  until staff confirm, rather than landing on the calendar as `Scheduled`.
- **Rate limited** — fixed window, 5 requests/minute per client IP
  (keyed on `X-Forwarded-For`).

Request body (`PublicBookingRequest`):

```jsonc
{
  "name": "Jane Doe",           // required
  "phone": "480-555-0100",      // required
  "email": "jane@example.com",  // optional
  "preferredStart": "2026-08-20T21:00:00Z", // required, UTC ISO-8601, must be future
  "durationMinutes": 60,        // optional; falls back to PublicBooking:DefaultDurationMinutes
  "reason": "New patient exam", // optional
  "message": "Second choice: Friday" // optional
}
```

Success returns `201 Created` with a minimal confirmation
(`{ id, status, startTime, endTime }`) — no internal fields are echoed. Contact
details are stored in the appointment `notes` for staff follow-up.

### Configuration

Set these under `PublicBooking` (via `appsettings.json`, environment variables,
or user secrets). Environment-variable form uses `PublicBooking__ApiKey`, etc.

| Key | Purpose |
|-----|---------|
| `PublicBooking:Enabled` | Master switch (default `false`). |
| `PublicBooking:ApiKey` | Shared secret the website sends. **Required when enabled.** |
| `PublicBooking:ProviderId` | GUID of the default provider. |
| `PublicBooking:LocationId` | GUID of the location (blank/zero → null). |
| `PublicBooking:PatientId` | GUID of a shared "Web Booking" placeholder patient. |
| `PublicBooking:DefaultDurationMinutes` | Appointment length when the request omits one (default `60`). |
| `PublicBooking:RequireApiKeyForReads` | When `true`, `GET /api/appointments` and `GET /api/appointments/{id}` also require the API key (default `false`). |

When `Enabled`, the endpoint validates configuration and returns `500` if
`ProviderId`/`PatientId` are unset or `DefaultDurationMinutes <= 0`, rather than
persisting an invalid appointment. `preferredStart` must carry a timezone
(UTC `Z` or an explicit offset); a value with no timezone is rejected.

### Deployment boundary (important)

The `SchedulingService` read endpoints (`GET /api/appointments*`) are anonymous
by default and return the full record, including the `Notes` that carry public
booking contact details. The Portal reaches them through the gateway on a
**private** network, so this is safe internally — but it means you must **not**
put the existing all-routes gateway directly on the internet.

To accept public bookings, expose **only** `/api/public/booking-requests` to the
internet (e.g. an edge path filter in Azure Front Door / Application Gateway, or
a dedicated public ingress), and keep the internal `/api/appointments*` routes on
the private gateway. As defense-in-depth for a shared/public gateway, also set
`PublicBooking:RequireApiKeyForReads=true` (the Portal must then present the key
on its scheduling calls). Add TLS + the API key at the edge. CORS is **not**
required — the website calls this server-to-server.

---

## Quick Start

### Docker Compose (recommended)

```bash
git clone https://github.com/aurelianware/clouddentaloffice.git
cd clouddentaloffice
docker-compose up -d
```

Portal: http://localhost:5000
API Gateway: http://localhost:5200
Swagger (per service): http://localhost:510x/swagger

### Local Development

```bash
# Prerequisites: .NET 8 SDK, PostgreSQL (or use SQLite default)

git clone https://github.com/aurelianware/clouddentaloffice.git
cd clouddentaloffice

# Restore and build all projects
dotnet restore CloudDentalOffice.sln
dotnet build CloudDentalOffice.sln

# Run individual services
dotnet run --project src/Services/PatientService
dotnet run --project src/Services/ClaimsService
dotnet run --project src/Services/ApiGateway
dotnet run --project src/CloudDentalOffice.Portal
```

Each service defaults to SQLite for local dev — no database setup required.

### Local Kubernetes

CloudDentalOffice runs in its own `clouddental` namespace. The local overlay uses
portable Kubernetes resources, an in-cluster PostgreSQL instance, and local images;
it does not depend on Azure or DigitalOcean services.

Prerequisites: Docker, `kubectl`, and a running local cluster. The helper recognizes
a kind cluster named `docker-desktop` by default and loads images into it. The helper
generates the local database password and JWT signing key directly in the cluster;
these credentials are not stored in the repository.

```bash
./scripts/deploy-local-k8s.sh
kubectl port-forward -n clouddental service/portal 5000:5000
```

Then open http://localhost:5000 and sign in with
`demo@clouddentaloffice.com` / `Password123!`.

Useful overrides:

```bash
# Re-apply manifests without rebuilding images
SKIP_BUILD=true ./scripts/deploy-local-k8s.sh

# Use another kind cluster name
KIND_CLUSTER_NAME=my-cluster ./scripts/deploy-local-k8s.sh
```

The manifests are under `infrastructure/k8s/local`. The reusable application
manifests remain under `infrastructure/k8s/clouddental` for cloud deployments.

#### CloudHealthOffice claim flow

The local portal configures the demo payer to submit to the CloudHealthOffice
claims service in the separate `cloudhealthoffice` namespace:

```text
CloudDentalOffice (clouddental)
  -> generate ASC X12 005010X224A2 837D
  -> POST multipart file to claims-service.cloudhealthoffice.svc.cluster.local
  -> /api/v1/claims/import/raw837
  -> CloudHealthOffice validation and adjudication pipeline
```

Override `CloudHealthOffice__BaseUrl` in the portal deployment when the payer
platform uses a different namespace or an external URL. CloudHealthOffice must
contain matching member, provider, and benefit configuration for the submitted
claim to adjudicate successfully; otherwise its raw-837 response is shown as a
claim-submission rejection in CloudDentalOffice.

---

## EDI / Payer Interoperability

Cloud Dental Office provides native support for dental EDI transactions:

| Transaction | Standard | Status |
|-------------|----------|--------|
| 837D Claims | ASC X12 005010X224A2 | ✅ Generator implemented |
| 270/271 Eligibility | ASC X12 005010X279A1 | 🔧 In progress |
| 835 ERA | ASC X12 005010X221A1 | 🔧 In progress |
| 276/277 Claim Status | ASC X12 005010X212 | 📋 Planned |
| 278 Prior Auth | ASC X12 005010X217 | 📋 Planned |

Designed to pair with **[Cloud Health Office](https://github.com/aurelianware/cloudhealthoffice)** for end-to-end provider ↔ payer automation.

---

## AI Vision Platform

Cloud Dental Office integrates with **privaseeAI** edge devices to provide intelligent vision capabilities for dental practices:

### Features

| Feature | Description | Status |
|---------|-------------|--------|
| **Insurance Card OCR** | Automatic extraction of member ID, payer info, group numbers from insurance cards using Azure AI Vision | ✅ Implemented |
| **Narcotics Cabinet Monitoring** | Real-time detection and compliance tracking for controlled substance access with badge verification | ✅ Implemented |
| **Patient Consent Recording** | Video-verified consent capture with detection of patient, provider, and consent forms | ✅ Implemented |
| **Clinical Note Generation** | AI-assisted procedure documentation from instrument detection and procedure observations | ✅ Implemented |
| **Real-time Detection** | SignalR hub for live camera feeds and event streaming | ✅ Implemented |
| **Device Management** | Multi-device registration and monitoring (IP cameras, tablets, Raspberry Pi, mobile devices) | ✅ Implemented |

### Detection Classes

The VisionService supports detection of:
- **Generic objects**: Person, Document, Cell Phone, Backpack, Handbag (COCO-SSD)
- **Dental instruments**: Handpiece, Mirror, Explorer, Forceps, Elevator, Scaler/Curette, Syringes, Suture Kit, Cotton Roll, Gauze, Impression Tray, Crown/Bridge, Dental Dam
- **Documents**: Insurance cards, consent forms, ID documents, prescription pads
- **Security**: Cabinet door status, badge scanning, medication vials, narcotics safe monitoring

### Architecture

```
┌───────────────────────────────────────────────────────────────┐
│                     privaseeAI Edge Devices                    │
│  (IP Cameras, Tablets, Raspberry Pi, Mobile Devices)          │
│                  ↓ Detection Events (HTTPS)                   │
└───────────────────────────────────────────────────────────────┘
                              ↓
┌───────────────────────────────────────────────────────────────┐
│                      VisionService (:5108)                     │
│  ┌─────────────────┐  ┌────────────────┐  ┌────────────────┐ │
│  │ Ingest Detections│→│Context Correlate│→│ Event Storage │ │
│  │  (REST API)      │  │   (Appt/Pt)     │  │  (PostgreSQL) │ │
│  └─────────────────┘  └────────────────┘  └────────────────┘ │
│                              ↓                                 │
│  ┌─────────────────┐  ┌────────────────┐  ┌────────────────┐ │
│  │ SignalR Hub      │  │  Azure AI      │  │ Alert Engine  │ │
│  │ (Real-time)      │  │  Vision OCR    │  │ (Compliance)  │ │
│  └─────────────────┘  └────────────────┘  └────────────────┘ │
└───────────────────────────────────────────────────────────────┘
                              ↓
┌───────────────────────────────────────────────────────────────┐
│                   Portal UI — AI Vision Pages                  │
│  • Vision Dashboard  • Device Management  • Events & Alerts    │
│  • Insurance Scans   • Consent Recording  • Cabinet Access     │
│  • Clinical Notes                                              │
└───────────────────────────────────────────────────────────────┘
```

### Configuration

The VisionService supports two modes:
- **Development/Mock**: Uses mock OCR and correlation providers for testing without external dependencies
- **Production**: Integrates with Azure AI Vision API and live correlation engine

Configure via `appsettings.json`:
```json
{
  "OcrProvider": "AzureAiVision",  // or "Mock"
  "CorrelationProvider": "Live",    // or "Mock"
  "AzureAiVision": {
    "Endpoint": "https://yourresource.cognitiveservices.azure.com/",
    "ApiKey": "your-api-key"
  }
}
```

---

## Project Structure

```
clouddentaloffice/
├── CloudDentalOffice.sln          # Solution file
├── Directory.Build.props          # Shared build settings
├── docker-compose.yml             # Full stack orchestration
├── src/
│   ├── CloudDentalOffice.Portal/  # Blazor Server UI (your existing Portal)
│   ├── CloudDentalOffice.Portal.Tests/
│   ├── Services/
│   │   ├── ApiGateway/            # YARP reverse proxy
│   │   ├── PatientService/        # Patient bounded context
│   │   ├── SchedulingService/     # Scheduling bounded context
│   │   ├── ClaimsService/         # Claims bounded context
│   │   ├── EligibilityService/    # 270/271 bounded context
│   │   ├── EraService/            # 835 bounded context
│   │   ├── AuthService/           # Identity bounded context
│   │   ├── PrescriptionService/   # e-Prescribing (DoseSpot, EPCS)
│   │   └── VisionService/         # AI Vision (privaseeAI, Azure AI)
│   └── Shared/
│       ├── CloudDentalOffice.Contracts/   # Shared DTOs & events
│       └── CloudDentalOffice.EdiCommon/   # X12 parser & generators
├── infrastructure/
│   ├── docker/                    # Per-service Dockerfiles
│   ├── k8s/                       # Kubernetes manifests
│   └── azure/                     # Azure Bicep/ARM templates
├── scripts/
│   └── seeds/                     # Database init & seed data
└── docs/                          # Architecture & API documentation
```

---

## Technology Stack

- **.NET 8** — All services and portal
- **Blazor Server** with **MudBlazor** — Responsive UI with dark theme
- **Entity Framework Core** — Multi-provider (PostgreSQL, SQL Server, SQLite)
- **YARP** — API Gateway / reverse proxy
- **Docker** + **Kubernetes** — Container orchestration
- **Azure** — Cloud deployment (Bicep IaC templates)
- **JWT / OpenID Connect** — Authentication and multi-tenant identity
- **SSH.NET** — SFTP for clearinghouse file exchange
- **DoseSpot** — Electronic prescription (eRx) and EPCS integration

---

## Roadmap

- [x] Microservices architecture with per-service databases
- [x] API Gateway with YARP
- [x] Patient, Scheduling, Claims, Eligibility, ERA, Auth services
- [x] e-Prescribing service with DoseSpot integration (EPCS, Surescripts)
- [x] AI Vision service — privaseeAI integration for dental practice automation
- [x] Insurance card OCR with Azure AI Vision
- [x] Narcotics cabinet monitoring and compliance tracking
- [x] Video consent recording and verification
- [x] Clinical note generation from procedure observations
- [x] Clean-room X12 837D claim generator
- [x] Docker Compose full-stack deployment
- [x] Blazor Portal integration with microservices via API Gateway
- [x] Kubernetes deployment manifests (DOKS)
- [x] CI/CD with GitHub Actions
- [x] Send 837D Claim transactions to payer SFTP
- [ ] Full 270/271 real-time eligibility checks
- [ ] 835 ERA auto-posting & reconciliation
- [ ] 276/277 claim status polling
- [ ] 278 prior authorization
- [ ] Multi-location / DSO support
- [ ] Azure AD B2C / OpenID Connect auth
- [ ] Kubernetes Helm charts
- [ ] Availity Integration
- [ ] Change Healthcare Integration

---

## Related Projects

- **[Cloud Health Office](https://github.com/aurelianware/cloudhealthoffice)** — Payer-side EDI platform (X12, FHIR R4, CMS-0057-F compliance)
- Together, Cloud Dental Office + Cloud Health Office provide complete provider ↔ payer interoperability

---

## License

[Apache License 2.0](LICENSE) — Copyright 2025 Aurelianware, Inc.

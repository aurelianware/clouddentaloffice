# Cloud Dental Office

**Modern SaaS Practice Management Platform for Dental Providers**

A cloud-native, microservices-based dental practice management system built from the ground up with .NET 8, Blazor Server, and deep payer interoperability.

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
│              (MudBlazor · Dark Theme · Real-time)            │
└──────────────────────────┬──────────────────────────────────┘
                           │
                    ┌──────┴──────┐
                    │ API Gateway │  ← YARP Reverse Proxy
                    │   :5200     │
                    └──────┬──────┘
         ┌─────────┬───────┼───────┬──────────┬──────────┐
         │         │       │       │          │          │
    ┌────┴───┐ ┌───┴────┐ ┌┴─────┐ ┌┴────────┐ ┌┴──────┐ ┌┴─────┐
    │Patient │ │Schedule│ │Claims│ │Eligiblty│ │  ERA  │ │ Auth │
    │Service │ │Service │ │Svc   │ │Service  │ │Service│ │  Svc │
    │ :5101  │ │ :5102  │ │:5103 │ │ :5104   │ │ :5105 │ │:5106 │
    └────┬───┘ └───┬────┘ └┬─────┘ └┬────────┘ └┬──────┘ └┬─────┘
         │         │       │        │           │         │
         └─────────┴───────┴────────┴───────────┴─────────┘
                        PostgreSQL (per-service DB)
```

### Services

| Service | Port | Description |
|---------|------|-------------|
| **Portal** | 5000 | Blazor Server UI — dashboard, patient management, claim wizard, scheduling |
| **API Gateway** | 5200 | YARP reverse proxy routing to all backend services |
| **PatientService** | 5101 | Patient demographics, insurance/subscriber info, search |
| **SchedulingService** | 5102 | Appointments, operatory management, provider calendars |
| **ClaimsService** | 5103 | Claim lifecycle (draft → submit → adjudicate), 837D generation |
| **EligibilityService** | 5104 | Real-time 270/271 eligibility verification |
| **EraService** | 5105 | 835 ERA file processing, claim matching, auto-posting |
| **AuthService** | 5106 | JWT authentication, OpenID Connect, multi-tenant identity |

### Shared Libraries

- **CloudDentalOffice.Contracts** — DTOs, integration events, and API contracts shared across services
- **CloudDentalOffice.EdiCommon** — Clean-room X12 EDI parser and generators (837D, 270/271, 835)

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
│   │   └── AuthService/           # Identity bounded context
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

---

## Roadmap

- [x] Microservices architecture with per-service databases
- [x] API Gateway with YARP
- [x] Patient, Scheduling, Claims, Eligibility, ERA, Auth services
- [x] Clean-room X12 837D claim generator
- [x] Docker Compose full-stack deployment
- [ ] Blazor Portal integration with API Gateway
- [ ] Full 270/271 real-time eligibility checks
- [ ] 835 ERA auto-posting & reconciliation
- [ ] 276/277 claim status polling
- [ ] 278 prior authorization
- [ ] Multi-location / DSO support
- [ ] Azure AD B2C / OpenID Connect auth
- [ ] Kubernetes Helm charts
- [ ] CI/CD with GitHub Actions

---

## Related Projects

- **[Cloud Health Office](https://github.com/aurelianware/cloudhealthoffice)** — Payer-side EDI platform (X12, FHIR R4, CMS-0057-F compliance)
- Together, Cloud Dental Office + Cloud Health Office provide complete provider ↔ payer interoperability

---

## License

[Apache License 2.0](LICENSE) — Copyright 2025 Aurelianware, Inc.

// ─────────────────────────────────────────────────────────────────────────────
// API Gateway - VisionService Route Configuration
// ─────────────────────────────────────────────────────────────────────────────
//
// ROUTES (add to ApiGateway appsettings.json under ReverseProxy.Routes):
//
//   "vision-route": {
//     "ClusterId": "vision-cluster",
//     "Match": {
//       "Path": "/api/vision/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/api/vision/{**catch-all}" }
//     ]
//   }
//
// CLUSTERS (add under ReverseProxy.Clusters):
//
//   "vision-cluster": {
//     "Destinations": {
//       "destination1": {
//         "Address": "http://localhost:5108/"
//       }
//     }
//   }
//
// SIGNALR HUB ROUTE (add separate route for WebSocket upgrade):
//
//   "vision-hub-route": {
//     "ClusterId": "vision-cluster",
//     "Match": {
//       "Path": "/hubs/vision/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/hubs/vision/{**catch-all}" }
//     ]
//   }
//
// DOCKER-COMPOSE (add to services):
//
//   vision-service:
//     build:
//       context: .
//       dockerfile: infrastructure/docker/VisionService.Dockerfile
//     ports:
//       - "5108:5108"
//     environment:
//       - ASPNETCORE_ENVIRONMENT=Development
//       - DatabaseProvider=PostgreSQL
//       - ConnectionStrings__VisionDb=Host=postgres;Database=cdo_vision;Username=cdo;Password=cdo_dev_password
//       - OcrProvider=Mock
//       - CorrelationProvider=Mock
//       - ApiGatewayUrl=http://api-gateway:5200
//     depends_on:
//       - postgres
//     networks:
//       - cdo-network
//
// DATABASE INIT (add to scripts/seeds/init-databases.sql):
//
//   CREATE DATABASE cdo_vision;
//
// UPDATED ARCHITECTURE DIAGRAM:
//
//   ┌─────────────────────────────────────────────────────────────────────────┐
//   │                    Blazor Server Portal                                 │
//   │          (MudBlazor · Dark Theme · Real-time via SignalR)               │
//   └──────────────────────────┬──────────────────────────────────────────────┘
//                              │
//                       ┌──────┴──────┐
//                       │ API Gateway │  ← YARP Reverse Proxy
//                       │   :5200     │
//                       └──────┬──────┘
//        ┌─────────┬──────┬────┼────┬──────────┬──────────┬──────────┐
//        │         │      │    │    │          │          │          │
//   ┌────┴───┐ ┌───┴──┐ ┌┴───┐│┌───┴──┐ ┌────┴───┐ ┌───┴──┐ ┌────┴───┐
//   │Patient │ │Sched │ │Clms│││Eligb │ │  ERA   │ │ Auth │ │Prescrp │
//   │Service │ │Svc   │ │Svc ││├──────┤ │Service │ │  Svc │ │Service │
//   │ :5101  │ │:5102 │ │:5103││:5104 │ │ :5105  │ │:5106 │ │ :5107  │
//   └────────┘ └──────┘ └────┘│└──────┘ └────────┘ └──────┘ └────────┘
//                              │
//                        ┌─────┴─────┐
//                        │  Vision   │  ← NEW: privaseeAI bridge
//                        │  Service  │
//                        │  :5108    │
//                        └─────┬─────┘
//                              │ SignalR + REST
//                    ┌─────────┼─────────┐
//                    │         │         │
//               ┌────┴──┐ ┌───┴───┐ ┌───┴────┐
//               │Operatry│ │FrontDsk│ │Cabinet │  ← privaseeAI
//               │Camera  │ │Tablet  │ │Camera  │     Edge Devices
//               └────────┘ └───────┘ └────────┘
//
// ─────────────────────────────────────────────────────────────────────────────

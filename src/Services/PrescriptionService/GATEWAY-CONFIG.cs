// ─────────────────────────────────────────────────────────────────────────────
// API Gateway - PrescriptionService Route Configuration
// ─────────────────────────────────────────────────────────────────────────────
//
// Add the following to your ApiGateway appsettings.json under
// ReverseProxy.Routes and ReverseProxy.Clusters:
//
// ROUTES (add alongside existing patient-route, scheduling-route, etc.):
//
//   "prescription-route": {
//     "ClusterId": "prescription-cluster",
//     "Match": {
//       "Path": "/api/prescriptions/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/api/prescriptions/{**catch-all}" }
//     ]
//   },
//   "prescriber-route": {
//     "ClusterId": "prescription-cluster",
//     "Match": {
//       "Path": "/api/prescribers/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/api/prescribers/{**catch-all}" }
//     ]
//   },
//   "pharmacy-route": {
//     "ClusterId": "prescription-cluster",
//     "Match": {
//       "Path": "/api/pharmacies/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/api/pharmacies/{**catch-all}" }
//     ]
//   },
//   "patient-allergy-route": {
//     "ClusterId": "prescription-cluster",
//     "Match": {
//       "Path": "/api/patients/{patientId}/allergies/{**catch-all}"
//     },
//     "Transforms": [
//       { "PathPattern": "/api/patients/{patientId}/allergies/{**catch-all}" }
//     ]
//   }
//
// CLUSTERS (add alongside existing patient-cluster, scheduling-cluster, etc.):
//
//   "prescription-cluster": {
//     "Destinations": {
//       "destination1": {
//         "Address": "http://localhost:5107/"
//       }
//     }
//   }
//
// DOCKER-COMPOSE (add to services section):
//
//   prescription-service:
//     build:
//       context: .
//       dockerfile: infrastructure/docker/PrescriptionService.Dockerfile
//     ports:
//       - "5107:5107"
//     environment:
//       - ASPNETCORE_ENVIRONMENT=Development
//       - DatabaseProvider=PostgreSQL
//       - ConnectionStrings__PrescriptionDb=Host=postgres;Database=cdo_prescriptions;Username=cdo;Password=cdo_dev_password
//       - ErxProvider=Mock
//     depends_on:
//       - postgres
//     networks:
//       - cdo-network
//
// DATABASE INIT (add to scripts/seeds/init-databases.sql):
//
//   CREATE DATABASE cdo_prescriptions;
//
// ─────────────────────────────────────────────────────────────────────────────

// Container Apps module — called from main.bicep
// All 10 services are deployed into the same Container App Environment.
// Inter-service communication uses ACA internal ingress:
//   internal services are reachable at http://<app-name> (port 80 → targetPort)
//   from any other app in the same environment.

param location string
param environmentId string
param acrLoginServer string
param identityId string

@description('Docker image tag to deploy (defaults to "latest"; CI overrides with commit SHA)')
param imageTag string = 'latest'

@secure()
param connPortal string
@secure()
param connPatient string
@secure()
param connScheduling string
@secure()
param connClaims string
@secure()
param connPrescription string
@secure()
param connVision string
@secure()
param jwtKey string
param jwtIssuer string
param jwtAudience string

// Shared registry config — Managed Identity pulls from ACR (no admin credentials)
var registry = [
  {
    server: acrLoginServer
    identity: identityId
  }
]

var identityObj = {
  type: 'UserAssigned'
  userAssignedIdentities: {
    '${identityId}': {}
  }
}

// ── portal ────────────────────────────────────────────────────────────────────
// External HTTPS ingress — public-facing Blazor Server UI

resource portal 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'portal'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: true
        targetPort: 5000
        transport: 'http'
        allowInsecure: false
      }
      secrets: [
        { name: 'conn-default', value: connPortal }
        { name: 'jwt-key', value: jwtKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'portal'
          image: '${acrLoginServer}/portal:${imageTag}'
          resources: { cpu: '0.5', memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            // ACA internal ingress: api-gateway is reachable at http://api-gateway (port 80)
            { name: 'ApiGateway__BaseUrl', value: 'http://api-gateway' }
            { name: 'Microservices__Patient__Enabled', value: 'true' }
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'conn-default' }
            { name: 'Jwt__Key', secretRef: 'jwt-key' }
            { name: 'Jwt__Issuer', value: jwtIssuer }
            { name: 'Jwt__Audience', value: jwtAudience }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── api-gateway ───────────────────────────────────────────────────────────────
// Internal ingress only — YARP reverse proxy routing to all microservices

resource apiGateway 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'api-gateway'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5200
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'api-gateway'
          image: '${acrLoginServer}/api-gateway:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            // YARP cluster destinations override — internal ACA services use http://<name> (port 80)
            { name: 'ReverseProxy__Clusters__patient-cluster__Destinations__primary__Address', value: 'http://patient-service' }
            { name: 'ReverseProxy__Clusters__scheduling-cluster__Destinations__primary__Address', value: 'http://scheduling-service' }
            { name: 'ReverseProxy__Clusters__claims-cluster__Destinations__primary__Address', value: 'http://claims-service' }
            { name: 'ReverseProxy__Clusters__eligibility-cluster__Destinations__primary__Address', value: 'http://eligibility-service' }
            { name: 'ReverseProxy__Clusters__era-cluster__Destinations__primary__Address', value: 'http://era-service' }
            { name: 'ReverseProxy__Clusters__auth-cluster__Destinations__primary__Address', value: 'http://auth-service' }
            { name: 'ReverseProxy__Clusters__prescription-cluster__Destinations__primary__Address', value: 'http://prescription-service' }
            { name: 'ReverseProxy__Clusters__vision-cluster__Destinations__primary__Address', value: 'http://vision-service' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── patient-service ───────────────────────────────────────────────────────────

resource patientService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'patient-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5101
        transport: 'http'
      }
      secrets: [
        { name: 'conn-patient', value: connPatient }
      ]
    }
    template: {
      containers: [
        {
          name: 'patient-service'
          image: '${acrLoginServer}/patient-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__PatientDb', secretRef: 'conn-patient' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── scheduling-service ────────────────────────────────────────────────────────

resource schedulingService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'scheduling-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5102
        transport: 'http'
      }
      secrets: [
        { name: 'conn-scheduling', value: connScheduling }
      ]
    }
    template: {
      containers: [
        {
          name: 'scheduling-service'
          image: '${acrLoginServer}/scheduling-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__SchedulingDb', secretRef: 'conn-scheduling' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── claims-service ────────────────────────────────────────────────────────────

resource claimsService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'claims-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5103
        transport: 'http'
      }
      secrets: [
        { name: 'conn-claims', value: connClaims }
      ]
    }
    template: {
      containers: [
        {
          name: 'claims-service'
          image: '${acrLoginServer}/claims-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__ClaimsDb', secretRef: 'conn-claims' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── eligibility-service ───────────────────────────────────────────────────────

resource eligibilityService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'eligibility-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5104
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'eligibility-service'
          image: '${acrLoginServer}/eligibility-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── era-service ───────────────────────────────────────────────────────────────

resource eraService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'era-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5105
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'era-service'
          image: '${acrLoginServer}/era-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── auth-service ──────────────────────────────────────────────────────────────

resource authService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'auth-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5106
        transport: 'http'
      }
      secrets: [
        { name: 'jwt-key', value: jwtKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'auth-service'
          image: '${acrLoginServer}/auth-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Jwt__Key', secretRef: 'jwt-key' }
            { name: 'Jwt__Issuer', value: jwtIssuer }
            { name: 'Jwt__Audience', value: jwtAudience }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── prescription-service ──────────────────────────────────────────────────────

resource prescriptionService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'prescription-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5107
        transport: 'http'
      }
      secrets: [
        { name: 'conn-prescription', value: connPrescription }
      ]
    }
    template: {
      containers: [
        {
          name: 'prescription-service'
          image: '${acrLoginServer}/prescription-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__PrescriptionDb', secretRef: 'conn-prescription' }
            { name: 'ErxProvider', value: 'Mock' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 5107 }
              initialDelaySeconds: 30
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: 5107 }
              initialDelaySeconds: 20
              periodSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── vision-service ────────────────────────────────────────────────────────────

resource visionService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'vision-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: {
        external: false
        targetPort: 5108
        transport: 'http'
      }
      secrets: [
        { name: 'conn-vision', value: connVision }
      ]
    }
    template: {
      containers: [
        {
          name: 'vision-service'
          image: '${acrLoginServer}/vision-service:${imageTag}'
          resources: { cpu: '0.25', memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__VisionDb', secretRef: 'conn-vision' }
            { name: 'OcrProvider', value: 'Mock' }
            { name: 'CorrelationProvider', value: 'Mock' }
            // ACA internal ingress: api-gateway reachable at http://api-gateway
            { name: 'ApiGatewayUrl', value: 'http://api-gateway' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 5108 }
              initialDelaySeconds: 30
              periodSeconds: 10
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: 5108 }
              initialDelaySeconds: 20
              periodSeconds: 5
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

@description('Public FQDN of the portal — use this as your app URL')
output portalFqdn string = portal.properties.configuration.ingress.fqdn

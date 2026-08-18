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
param connIntake string
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
@secure()
param serviceBusSendConnection string
@secure()
param serviceBusListenConnection string
@secure()
param publicBookingApiKey string
@secure()
param patientServiceApiKey string = ''
@secure()
param publicSchedulingServiceApiKey string
@secure()
param publicAvailabilitySlotKey string
param zocdocWebhookIntegrationId string = ''
@secure()
param zocdocWebhookSecret string = ''
@secure()
param integrationInboxAdminApiKey string = ''
param initialTenantId string = 'third-set-smiles'
param googleOAuthClientId string = ''
@secure()
param googleOAuthClientSecret string = ''
param cloudHealthOfficeBaseUrl string
@secure()
param cloudHealthOfficeApiKey string
param cloudHealthOfficeBenefitPlanId string
param cloudHealthOfficePayerId string = '00001'

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
        { name: 'google-oauth-client-secret', value: googleOAuthClientSecret }
        { name: 'cloudhealthoffice-api-key', value: cloudHealthOfficeApiKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'portal'
          image: '${acrLoginServer}/portal:${imageTag}'
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Database__Provider', value: 'PostgreSQL' }
            { name: 'Database__UseMigrations', value: 'false' }
            // ACA internal ingress: api-gateway is reachable at http://api-gateway (port 80)
            { name: 'ApiGateway__BaseUrl', value: 'http://api-gateway' }
            { name: 'Microservices__Patient__Enabled', value: 'true' }
            { name: 'ConnectionStrings__DefaultConnection', secretRef: 'conn-default' }
            { name: 'Jwt__Key', secretRef: 'jwt-key' }
            { name: 'Jwt__Issuer', value: jwtIssuer }
            { name: 'Jwt__Audience', value: jwtAudience }
            { name: 'InitialTenant__Enabled', value: 'true' }
            { name: 'InitialTenant__TenantId', value: initialTenantId }
            { name: 'InitialTenant__Name', value: '3rd Set Smiles' }
            { name: 'InitialTenant__Domain', value: '3rdsetsmiles.com' }
            { name: 'AzureAd__Enabled', value: 'false' }
            { name: 'StaffAuth__Enabled', value: !empty(googleOAuthClientId) ? 'true' : 'false' }
            { name: 'StaffAuth__TenantId', value: initialTenantId }
            { name: 'StaffAuth__Users__0__Email', value: 'matt@3rdsetsmiles.com' }
            { name: 'StaffAuth__Users__0__Role', value: 'Admin' }
            { name: 'StaffAuth__Users__1__Email', value: 'markus.phillips@gmail.com' }
            { name: 'StaffAuth__Users__1__Role', value: 'Admin' }
            { name: 'StaffAuth__Users__2__Email', value: 'cindy@3rdsetsmiles.com' }
            { name: 'StaffAuth__Users__2__Role', value: 'Admin' }
            { name: 'CloudHealthOffice__Enabled', value: 'true' }
            { name: 'CloudHealthOffice__BaseUrl', value: cloudHealthOfficeBaseUrl }
            { name: 'CloudHealthOffice__EstimatePath', value: '/api/v1/adjudication/estimate' }
            { name: 'CloudHealthOffice__ApiKey', secretRef: 'cloudhealthoffice-api-key' }
            { name: 'CloudHealthOffice__BenefitPlanMappings__${cloudHealthOfficePayerId}', value: cloudHealthOfficeBenefitPlanId }
            { name: 'PayerConnectivity__Payers__${cloudHealthOfficePayerId}__PaymentEstimate__0', value: 'CloudHealthOffice' }
          ]
        }
      ]
      // Staff UI stays warm to avoid login/review cold starts.
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

resource portalGoogleAuth 'Microsoft.App/containerApps/authConfigs@2024-03-01' = if (!empty(googleOAuthClientId)) {
  parent: portal
  name: 'current'
  properties: {
    platform: {
      enabled: true
    }
    globalValidation: {
      unauthenticatedClientAction: 'RedirectToLoginPage'
      redirectToProvider: 'google'
    }
    identityProviders: {
      google: {
        registration: {
          clientId: googleOAuthClientId
          clientSecretSettingName: 'google-oauth-client-secret'
        }
        validation: {
          allowedAudiences: [ googleOAuthClientId ]
        }
      }
    }
    login: {
      tokenStore: {
        enabled: false
      }
    }
    httpSettings: {
      requireHttps: true
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
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
      // Portal calls this on every private API request, so keep one warm.
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
        { name: 'internal-api-key', value: patientServiceApiKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'patient-service'
          image: '${acrLoginServer}/patient-service:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__PatientDb', secretRef: 'conn-patient' }
            { name: 'InternalApi__Clients__0__TenantId', value: initialTenantId }
            { name: 'InternalApi__Clients__0__ApiKey', secretRef: 'internal-api-key' }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
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
        { name: 'servicebus-listen', value: serviceBusListenConnection }
        { name: 'jwt-key', value: jwtKey }
        { name: 'patient-service-api-key', value: patientServiceApiKey }
        { name: 'public-intake-api-key', value: publicSchedulingServiceApiKey }
        { name: 'public-slot-key', value: publicAvailabilitySlotKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'scheduling-service'
          image: '${acrLoginServer}/scheduling-service:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__SchedulingDb', secretRef: 'conn-scheduling' }
            { name: 'ServiceBus__ConnectionString', secretRef: 'servicebus-listen' }
            { name: 'Services__PatientService', value: 'http://patient-service' }
            { name: 'InternalApi__PublicIntakeClients__0__TenantId', value: initialTenantId }
            { name: 'InternalApi__PublicIntakeClients__0__ApiKey', secretRef: 'public-intake-api-key' }
            { name: 'PublicAvailability__SlotTokenKey', secretRef: 'public-slot-key' }
            { name: 'Services__PatientServiceClients__0__TenantId', value: initialTenantId }
            { name: 'Services__PatientServiceClients__0__ApiKey', secretRef: 'patient-service-api-key' }
            { name: 'Jwt__Key', secretRef: 'jwt-key' }
            { name: 'Jwt__Issuer', value: jwtIssuer }
            { name: 'Jwt__Audience', value: jwtAudience }
          ]
        }
      ]
      // No HTTP ingress wakes this consumer. KEDA watches the private
      // subscription and starts a replica whenever a booking is queued.
      scale: {
        minReplicas: 0
        maxReplicas: 3
        rules: [
          {
            name: 'booking-request-messages'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'booking-requests'
                subscriptionName: 'scheduling'
                messageCount: '1'
              }
              auth: [
                { secretRef: 'servicebus-listen', triggerParameter: 'connection' }
              ]
            }
          }
          {
            name: 'zocdoc-availability-messages'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'scheduling-availability'
                subscriptionName: 'zocdoc'
                messageCount: '1'
              }
              auth: [
                { secretRef: 'servicebus-listen', triggerParameter: 'connection' }
              ]
            }
          }
          {
            name: 'zocdoc-webhook-messages'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'zocdoc-webhooks'
                subscriptionName: 'scheduling'
                messageCount: '1'
              }
              auth: [
                { secretRef: 'servicebus-listen', triggerParameter: 'connection' }
              ]
            }
          }
          {
            name: 'zocdoc-lifecycle-messages'
            custom: {
              type: 'azure-servicebus'
              metadata: {
                topicName: 'appointment-lifecycle'
                subscriptionName: 'zocdoc'
                messageCount: '1'
              }
              auth: [
                { secretRef: 'servicebus-listen', triggerParameter: 'connection' }
              ]
            }
          }
        ]
      }
    }
  }
}

// ── intake-service ───────────────────────────────────────────────────────────
// The only public API. Its isolated database contains only minimized inbox data;
// it cannot reach patient, clinical, or scheduling databases.
resource intakeService 'Microsoft.App/containerApps@2023-05-01' = {
  name: 'intake-service'
  location: location
  identity: identityObj
  properties: {
    environmentId: environmentId
    configuration: {
      registries: registry
      ingress: { external: true, targetPort: 5109, transport: 'http', allowInsecure: false }
      secrets: [
        { name: 'conn-intake', value: connIntake }
        { name: 'servicebus-send', value: serviceBusSendConnection }
        { name: 'booking-key', value: publicBookingApiKey }
        { name: 'zocdoc-webhook-secret', value: zocdocWebhookSecret }
        { name: 'inbox-admin-key', value: integrationInboxAdminApiKey }
        { name: 'scheduling-service-api-key', value: publicSchedulingServiceApiKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'intake-service'
          image: '${acrLoginServer}/intake-service:${imageTag}'
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__IntakeDb', secretRef: 'conn-intake' }
            { name: 'ServiceBus__ConnectionString', secretRef: 'servicebus-send' }
            { name: 'PublicBooking__Enabled', value: 'true' }
            { name: 'PublicBooking__RequireAvailabilitySelection', value: 'true' }
            { name: 'PublicBooking__Clients__0__TenantId', value: initialTenantId }
            { name: 'PublicBooking__Clients__0__ApiKey', secretRef: 'booking-key' }
            { name: 'PublicBooking__Source', value: '3rdsetsmiles.com' }
            { name: 'Services__SchedulingService', value: 'http://scheduling-service' }
            { name: 'Services__SchedulingServiceClients__0__TenantId', value: initialTenantId }
            { name: 'Services__SchedulingServiceClients__0__ApiKey', secretRef: 'scheduling-service-api-key' }
            { name: 'ZocdocWebhooks__Integrations__0__IntegrationId', value: zocdocWebhookIntegrationId }
            { name: 'ZocdocWebhooks__Integrations__0__TenantId', value: initialTenantId }
            { name: 'ZocdocWebhooks__Integrations__0__WebhookSecret', secretRef: 'zocdoc-webhook-secret' }
            { name: 'ZocdocWebhooks__Integrations__0__Enabled', value: 'true' }
            { name: 'IntegrationInbox__AdminClients__0__TenantId', value: initialTenantId }
            { name: 'IntegrationInbox__AdminClients__0__ApiKey', secretRef: 'inbox-admin-key' }
          ]
          probes: [
            // Liveness has no database dependency, so a transient database outage
            // does not cause the platform to restart an otherwise-healthy process.
            { type: 'Liveness', httpGet: { path: '/health/live', port: 5109 }, initialDelaySeconds: 10, periodSeconds: 10 }
            // Readiness reflects successful schema migration plus current database
            // connectivity, so traffic is only routed once the schema is ready.
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 5109 }, initialDelaySeconds: 5, periodSeconds: 5 }
          ]
        }
      ]
      // Public booking should respond without a cold-start delay.
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DatabaseProvider', value: 'PostgreSQL' }
            { name: 'ConnectionStrings__ClaimsDb', secretRef: 'conn-claims' }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Jwt__Key', secretRef: 'jwt-key' }
            { name: 'Jwt__Issuer', value: jwtIssuer }
            { name: 'Jwt__Audience', value: jwtAudience }
          ]
        }
      ]
      scale: { minReplicas: 0, maxReplicas: 3 }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
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
      scale: { minReplicas: 0, maxReplicas: 3 }
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
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
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
      scale: { minReplicas: 0, maxReplicas: 3 }
    }
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

@description('Public FQDN of the portal — use this as your app URL')
output portalFqdn string = portal.properties.configuration.ingress.fqdn
@description('Public HTTPS host for Cloudflare Pages CLOUDDENTAL_API_BASE')
output intakeFqdn string = intakeService.properties.configuration.ingress.fqdn

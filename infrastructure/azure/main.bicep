targetScope = 'resourceGroup'

// ── Parameters ───────────────────────────────────────────────────────────────

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Base name used to prefix Azure resources (lowercase, no spaces)')
param appName string = 'cdo'

@description('Container App Environment name')
param environmentName string = '${appName}-env'

@description('Azure Container Registry name (must be globally unique, alphanumeric only)')
param acrName string = '${appName}acr${uniqueString(resourceGroup().id)}'

@description('PostgreSQL Flexible Server name (must be globally unique)')
param postgresServerName string = '${appName}-postgres-${uniqueString(resourceGroup().id)}'

@description('PostgreSQL admin username')
param postgresAdminUser string = 'cdoadmin'

@secure()
@description('PostgreSQL admin password (min 8 chars, must include uppercase, lowercase, digit, special char)')
param postgresAdminPassword string

@secure()
@description('JWT signing key (long random string, min 32 chars)')
param jwtKey string

@description('JWT issuer claim value')
param jwtIssuer string = 'CloudDentalOffice'

@description('JWT audience claim value')
param jwtAudience string = 'CloudDentalOfficeUsers'

// ── Log Analytics Workspace (required by Container App Environment) ───────────

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: '${appName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Azure Container Registry ──────────────────────────────────────────────────

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

// ── User-Assigned Managed Identity (allows Container Apps to pull from ACR) ───

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-identity'
  location: location
}

var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPullRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, identity.id, acrPullRoleId)
  scope: acr
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── Container App Environment ─────────────────────────────────────────────────

resource containerAppEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// ── PostgreSQL Flexible Server ────────────────────────────────────────────────

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview' = {
  name: postgresServerName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: postgresAdminUser
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32 }
    version: '16'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

// Allow Azure-hosted services (Container Apps) to connect
resource postgresFirewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview' = {
  parent: postgresServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Per-service databases matching docker-compose and K8s seeds
resource portalDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_portal'
}

resource patientDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_patients'
}

resource schedulingDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_scheduling'
}

resource claimsDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_claims'
}

resource prescriptionDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_prescriptions'
}

resource visionDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: 'cdo_vision'
}

// ── Connection string helper ───────────────────────────────────────────────────

var pgHost = postgresServer.properties.fullyQualifiedDomainName
var pgBase = 'Host=${pgHost};Port=5432;Username=${postgresAdminUser};Password=${postgresAdminPassword};SSL Mode=Require;Trust Server Certificate=true;Database='

// ── Container Apps module ─────────────────────────────────────────────────────

module apps 'container-apps.bicep' = {
  name: 'containerApps'
  params: {
    location: location
    environmentId: containerAppEnv.id
    acrLoginServer: acr.properties.loginServer
    identityId: identity.id
    imageTag: 'latest'
    connPortal: '${pgBase}cdo_portal;'
    connPatient: '${pgBase}cdo_patients;'
    connScheduling: '${pgBase}cdo_scheduling;'
    connClaims: '${pgBase}cdo_claims;'
    connPrescription: '${pgBase}cdo_prescriptions;'
    connVision: '${pgBase}cdo_vision;'
    jwtKey: jwtKey
    jwtIssuer: jwtIssuer
    jwtAudience: jwtAudience
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────────

@description('ACR login server used as image prefix (e.g. cdoacr.azurecr.io)')
output acrLoginServer string = acr.properties.loginServer

@description('ACR resource name — set as ACR_NAME GitHub secret')
output acrName string = acr.name

@description('Managed identity resource ID — referenced in GitHub workflow')
output identityId string = identity.id

@description('Managed identity client ID — set as AZURE_CLIENT_ID GitHub secret if using identity-based auth')
output identityClientId string = identity.properties.clientId

@description('PostgreSQL server FQDN')
output postgresHost string = postgresServer.properties.fullyQualifiedDomainName

@description('Public FQDN of the portal Container App')
output portalFqdn string = apps.outputs.portalFqdn

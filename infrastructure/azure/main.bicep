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

// ── Outputs ───────────────────────────────────────────────────────────────────

@description('ACR login server used as image prefix (e.g. cdoacr.azurecr.io)')
output acrLoginServer string = acr.properties.loginServer

@description('ACR resource name — set as ACR_NAME GitHub secret')
output acrName string = acr.name

@description('Managed identity resource ID — used by apps.bicep')
output identityId string = identity.id

@description('Container App Environment resource ID — used by apps.bicep')
output environmentId string = containerAppEnv.id

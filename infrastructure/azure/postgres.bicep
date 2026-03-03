// Standalone PostgreSQL Flexible Server deployment for Cloud Dental Office.
// Use this to provision the database independently of the full infrastructure.
//
// Deploy:
//   az deployment group create \
//     --resource-group cdo-rg \
//     --template-file infrastructure/azure/postgres.bicep \
//     --parameters adminPassword=<your-password>

targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('PostgreSQL admin username')
param adminUser string = 'cdoadmin'

@secure()
@description('PostgreSQL admin password')
param adminPassword string

@description('PostgreSQL server name (must be globally unique)')
param serverName string = 'cdo-postgres-${uniqueString(resourceGroup().id)}'

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview' = {
  name: serverName
  location: location
  sku: {
    name: 'Standard_B1ms'
    tier: 'Burstable'
  }
  properties: {
    administratorLogin: adminUser
    administratorLoginPassword: adminPassword
    storage: { storageSizeGB: 32 }
    version: '16'
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: { mode: 'Disabled' }
  }
}

// Allow Azure services (Container Apps) to connect
resource firewall 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview' = {
  parent: postgresServer
  name: 'AllowAllAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

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

output serverName string = postgresServer.name
output host string = postgresServer.properties.fullyQualifiedDomainName
output adminUser string = adminUser

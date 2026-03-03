// Standalone Container Apps deployment — run AFTER images have been pushed to ACR.
// Called from the GitHub Actions workflow as the second Bicep deployment step.
//
// Deploy (manually):
//   az deployment group create \
//     --resource-group cdo-rg \
//     --template-file infrastructure/azure/apps.bicep \
//     --parameters \
//       acrLoginServer=<from main.bicep output> \
//       identityId=<from main.bicep output> \
//       environmentId=<from main.bicep output> \
//       postgresHost=<fqdn> \
//       postgresAdminPassword=<password> \
//       jwtKey=<key>

targetScope = 'resourceGroup'

param location string = resourceGroup().location

@description('ACR login server (from main.bicep output acrLoginServer)')
param acrLoginServer string

@description('Managed identity resource ID (from main.bicep output identityId)')
param identityId string

@description('Container App Environment resource ID (from main.bicep output environmentId)')
param environmentId string

@description('Image tag to deploy')
param imageTag string = 'latest'

@description('PostgreSQL server FQDN')
param postgresHost string

@description('PostgreSQL admin username')
param postgresAdminUser string = 'cdoadmin'

@secure()
param postgresAdminPassword string

@secure()
param jwtKey string

param jwtIssuer string = 'CloudDentalOffice'
param jwtAudience string = 'CloudDentalOfficeUsers'

var pgBase = 'Host=${postgresHost};Port=5432;Username=${postgresAdminUser};Password=${postgresAdminPassword};SSL Mode=Require;Trust Server Certificate=true;Database='

module apps 'container-apps.bicep' = {
  name: 'containerApps'
  params: {
    location: location
    environmentId: environmentId
    acrLoginServer: acrLoginServer
    identityId: identityId
    imageTag: imageTag
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

output portalFqdn string = apps.outputs.portalFqdn

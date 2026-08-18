// Standalone Container Apps deployment — run AFTER images have been pushed to ACR.
// Called from the GitHub Actions workflow as the second Bicep deployment step.
//
// Deploy (manually):
//   az deployment group create \
//     --resource-group cdo-prod-rg \
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
@secure()
param serviceBusSendConnection string
@secure()
param serviceBusListenConnection string
@secure()
param publicBookingApiKey string
@secure()
@description('Tenant-scoped key authorizing SchedulingService to call PatientService internal APIs')
param patientServiceApiKey string = ''
@secure()
@description('Tenant-scoped key authorizing IntakeService to call SchedulingService availability APIs')
param publicSchedulingServiceApiKey string
@secure()
@description('HMAC key used by SchedulingService for opaque public availability selections')
param publicAvailabilitySlotKey string
@description('Opaque route identifier configured in the Zocdoc webhook URL')
param zocdocWebhookIntegrationId string = ''
@secure()
@description('Base64 webhook signing key issued by Zocdoc')
param zocdocWebhookSecret string = ''
@description('Initial tenant identifier to configure in the Portal app')
param initialTenantId string = 'third-set-smiles'
@description('Google OAuth client ID for Portal sign-in; leave empty to disable Google sign-in')
param googleOAuthClientId string = ''
@secure()
@description('Google OAuth client secret for Portal sign-in; leave empty to disable Google sign-in')
param googleOAuthClientSecret string = ''
@description('Base URL for the CloudHealthOffice payment estimate API')
param cloudHealthOfficeBaseUrl string = 'https://benefit-plan-estimate.lemoncoast-a1e8528c.westus3.azurecontainerapps.io'
@secure()
@description('API key for the CloudHealthOffice payment estimate API')
param cloudHealthOfficeApiKey string
@description('CloudHealthOffice benefit plan ID mapped to the configured payer')
param cloudHealthOfficeBenefitPlanId string = '3e8c59e8-47dd-4aa9-b318-9828fbdcb072'
@description('Payer ID that should route payment estimates through CloudHealthOffice')
param cloudHealthOfficePayerId string = '00001'

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
    serviceBusSendConnection: serviceBusSendConnection
    serviceBusListenConnection: serviceBusListenConnection
    publicBookingApiKey: publicBookingApiKey
    patientServiceApiKey: patientServiceApiKey
    publicSchedulingServiceApiKey: publicSchedulingServiceApiKey
    publicAvailabilitySlotKey: publicAvailabilitySlotKey
    zocdocWebhookIntegrationId: zocdocWebhookIntegrationId
    zocdocWebhookSecret: zocdocWebhookSecret
    initialTenantId: initialTenantId
    googleOAuthClientId: googleOAuthClientId
    googleOAuthClientSecret: googleOAuthClientSecret
    cloudHealthOfficeBaseUrl: cloudHealthOfficeBaseUrl
    cloudHealthOfficeApiKey: cloudHealthOfficeApiKey
    cloudHealthOfficeBenefitPlanId: cloudHealthOfficeBenefitPlanId
    cloudHealthOfficePayerId: cloudHealthOfficePayerId
  }
}

output portalFqdn string = apps.outputs.portalFqdn
output intakeFqdn string = apps.outputs.intakeFqdn

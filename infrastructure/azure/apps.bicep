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
@description('Tenant-scoped key authorizing SchedulingService to call the Portal internal patient match-or-create API')
param patientServiceApiKey string
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
@description('Opaque SchedulingService credential reference; must match the tenant Zocdoc integration setting in the Portal')
param zocdocCredentialReference string = 'third-set-smiles-zocdoc'
@description('Zocdoc Calendar Integration OAuth client ID; leave empty to leave outbound Zocdoc calls unconfigured')
param zocdocClientId string = ''
@secure()
@description('Zocdoc Calendar Integration OAuth client secret')
param zocdocClientSecret string = ''
@secure()
@description('Tenant-scoped credential for IntakeService inbox status and retry operations')
param integrationInboxAdminApiKey string = ''
@description('Initial tenant identifier to configure in the Portal app')
param initialTenantId string = 'third-set-smiles'
@description('Google OAuth client ID for Portal sign-in; leave empty to disable Google sign-in')
param googleOAuthClientId string = ''
@secure()
@description('Google OAuth client secret for Portal sign-in; leave empty to disable Google sign-in')
param googleOAuthClientSecret string = ''
param searchConsoleServiceAccountEmail string = ''
@secure()
param searchConsolePrivateKey string = ''
@description('Base URL for the CloudHealthOffice payment estimate API')
param cloudHealthOfficeBaseUrl string = 'https://benefit-plan-estimate.lemoncoast-a1e8528c.westus3.azurecontainerapps.io'
@secure()
@description('API key for the CloudHealthOffice payment estimate API')
param cloudHealthOfficeApiKey string
@description('CloudHealthOffice benefit plan ID mapped to the configured payer')
param cloudHealthOfficeBenefitPlanId string = '3e8c59e8-47dd-4aa9-b318-9828fbdcb072'
@description('Payer ID that should route payment estimates through CloudHealthOffice')
param cloudHealthOfficePayerId string = '00001'
@description('Internal URL of the CHO provider eligibility app; empty leaves eligibility off')
param cloudHealthOfficeEligibilityBaseUrl string = ''
@secure()
@description('CDO client credential for the CHO provider eligibility API')
param cloudHealthOfficeEligibilityApiKey string = ''
@description('CDO insurance-plan payer IDs whose eligibility checks route through CloudHealthOffice')
param cloudHealthOfficeEligibilityPayerIds array = []

@description('Key Vault URI for per-practice Stedi keys (main.bicep output keyVaultUri)')
param keyVaultUri string = ''

@description('Portal-only identity resource ID (main.bicep output portalIdentityId)')
param portalIdentityId string = ''

@description('Portal identity client ID (main.bicep output portalIdentityClientId)')
param portalIdentityClientId string = ''

@description('Pilot only: allow Shared-mode practices to use Aurelianware\'s Stedi account')
param stediSharedAccountEnabled bool = false

@description('Key Vault secret name of the shared Stedi key (must start with stedi-shared-)')
param stediSharedAccountSecretName string = ''

@description('Payer IDs whose eligibility routes by each practice\'s clearinghouse connection')
param clearinghouseEligibilityPayerIds array = []

@description('Route every payer without its own eligibility route to Clearinghouse')
param clearinghouseEligibilityDefault bool = false

@description('Turn on the background coverage verification worker in the Portal. It checks only practices with an active clearinghouse connection, and also sends due coverage intake links.')
param coverageVerificationEnabled bool = false

@description('Turn on patient coverage intake links (Portal and IntakeService). Needs the key, link URL and listen connection.')
param coverageIntakeEnabled bool = false

@secure()
@description('Shared signing key for coverage intake links, at least 32 bytes.')
param coverageIntakeSigningKey string = ''

@description('Public HTTPS address of IntakeService used in coverage intake links.')
param coverageIntakeLinkBaseUrl string = ''

@secure()
@description('Listen-only connection for the coverage-intake topic (rule portal-listen).')
param coverageIntakeListenConnection string = ''

@description('SMTP host for patient email (e.g. smtp.azurecomm.net). Empty leaves email off.')
param emailSmtpHost string = ''

param emailSmtpPort int = 587

@description('SMTP user name (for Azure Communication Services, its SMTP username).')
param emailSmtpUsername string = ''

@secure()
@description('SMTP password (for Azure Communication Services, the Entra app client secret).')
param emailSmtpPassword string = ''

@description('Platform sending address, e.g. no-reply@clouddental.io. Shown under the practice name.')
param emailFromAddress string = ''

@description('The initial practice\'s own address for patient replies, e.g. info@3rdsetsmiles.com.')
param practiceReplyTo string = ''

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
    connScheduling: '${pgBase}cdo_scheduling;'
    connIntake: '${pgBase}cdo_intake;'
    connAuth: '${pgBase}cdo_auth;'
    connClaims: '${pgBase}cdo_claims;'
    connPrescription: '${pgBase}cdo_prescriptions;'
    connVision: '${pgBase}cdo_vision;'
    jwtKey: jwtKey
    jwtIssuer: jwtIssuer
    jwtAudience: jwtAudience
    serviceBusSendConnection: serviceBusSendConnection
    serviceBusListenConnection: serviceBusListenConnection
    coverageVerificationEnabled: coverageVerificationEnabled
    coverageIntakeEnabled: coverageIntakeEnabled
    emailSmtpHost: emailSmtpHost
    emailSmtpPort: emailSmtpPort
    emailSmtpUsername: emailSmtpUsername
    emailSmtpPassword: emailSmtpPassword
    emailFromAddress: emailFromAddress
    practiceReplyTo: practiceReplyTo
    coverageIntakeSigningKey: coverageIntakeSigningKey
    coverageIntakeLinkBaseUrl: coverageIntakeLinkBaseUrl
    coverageIntakeListenConnection: coverageIntakeListenConnection
    publicBookingApiKey: publicBookingApiKey
    patientServiceApiKey: patientServiceApiKey
    publicSchedulingServiceApiKey: publicSchedulingServiceApiKey
    publicAvailabilitySlotKey: publicAvailabilitySlotKey
    zocdocWebhookIntegrationId: zocdocWebhookIntegrationId
    zocdocWebhookSecret: zocdocWebhookSecret
    zocdocCredentialReference: zocdocCredentialReference
    zocdocClientId: zocdocClientId
    zocdocClientSecret: zocdocClientSecret
    integrationInboxAdminApiKey: integrationInboxAdminApiKey
    initialTenantId: initialTenantId
    googleOAuthClientId: googleOAuthClientId
    googleOAuthClientSecret: googleOAuthClientSecret
    searchConsoleServiceAccountEmail: searchConsoleServiceAccountEmail
    searchConsolePrivateKey: searchConsolePrivateKey
    cloudHealthOfficeBaseUrl: cloudHealthOfficeBaseUrl
    cloudHealthOfficeApiKey: cloudHealthOfficeApiKey
    cloudHealthOfficeBenefitPlanId: cloudHealthOfficeBenefitPlanId
    cloudHealthOfficePayerId: cloudHealthOfficePayerId
    cloudHealthOfficeEligibilityBaseUrl: cloudHealthOfficeEligibilityBaseUrl
    cloudHealthOfficeEligibilityApiKey: cloudHealthOfficeEligibilityApiKey
    cloudHealthOfficeEligibilityPayerIds: cloudHealthOfficeEligibilityPayerIds
    keyVaultUri: keyVaultUri
    portalIdentityId: portalIdentityId
    portalIdentityClientId: portalIdentityClientId
    stediSharedAccountEnabled: stediSharedAccountEnabled
    stediSharedAccountSecretName: stediSharedAccountSecretName
    clearinghouseEligibilityPayerIds: clearinghouseEligibilityPayerIds
    clearinghouseEligibilityDefault: clearinghouseEligibilityDefault
  }
}

output portalFqdn string = apps.outputs.portalFqdn
output intakeFqdn string = apps.outputs.intakeFqdn

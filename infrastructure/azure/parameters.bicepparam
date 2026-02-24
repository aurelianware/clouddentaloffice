// Azure Bicep parameters file for Cloud Dental Office infrastructure.
// Copy this file, fill in the secure values, and pass it to `az deployment group create`.
//
// NEVER commit a filled-in copy of this file to source control.
// Store secrets in a secrets manager (Azure Key Vault, GitHub Secrets, 1Password, etc.)
//
// Deploy command:
//   az deployment group create \
//     --resource-group <your-rg> \
//     --template-file main.bicep \
//     --parameters @parameters.bicepparam

using 'main.bicep'

// ── Region & naming ───────────────────────────────────────────────────────────

// Azure region — run `az account list-locations -o table` for options
param location = 'eastus'

// Short prefix for all resource names (lowercase, letters/digits only)
param appName = 'cdo'

// Override if you need a specific name (must be globally unique, alphanumeric)
// param acrName = 'cdoacr'
// param postgresServerName = 'cdo-postgres'

// ── PostgreSQL ────────────────────────────────────────────────────────────────

param postgresAdminUser = 'cdoadmin'

// Replace with a strong password (min 8 chars, uppercase + lowercase + digit + special)
param postgresAdminPassword = 'REPLACE_WITH_STRONG_PASSWORD'

// ── JWT ───────────────────────────────────────────────────────────────────────

// Generate a long random key, e.g.: openssl rand -base64 64
param jwtKey = 'REPLACE_WITH_LONG_RANDOM_KEY'

param jwtIssuer = 'CloudDentalOffice'
param jwtAudience = 'CloudDentalOfficeUsers'

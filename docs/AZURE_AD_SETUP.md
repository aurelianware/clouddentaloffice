# Azure AD Multi-Tenant Authentication Setup Guide

This guide explains how to configure Cloud Dental Office to use Azure AD (Microsoft Entra ID) for multi-tenant authentication.

## Overview

The system now supports two authentication modes:
1. **Azure AD Multi-Tenant** (Recommended for production): Organizations sign in with their Microsoft work accounts
2. **Local Authentication** (Development fallback): Simple email/password authentication

## Azure AD Architecture

### How It Works

1. **Organization Signup**: When a dental practice signs up, they authenticate with their Microsoft work account
2. **Tenant Mapping**: Their Azure AD tenant ID is stored in the `Organizations` table
3. **User Provisioning**: Users are automatically created in the database when they first sign in via Azure AD
4. **Organization Isolation**: Users from different Azure AD tenants are isolated into separate organizations

### Database Schema

**Organizations Table**:
- `TenantId`: Internal unique identifier (GUID)
- `AzureAdTenantId`: Microsoft Entra Directory ID
- `Name`: Organization/practice name
- `Domain`: Email domain (e.g., "dentalclinic.com")
- `Plan`: Subscription tier (trial, hobby, pro, enterprise)
- `StripeCustomerId`, `StripeSubscriptionId`: Billing integration

**Users Table** (Updated):
- `OrganizationId`: FK to Organizations table
- `AzureAdObjectId`: Unique Azure AD user identifier
- `AzureAdUpn`: User Principal Name (email)
- `PasswordHash`: Nullable (not needed for Azure AD users)
- `CanInviteUsers`: Permission to invite other users
- `IsActive`: Account status

## Step 1: Create Azure AD App Registration

### 1.1 Register the Application

1. Go to [Azure Portal](https://portal.azure.com)
2. Navigate to **Azure Active Directory** → **App registrations**
3. Click **New registration**
4. Configure:
   - **Name**: `Cloud Dental Office`
   - **Supported account types**: `Accounts in any organizational directory (Any Azure AD directory - Multitenant)`
   - **Redirect URI**: 
     - Platform: `Web`
     - URI: `https://yourdomain.com/signin-oidc` (for production)
     - Add `https://localhost:7001/signin-oidc` for development

### 1.2 Configure API Permissions

1. Go to **API permissions**
2. Click **Add a permission** → **Microsoft Graph**
3. Select **Delegated permissions**
4. Add these permissions:
   - `User.Read` (Read user profile)
   - `email` (View user's email)
   - `openid` (Sign in user)
   - `profile` (View user's basic profile)
5. Click **Grant admin consent** (if you're a tenant admin)

### 1.3 Create Client Secret

1. Go to **Certificates & secrets**
2. Click **New client secret**
3. Description: `CloudDentalOffice-Portal`
4. Expires: Choose appropriate duration (12 months recommended)
5. Click **Add**
6. **COPY THE SECRET VALUE** - you won't be able to see it again!

### 1.4 Get Application IDs

From the **Overview** page, note:
- **Application (client) ID**: e.g., `12345678-1234-1234-1234-123456789abc`
- **Directory (tenant) ID**: Set to `common` for multi-tenant (not your specific tenant ID)

## Step 2: Configure Application Settings

### 2.1 Update appsettings.json

```json
{
  "AzureAd": {
    "Enabled": true,
    "Instance": "https://login.microsoftonline.com/",
    "TenantId": "common",
    "ClientId": "YOUR_APPLICATION_CLIENT_ID",
    "ClientSecret": "YOUR_CLIENT_SECRET_VALUE",
    "Domain": "yourdomain.com",
    "CallbackPath": "/signin-oidc",
    "SignedOutCallbackPath": "/signout-callback-oidc"
  }
}
```

**Important**:
- Set `Enabled` to `true` to activate Azure AD authentication
- Use `"common"` for TenantId to support multi-tenant
- Never commit `ClientSecret` to source control

### 2.2 Production Configuration

For production, store secrets in Azure Key Vault:

```json
{
  "KeyVault": {
    "VaultUri": "https://your-keyvault.vault.azure.net/"
  }
}
```

Add secrets to Key Vault:
- `AzureAd--ClientSecret`
- `ConnectionStrings--DefaultConnection`

The application automatically loads secrets from Key Vault in production (when not in Development environment).

## Step 3: Database Migration

Run the Entity Framework migration to add Organizations and Azure AD support:

```bash
cd src/CloudDentalOffice.Portal
dotnet ef database update
```

This will:
- Create the `Organizations` table
- Add Azure AD columns to `Users` table
- Create necessary indexes and foreign keys

## Step 4: Test Authentication Flow

### 4.1 Local Testing

1. Enable Azure AD in `appsettings.Development.json`:
   ```json
   {
     "AzureAd": {
       "Enabled": true,
       "ClientId": "YOUR_DEV_CLIENT_ID",
       "ClientSecret": "YOUR_DEV_CLIENT_SECRET"
     }
   }
   ```

2. Add `https://localhost:7001/signin-oidc` as a redirect URI in Azure

3. Run the application:
   ```bash
   dotnet run
   ```

4. Navigate to `/signup` - you should see "Sign up with Microsoft Azure AD" button

### 4.2 Authentication Flow

1. **User clicks "Sign up with Microsoft Azure AD"**
2. **Redirected to Microsoft login** (`login.microsoftonline.com`)
3. **User signs in** with their work account (e.g., `user@dentalclinic.com`)
4. **Microsoft redirects back** with authentication token
5. **Application processes token**:
   - Extracts Azure AD claims (oid, tid, upn, email)
   - Looks up or creates Organization (by Azure AD tenant ID)
   - Looks up or creates User (by Azure AD object ID)
   - First user in organization becomes Admin
   - Sets LastLoginAt timestamp
6. **User is logged in** and redirected to dashboard

## Step 5: Organization Management

### 5.1 Admin Features

Admins can access organization management from the navigation menu:
- **Administration** → **Organization Settings**: Configure org details, plan, billing
- **Administration** → **User Management**: Invite users, manage permissions

### 5.2 Inviting Users

1. Navigate to **Administration** → **User Management**
2. Click **Invite User**
3. Enter email and select role (Admin, Staff, Dentist, Hygienist)
4. User receives invitation (email implementation pending)
5. User signs in with their Microsoft work account
6. If they're from the same Azure AD tenant, they're automatically added to the organization

### 5.3 User Permissions

- **Admin**: Full access, can invite users, manage organization settings
- **Staff**: Basic access to practice management
- **Dentist**: Clinical features access
- **Hygienist**: Limited clinical access

## Step 6: Billing Integration

### 6.1 Stripe Integration

Organizations automatically get a Stripe customer ID when:
- They upgrade from trial to paid plan
- They add payment information

The `Organizations.StripeCustomerId` and `StripeSubscriptionId` fields integrate with the existing Stripe billing system.

### 6.2 Trial Management

- New organizations start on 14-day free trial
- `TrialExpiresAt` field tracks expiration
- After trial, users must upgrade to continue access

## Troubleshooting

### Users Can't Sign In

**Check**:
1. Azure AD app redirect URIs match your domain
2. `AzureAd:Enabled` is `true` in configuration
3. Client ID and secret are correct
4. Database migration was applied

**Logs to check**:
```bash
# Application logs show authentication events
[Information] User user@domain.com authenticated via Azure AD (Org: Dental Clinic)
```

### Users from Same Company Can't See Each Other

**Likely cause**: They signed up separately, creating duplicate organizations

**Resolution**:
1. Check `Organizations` table for duplicates (same `AzureAdTenantId`)
2. Merge organizations if needed
3. Future: Implement organization discovery/joining flow

### "Missing required Azure AD claims" Error

**Cause**: Azure AD token doesn't include `oid` or `tid` claims

**Resolution**:
1. Verify API permissions include `openid` and `profile`
2. Check if admin consent was granted
3. Try signing out and back in

## Security Best Practices

1. **Never commit secrets**: Use Key Vault for production
2. **Rotate client secrets**: Set expiration and rotate regularly
3. **Use managed identities**: When deploying to Azure, use managed identity for Key Vault access
4. **Monitor sign-ins**: Check Azure AD sign-in logs for suspicious activity
5. **Implement MFA**: Require multi-factor authentication in your Azure AD tenant

## Rollback to Local Authentication

To disable Azure AD temporarily:

```json
{
  "AzureAd": {
    "Enabled": false
  }
}
```

The system will fall back to JWT-based local authentication. Existing users with Azure AD accounts won't be able to sign in until Azure AD is re-enabled.

## Next Steps

- [ ] Implement email invitation system
- [ ] Add organization discovery (join existing org)
- [ ] Build billing upgrade flow
- [ ] Add user activity logging
- [ ] Implement organization-level settings (feature flags, branding)
- [ ] Add SSO-forced authentication (require Azure AD, disable local auth)

## Support

For Azure AD setup assistance:
- [Microsoft Identity Platform Documentation](https://docs.microsoft.com/azure/active-directory/develop/)
- [Microsoft.Identity.Web Documentation](https://docs.microsoft.com/azure/active-directory/develop/microsoft-identity-web)

# Authentication and tenant identity

```text
Credentials
   ↓
Identity validation
   ↓
Global user
   ↓
Enabled tenant memberships
   ↓
Authorized tenant selection
   ↓
Membership roles
   ↓
Short-lived JWT
   ↓
tenant_id claim / current tenant
```

AuthService owns `AuthUsers` and `TenantMemberships` in the isolated `cdo_auth`
database. A user is global and may use a local adaptive password hash or, in a
future adapter, an external issuer/subject pair. Memberships independently enable
access to a tenant and supply that tenant's roles. Requests cannot supply roles.

## Login and multiple practices

`POST /api/auth/login` accepts only username and password. A user with one enabled
membership receives a 30-minute tenant-scoped access token. A user with multiple
memberships receives no access token; the response contains the permitted practice
choices and a five-minute, purpose- and audience-restricted selection token.
`POST /api/auth/select-tenant` validates that token and the selected enabled
membership before issuing an access token. An arbitrary tenant ID cannot create a
membership or claim.

JWTs contain `sub`, `email`, `jti`, `iat`, `exp`, `tenant_id`, and membership-derived
role claims. They contain no passwords, hashes, secrets, or PHI. Refresh tokens are
not implemented; rotation/revocation should be added as a separate design rather
than using the former placeholder endpoint.

## Passwords and external identities

Local passwords use ASP.NET Core Identity's salted adaptive `PasswordHasher` and
only the resulting hash is persisted. Hashes that need stronger current parameters
are upgraded and persisted after a successful login. `ExternalIssuer` and
`ExternalSubject` allow future Entra ID, Google Workspace, Okta, or other OIDC
identities without changing the membership model. The existing Portal OIDC flows
remain separate and unchanged.

## Local development

Development configuration seeds `demo-admin@example.test` for tenant `demo` using
the same password hasher as normal identities. Its password is documented in
`appsettings.Development.json` and must never be used outside local development.
Set `DemoAuth:Enabled=false` to disable it.

## Production configuration

Production requires secret-backed `Jwt__Key`, `Jwt__Issuer`, `Jwt__Audience`, and
`ConnectionStrings__AuthDb`. AuthService refuses to start with a missing/short JWT
key, a recognized development key, a non-PostgreSQL or missing production database
connection, or demo authentication enabled. Azure uses the
existing secure deployment parameters and the isolated `cdo_auth` PostgreSQL
database; Kubernetes reads the same values from `cdo-app-secrets`.

Login and tenant selection are rate limited per source address. Failure responses
are deliberately generic to avoid account enumeration. Security logs contain only
server-side user IDs after success or correlation IDs after failure—never passwords,
tokens, hashes, or patient data.

When AuthService runs behind a reverse proxy, configure each trusted proxy address
through `TrustedProxies` (for example, `TrustedProxies__0`). Forwarded client
addresses are accepted only from that allowlist; an empty list fails safely by
ignoring forwarded headers from unknown proxies.

## Migration

The initial AuthService EF Core migration creates global users, tenant memberships,
unique normalized-email identity, unique external issuer/subject identity, and one
membership per user/tenant. It does not alter Portal users or tenant data. Existing
deployments must provision `cdo_auth`; no placeholder AuthService credentials are
migrated because they were never authoritative stored identities. Migration
scaffolding uses an explicit PostgreSQL design-time context so generated migrations
and model snapshots stay aligned with the production provider.

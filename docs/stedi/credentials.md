# Per-practice Stedi credentials

**Goal.** Every outbound Stedi call uses the calling practice's own Stedi API key, resolved at request time. The only shared key is an explicit, flagged fallback. Baseline: [current-state.md](current-state.md).

## How it works

```
Staff action ─► PayerTransactionRouter ─► "Clearinghouse" adapter
                                               │  reads TenantClearinghouseConnection (explicit tenant)
                         ┌─────────────────────┴───────────────────────┐
                EligibilityGateway = Stedi               EligibilityGateway = CloudHealthOffice
                         │                                             │
              StediEligibilityClient                     CloudHealthOfficeEligibilityClient
              (request stamped with tenant)                (unchanged CHO path; logged)
                         │
              StediCredentialHandler ─► IStediCredentialProvider ─► Key Vault
              sets Authorization              (5-min cache)         stedi-apikey-{tenantId}
```

1. **`TenantClearinghouseConnection`** (`Models/TenantClearinghouseConnection.cs`). One row per practice and provider (unique on `TenantId, Provider`). It holds:
   - `Mode`: `Integrated` (the practice's own account) or `Shared` (Aurelianware's account, pilot only).
   - `Status`: `Pending`, `Active` or `Suspended`.
   - `StediAccountId`.
   - `KeyReference`: the Key Vault secret **name**, never the key.
   - `EligibilityGateway`: `Stedi` or `CloudHealthOffice`.
   - `CreatedAt`, `UpdatedAt`, `RotatedAt`.

   `TenantId` has no `demo` default.
2. **Keys live only in Key Vault**, one secret per practice, named `stedi-apikey-{tenantId}`. For Integrated mode the provider *derives* that name from the tenant and requires `KeyReference` to match it exactly. A row can therefore never point one practice at another practice's key.
3. **`IStediCredentialProvider.GetAsync(tenantId)`** (`Services/Stedi/StediCredentials.cs`):
   - It reads the row for the explicit tenant (ignoring the ambient tenant) and requires `Active`.
   - It reads the secret with the Container App's managed identity (`DefaultAzureCredential`, `AZURE_CLIENT_ID`).
   - It caches the key for 5 minutes (`Stedi:CredentialCacheMinutes`).
   - The cache key includes the secret name and `RotatedAt`, so a rotation recorded by any replica is picked up on that replica's next lookup.
   - `Invalidate(tenantId)` evicts this instance's copy immediately.
4. **`StediCredentialHandler`** (a `DelegatingHandler` on the Stedi `HttpClient`):
   - It takes the tenant only from the request's `StediRequest.Tenant` option, never from ambient state.
   - It removes any existing `Authorization` header and attaches that practice's key.
   - A request with no tenant throws before anything is sent.
5. **Failures never fall back.** A missing tenant, missing or inactive connection, bad key reference, disabled shared account, missing vault configuration, missing secret, or Key Vault error each raise `StediCredentialUnavailableException`. The eligibility client turns that into a staff-readable message. No other key is tried.

## Shared mode (pilot only)

Aurelianware's Stedi key is used only when **both** of these hold:
- `Stedi:SharedAccount:Enabled` is `true`. This is the GitHub variable `STEDI_SHARED_ACCOUNT_ENABLED`.
- The practice's connection has `Mode = Shared`.

The key is read from the secret named in `Stedi:SharedAccount:SecretName` (e.g. `stedi-apikey-shared`, via GitHub variable `STEDI_SHARED_ACCOUNT_SECRET_NAME`). Every use logs a warning, `Tenant {TenantId} is using the shared Aurelianware Stedi account`, with no PHI. An Integrated practice is never given the shared key, even when the flag is on.

## Eligibility routing per practice

`IEligibilityGateway` has two implementations, chosen by the connection's `EligibilityGateway`:

| Value | Path | Credential |
|---|---|---|
| `Stedi` | `StediEligibilityClient` → Stedi eligibility v3 | The practice's key (or the flagged shared key) |
| `CloudHealthOffice` | Existing `CloudHealthOfficeEligibilityClient` → CHO provider eligibility API → CHO's Stedi account | CDO's CHO client key. CHO uses its own Stedi key, so **each use logs a warning** |

To use per-practice routing, set a payer's route to the new adapter:
`PayerConnectivity__Payers__{payerId}__Eligibility=Clearinghouse`.

Payers still routed to `CloudHealthOffice` behave exactly as before, so nothing changes until routes are switched. A practice with no active connection gets "Eligibility checks aren't set up for this practice yet". The adapter never picks a path on its behalf.

On the direct path, the insurance plan's payer ID is sent to Stedi as `tradingPartnerServiceId`, so it must be a Stedi payer ID. CHO resolved payer IDs through its payer directory; the direct path does not.

Results from the direct path show staff "via Clearinghouse". Vendor names are not shown in the portal.

## Background work

No background job or message consumer calls Stedi today. The API is shaped so one can't use the wrong key:
- Callers pass the tenant explicitly: the credential provider takes `tenantId`, and the handler reads the tenant stamped on the request.
- Resolution ignores the ambient tenant, which silently defaults to `demo` outside a signed-in circuit.
- A missing tenant fails closed.

A future consumer must carry `TenantId` in its message and stamp it on the request.

## Infrastructure

- **`main.bicep`:**
  - Adds Key Vault `cdo-kv-{unique}`: Azure RBAC authorization, soft delete with 90 days' retention.
  - Grants the apps' identity (`cdo-identity`) **Key Vault Secrets User** on that vault only. That role allows secret get and list (`getSecret`, `readMetadata`): no writes, deletes, keys or certificates.
  - New outputs: `identityClientId`, `keyVaultUri`, `keyVaultName`.
- **`apps.bicep` / `container-apps.bicep`:** set the Portal's `Stedi__KeyVaultUri`, `AZURE_CLIENT_ID`, `Stedi__SharedAccount__Enabled` and `Stedi__SharedAccount__SecretName`. No key is passed to any app.
- **`.github/workflows/deploy-aca.yml`:** reads the new outputs and passes them to `apps.bicep`, along with the optional variables `STEDI_SHARED_ACCOUNT_ENABLED` and `STEDI_SHARED_ACCOUNT_SECRET_NAME`.
- **`apps.json`:** regenerated from `apps.bicep`.

Purge protection is **not** enabled. It can't be turned off once on; decide before storing production keys.

## Database

- **Development:** migration `AddTenantClearinghouseConnections` (SQLite and development environments).
- **Production:** production PostgreSQL uses `EnsureCreated`, which never adds tables to an existing database. `ClearinghouseConnectionSchemaReconciliation` creates the table and unique index idempotently at startup, the same pattern as the review-outreach and claim-lifecycle tables.

## Onboarding a practice (until there is an admin screen)

1. Grant yourself **Key Vault Secrets Officer** on the vault. The template doesn't grant write access to anyone.
2. Store the key (paste it at a prompt; don't put it in shell history):
   ```sh
   read -s "KEY?Stedi key: " && az keyvault secret set --vault-name <keyVaultName> --name stedi-apikey-<tenantId> --value "$KEY" --output none; unset KEY
   ```
3. Insert the connection row:
   ```sql
   INSERT INTO "TenantClearinghouseConnections"
     ("TenantId","Provider","Mode","Status","KeyReference","EligibilityGateway","CreatedAt","UpdatedAt")
   VALUES ('<tenantId>','Stedi','Integrated','Active','stedi-apikey-<tenantId>','Stedi',now(),now());
   ```
4. Route the practice's payers to `Clearinghouse`.
5. **Rotation:** set a new secret version, then record the rotation with `IClearinghouseConnectionStore.MarkRotatedAsync`, or `UPDATE … SET "RotatedAt" = now()`. Replicas pick it up on their next lookup; the old key stays cached for at most 5 minutes on replicas that don't see the update.

## Tests

`src/CloudDentalOffice.Portal.Tests/StediCredentialTests.cs` (29 tests) covers:
- Per-practice resolution, and practice A never resolving practice B's key: both a row pointing at B's secret and interleaved concurrent lookups.
- Missing-tenant failures in the provider and the handler.
- Inactive connections.
- The shared-mode flag and the warning logged on each use.
- The cache, invalidation, and rotation recorded by another replica.
- Header replacement.
- Direct request and response mapping.
- Routing per practice.
- Keys and patient identity kept out of logs.

A mutation check confirmed the isolation test fails if the key-name check is removed.

## Not done yet

- **Admin tooling:** no admin screen or API to create or rotate connections. SQL and CLI only for now.
- **Other transactions:** claims (837D), claim status, 835 and enrollment don't call Stedi from CDO yet. When added, they must use the same handler and provider.
- **CHO path:** still uses CHO's single Stedi key. Moving it to per-practice keys is a CHO change.

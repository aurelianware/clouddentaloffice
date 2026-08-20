using Microsoft.EntityFrameworkCore;

public static class SearchConsoleBootstrap
{
    public static async Task ApplyAsync(SchedulingDbContext db, IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var section = configuration.GetSection("SearchConsoleBootstrap");
        if (!section.GetValue("Enabled", false)) return;

        var tenantId = section["TenantId"]?.Trim();
        var propertyUrl = section["PropertyUrl"]?.Trim();
        var credentialReference = section["CredentialReference"]?.Trim();
        var canonicalHost = section["CanonicalHost"]?.Trim();
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64 ||
            !SearchConsoleProperty.IsValid(propertyUrl) || string.IsNullOrWhiteSpace(credentialReference))
            throw new InvalidOperationException("SearchConsoleBootstrap configuration is invalid.");

        var row = await db.SearchConsoleIntegrations.SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (row is null)
        {
            db.SearchConsoleIntegrations.Add(new SearchConsoleIntegration
            {
                TenantId = tenantId,
                Enabled = true,
                PropertyUrl = propertyUrl!,
                CredentialReference = credentialReference,
                CanonicalHost = canonicalHost,
                SyncStatus = SearchConsoleSyncStatus.Pending,
                NextSyncAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var changed = !row.Enabled || row.PropertyUrl != propertyUrl ||
            row.CredentialReference != credentialReference || row.CanonicalHost != canonicalHost;
        if (!changed) return;

        row.Enabled = true;
        row.PropertyUrl = propertyUrl!;
        row.CredentialReference = credentialReference;
        row.CanonicalHost = canonicalHost;
        row.SyncStatus = SearchConsoleSyncStatus.Pending;
        row.NextSyncAt = DateTime.UtcNow;
        row.LastError = null;
        row.LockId = null;
        row.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);
    }
}

using System.Security.Claims;

namespace CloudDentalOffice.Portal.Services.Tenancy;

/// <summary>
/// Lets background work run tenant-scoped services for one practice. A worker
/// creates a DI scope, pins the tenant, and only then resolves services: the
/// scope's <see cref="ITenantProvider"/> (and so the DbContext query filters and
/// the payer router's tenant check) then answers with that tenant instead of the
/// signed-in user's. Scopes that never pin keep the normal provider.
/// </summary>
public sealed class PinnedTenantScope
{
    public string? TenantId { get; private set; }

    public void Pin(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("A pinned tenant must be explicit.", nameof(tenantId));
        if (TenantId is not null && TenantId != tenantId)
            throw new InvalidOperationException("This scope is already pinned to another tenant.");
        TenantId = tenantId;
    }

    /// <summary>
    /// Registers <see cref="ITenantProvider"/> so a pinned scope gets the pinned tenant.
    /// The provider is resolved once per scope, so pin before resolving anything that uses it.
    /// </summary>
    public static void Register<TDefault>(IServiceCollection services) where TDefault : class, ITenantProvider
    {
        services.AddScoped<PinnedTenantScope>();
        services.AddScoped<TDefault>();
        services.AddScoped<ITenantProvider>(sp => sp.GetRequiredService<PinnedTenantScope>().TenantId is { } pinned
            ? new PinnedTenantProvider(pinned)
            : sp.GetRequiredService<TDefault>());
    }

    private sealed class PinnedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
}

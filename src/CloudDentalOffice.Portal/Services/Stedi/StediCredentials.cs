using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace CloudDentalOffice.Portal.Services.Stedi;

public sealed class StediOptions
{
    public const string SectionName = "Stedi";

    public string BaseUrl { get; set; } = "https://healthcare.us.stedi.com";
    public string EligibilityPath { get; set; } = "/2024-04-01/change/medicalnetwork/eligibility/v3";

    /// <summary>Key Vault holding per-practice keys (stedi-apikey-{tenantId}). No keys live in configuration.</summary>
    public string? KeyVaultUri { get; set; }

    public int CredentialCacheMinutes { get; set; } = 5;

    public SharedStediAccountOptions SharedAccount { get; set; } = new();
}

/// <summary>
/// Aurelianware's own Stedi account, for pilot practices that have not installed
/// the app yet. Used only when <see cref="Enabled"/> is on AND the practice's
/// connection is in Shared mode. Every use is logged.
/// </summary>
public sealed class SharedStediAccountOptions
{
    public bool Enabled { get; set; }

    /// <summary>Key Vault secret name of the shared key (e.g. stedi-apikey-shared).</summary>
    public string? SecretName { get; set; }
}

/// <summary>Secret names for per-practice keys. The name is derived from the tenant, never chosen freely.</summary>
public static partial class StediSecretNames
{
    public const string Prefix = "stedi-apikey-";

    /// <summary>Key Vault names allow letters, digits and hyphens, up to 127 characters.</summary>
    public static bool TryForTenant(string? tenantId, out string secretName)
    {
        secretName = string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId) || !TenantPattern().IsMatch(tenantId)) return false;
        secretName = Prefix + tenantId;
        return secretName.Length <= 127;
    }

    internal static bool IsValidSecretName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 127 && SecretPattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex TenantPattern();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex SecretPattern();
}

/// <summary>A resolved key. <see cref="ToString"/> never includes the key.</summary>
public sealed record StediCredential(string TenantId, ClearinghouseConnectionMode Mode, string ApiKey)
{
    public override string ToString() => $"StediCredential {{ TenantId = {TenantId}, Mode = {Mode} }}";
}

public enum StediCredentialFailure
{
    MissingTenant,
    NotConnected,
    NotActive,
    InvalidKeyReference,
    SharedAccountDisabled,
    KeyVaultNotConfigured,
    SecretNotFound,
    KeyVaultUnavailable
}

/// <summary>Raised instead of ever falling back to another key. Messages carry no secrets or PHI.</summary>
public sealed class StediCredentialUnavailableException(string tenantId, StediCredentialFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string TenantId { get; } = tenantId;
    public StediCredentialFailure Failure { get; } = failure;
}

public interface IStediSecretReader
{
    /// <summary>Returns the secret value, or null when the secret does not exist.</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Reads secrets with the Container App's managed identity (AZURE_CLIENT_ID
/// selects the user-assigned identity). The identity has only Key Vault
/// Secrets User (get/list) on the vault; the app never writes secrets.
/// </summary>
public sealed class KeyVaultStediSecretReader : IStediSecretReader
{
    private readonly Lazy<SecretClient?> _client;

    public KeyVaultStediSecretReader(IOptions<StediOptions> options)
    {
        _client = new Lazy<SecretClient?>(() =>
        {
            var uri = options.Value.KeyVaultUri;
            if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var vault)) return null;
            var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")
            });
            return new SecretClient(vault, credential);
        });
    }

    public bool IsConfigured => _client.Value is not null;

    public async Task<string?> GetSecretAsync(string name, CancellationToken cancellationToken)
    {
        var client = _client.Value ?? throw new InvalidOperationException("Stedi:KeyVaultUri is not configured.");
        try
        {
            var secret = await client.GetSecretAsync(name, cancellationToken: cancellationToken);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}

public interface IClearinghouseConnectionStore
{
    /// <summary>Reads the connection for exactly this tenant, regardless of the ambient tenant.</summary>
    Task<TenantClearinghouseConnection?> GetAsync(string tenantId, CancellationToken cancellationToken);

    /// <summary>Records that the practice's key was replaced in Key Vault and drops cached copies.</summary>
    Task MarkRotatedAsync(string tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Singleton-safe access to connection rows: each call opens its own scope and
/// filters by the explicit tenant, so background work never depends on an
/// ambient (possibly defaulted) tenant.
/// </summary>
public sealed class ClearinghouseConnectionStore(
    IServiceScopeFactory scopes, IStediCredentialProvider credentials, TimeProvider time) : IClearinghouseConnectionStore
{
    public async Task<TenantClearinghouseConnection?> GetAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
        return await ConnectionQueries.ForTenantAsync(db, tenantId, cancellationToken);
    }

    public async Task MarkRotatedAsync(string tenantId, CancellationToken cancellationToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
            var connection = await db.TenantClearinghouseConnections.IgnoreQueryFilters()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Provider == TenantClearinghouseConnection.StediProvider,
                    cancellationToken)
                ?? throw new StediCredentialUnavailableException(tenantId, StediCredentialFailure.NotConnected,
                    "This practice has no Stedi connection to rotate.");
            var now = time.GetUtcNow();
            connection.RotatedAt = now;
            connection.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        credentials.Invalidate(tenantId);
    }
}

internal static class ConnectionQueries
{
    public static Task<TenantClearinghouseConnection?> ForTenantAsync(
        CloudDentalDbContext db, string tenantId, CancellationToken cancellationToken) =>
        db.TenantClearinghouseConnections.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Provider == TenantClearinghouseConnection.StediProvider,
                cancellationToken);
}

public interface IStediCredentialProvider
{
    /// <summary>
    /// The Stedi key for exactly <paramref name="tenantId"/>. Throws
    /// <see cref="StediCredentialUnavailableException"/> rather than returning
    /// any other practice's or a default key.
    /// </summary>
    Task<StediCredential> GetAsync(string tenantId, CancellationToken cancellationToken = default);

    /// <summary>Drops this tenant's cached key on this instance (call after rotation).</summary>
    void Invalidate(string tenantId);
}

/// <summary>
/// Resolves per-practice keys from Key Vault with a short in-memory cache.
/// The cache key includes the secret name and the connection's RotatedAt, so a
/// rotation recorded by any replica is picked up on the next lookup here.
/// </summary>
public sealed class StediCredentialProvider : IStediCredentialProvider
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IStediSecretReader _secrets;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<StediOptions> _options;
    private readonly ILogger<StediCredentialProvider> _logger;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _evictions = new(StringComparer.Ordinal);

    public StediCredentialProvider(IServiceScopeFactory scopes, IStediSecretReader secrets, IMemoryCache cache,
        IOptionsMonitor<StediOptions> options, ILogger<StediCredentialProvider> logger)
    {
        _scopes = scopes;
        _secrets = secrets;
        _cache = cache;
        _options = options;
        _logger = logger;
    }

    public async Task<StediCredential> GetAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new StediCredentialUnavailableException(string.Empty, StediCredentialFailure.MissingTenant,
                "A Stedi credential was requested without a tenant.");

        TenantClearinghouseConnection? connection;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>();
            connection = await ConnectionQueries.ForTenantAsync(db, tenantId, cancellationToken);
        }

        if (connection is null)
            throw Fail(tenantId, StediCredentialFailure.NotConnected, "This practice is not connected to Stedi.");
        if (connection.Status != ClearinghouseConnectionStatus.Active)
            throw Fail(tenantId, StediCredentialFailure.NotActive, $"This practice's Stedi connection is {connection.Status}.");

        var options = _options.CurrentValue;
        string secretName;
        if (connection.Mode == ClearinghouseConnectionMode.Integrated)
        {
            // The name is derived from the tenant and must match the stored
            // reference, so a row can never point at another practice's key.
            if (!StediSecretNames.TryForTenant(tenantId, out secretName) ||
                !string.Equals(connection.KeyReference, secretName, StringComparison.Ordinal))
                throw Fail(tenantId, StediCredentialFailure.InvalidKeyReference,
                    "This practice's Stedi key reference is not valid for this practice.");
        }
        else
        {
            if (!options.SharedAccount.Enabled)
                throw Fail(tenantId, StediCredentialFailure.SharedAccountDisabled,
                    "The shared Stedi account is disabled; this practice needs its own Stedi connection.");
            if (!StediSecretNames.IsValidSecretName(options.SharedAccount.SecretName))
                throw Fail(tenantId, StediCredentialFailure.InvalidKeyReference, "The shared Stedi key reference is not configured.");
            secretName = options.SharedAccount.SecretName!;
            _logger.LogWarning("Tenant {TenantId} is using the shared Aurelianware Stedi account", tenantId);
        }

        var cacheKey = $"stedi-credential:{tenantId}:{secretName}:{connection.RotatedAt?.UtcTicks ?? 0}";
        if (_cache.TryGetValue(cacheKey, out string? cachedKey) && !string.IsNullOrEmpty(cachedKey))
            return new StediCredential(tenantId, connection.Mode, cachedKey);

        if (string.IsNullOrWhiteSpace(options.KeyVaultUri))
            throw Fail(tenantId, StediCredentialFailure.KeyVaultNotConfigured, "Stedi credentials are not configured for this environment.");

        string? apiKey;
        try
        {
            apiKey = await _secrets.GetSecretAsync(secretName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError("Key Vault lookup for tenant {TenantId} Stedi credential failed ({ErrorType})", tenantId, ex.GetType().Name);
            throw new StediCredentialUnavailableException(tenantId, StediCredentialFailure.KeyVaultUnavailable,
                "Stedi credentials could not be read. Try again shortly.", ex);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            throw Fail(tenantId, StediCredentialFailure.SecretNotFound, "This practice's Stedi key has not been stored yet.");

        var minutes = Math.Clamp(options.CredentialCacheMinutes, 0, 60);
        if (minutes > 0)
        {
            var eviction = _evictions.GetOrAdd(tenantId, _ => new CancellationTokenSource());
            _cache.Set(cacheKey, apiKey, new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(minutes))
                .AddExpirationToken(new CancellationChangeToken(eviction.Token)));
        }

        return new StediCredential(tenantId, connection.Mode, apiKey);
    }

    public void Invalidate(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (_evictions.TryRemove(tenantId, out var eviction))
        {
            eviction.Cancel();
            eviction.Dispose();
        }
    }

    private StediCredentialUnavailableException Fail(string tenantId, StediCredentialFailure failure, string message)
    {
        _logger.LogWarning("Stedi credential unavailable for tenant {TenantId}: {Failure}", tenantId, failure);
        return new StediCredentialUnavailableException(tenantId, failure, message);
    }
}

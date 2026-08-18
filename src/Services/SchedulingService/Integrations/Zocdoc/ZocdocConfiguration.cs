using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;

namespace SchedulingService.Integrations.Zocdoc;

public enum ZocdocEnvironment { Sandbox, Production }

public sealed record ZocdocEndpoints(Uri ApiBaseUri, Uri TokenUri, string Audience)
{
    public static ZocdocEndpoints For(ZocdocEnvironment environment) => environment switch
    {
        ZocdocEnvironment.Sandbox => new(
            new("https://api-developer-sandbox.zocdoc.com/"),
            new("https://auth-api-developer-sandbox.zocdoc.com/oauth/token"),
            "https://api-developer-sandbox.zocdoc.com/"),
        ZocdocEnvironment.Production => new(
            new("https://api-developer.zocdoc.com/"),
            new("https://auth.zocdoc.com/oauth/token"),
            "https://api-developer.zocdoc.com/"),
        _ => throw new ZocdocIntegrationException(ZocdocFailureKind.Misconfiguration,
            $"Unsupported Zocdoc environment '{environment}'.")
    };

    public static ZocdocEnvironment Parse(string value) =>
        Enum.TryParse<ZocdocEnvironment>(value, true, out var environment) ? environment
            : throw new ZocdocIntegrationException(ZocdocFailureKind.Misconfiguration,
                "Zocdoc environment must be Sandbox or Production.");
}

public sealed record ZocdocCredentials(string ClientId, string ClientSecret, string? WebhookSecret);

public interface IZocdocCredentialProvider
{
    Task<ZocdocCredentials> GetAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the integration's opaque CredentialReference through the ASP.NET
/// configuration providers. In production those values can be backed by
/// Container App secrets or Key Vault; credentials never enter the database.
/// </summary>
public sealed class ConfigurationZocdocCredentialProvider(IConfiguration configuration) : IZocdocCredentialProvider
{
    public Task<ZocdocCredentials> GetAsync(string tenantId, SchedulingIntegrationConfiguration integration,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(integration.CredentialReference))
            throw new ZocdocIntegrationException(ZocdocFailureKind.Misconfiguration,
                "Zocdoc CredentialReference is not configured.");
        var section = configuration.GetSection($"SchedulingCredentials:{integration.CredentialReference}");
        var clientId = section["ClientId"];
        var clientSecret = section["ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new ZocdocIntegrationException(ZocdocFailureKind.Misconfiguration,
                "Zocdoc client credentials could not be resolved.");
        return Task.FromResult(new ZocdocCredentials(clientId, clientSecret, section["WebhookSecret"]));
    }
}

public interface IZocdocAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default);
}

public sealed class ZocdocAccessTokenProvider(
    IHttpClientFactory httpClientFactory,
    IZocdocCredentialProvider credentialProvider,
    ISchedulingClock clock,
    ILogger<ZocdocAccessTokenProvider> logger) : IZocdocAccessTokenProvider
{
    private readonly ConcurrentDictionary<TokenCacheKey, TokenEntry> _tokens = new();
    private readonly ConcurrentDictionary<TokenCacheKey, SemaphoreSlim> _locks = new();

    public async Task<string> GetAccessTokenAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        RemoveExpiredTokens();
        var environment = ZocdocEndpoints.Parse(configuration.Environment);
        var credentials = await credentialProvider.GetAsync(tenantId, configuration, cancellationToken);
        var key = new TokenCacheKey(tenantId, environment, credentials.ClientId);
        if (TryGetCached(key, out var token)) return token;

        var gate = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetCached(key, out token)) return token;
            var endpoints = ZocdocEndpoints.For(environment);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoints.TokenUri)
            {
                Content = JsonContent.Create(new ZocdocTokenRequest(
                    "client_credentials", credentials.ClientId, credentials.ClientSecret, endpoints.Audience))
            };
            using var response = await httpClientFactory.CreateClient("ZocdocAuth")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw ZocdocIntegrationException.FromTokenResponse(
                    response.StatusCode, response.Headers.RetryAfter?.Delta);
            var payload = await response.Content.ReadFromJsonAsync<ZocdocTokenResponse>(cancellationToken)
                ?? throw new ZocdocIntegrationException(ZocdocFailureKind.Authentication,
                    "Zocdoc returned an empty OAuth token response.");
            if (string.IsNullOrWhiteSpace(payload.AccessToken) || payload.ExpiresIn <= 0)
                throw new ZocdocIntegrationException(ZocdocFailureKind.Authentication,
                    "Zocdoc returned an invalid OAuth token response.");
            var expiresAt = clock.UtcNow.AddSeconds(payload.ExpiresIn);
            _tokens[key] = new TokenEntry(payload.AccessToken, expiresAt);
            logger.LogInformation(
                "Zocdoc operation {Operation} for tenant {TenantId}, channel {Channel} completed with {Result}",
                "AcquireToken", tenantId, SchedulingChannel.Zocdoc, "Success");
            return payload.AccessToken;
        }
        finally { gate.Release(); }
    }

    private bool TryGetCached(TokenCacheKey key, out string token)
    {
        if (_tokens.TryGetValue(key, out var entry) && entry.ExpiresAt > clock.UtcNow.AddMinutes(1))
        {
            token = entry.AccessToken;
            return true;
        }
        token = string.Empty;
        return false;
    }

    private void RemoveExpiredTokens()
    {
        var refreshThreshold = clock.UtcNow.AddMinutes(1);
        foreach (var entry in _tokens)
            if (entry.Value.ExpiresAt <= refreshThreshold)
                _tokens.TryRemove(entry.Key, out _);
    }

    internal int CachedTokenCount => _tokens.Count;

    private sealed record TokenCacheKey(string TenantId, ZocdocEnvironment Environment, string ClientId);
    private sealed record TokenEntry(string AccessToken, DateTimeOffset ExpiresAt);
}

internal sealed record ZocdocTokenRequest(
    [property: JsonPropertyName("grant_type")] string GrantType,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_secret")] string ClientSecret,
    [property: JsonPropertyName("audience")] string Audience);

internal sealed record ZocdocTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType);

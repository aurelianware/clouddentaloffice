using System.Net;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;

namespace SchedulingService.Integrations.Zocdoc;

public enum ZocdocFailureKind
{
    Authentication,
    Authorization,
    Throttling,
    RemoteValidation,
    TemporaryRemoteFailure,
    Misconfiguration
}

public sealed class ZocdocIntegrationException : Exception
{
    public ZocdocFailureKind Kind { get; }
    public HttpStatusCode? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
    public string? ExternalCorrelationId { get; }

    public ZocdocIntegrationException(ZocdocFailureKind kind, string message,
        HttpStatusCode? statusCode = null, TimeSpan? retryAfter = null,
        string? externalCorrelationId = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        ExternalCorrelationId = externalCorrelationId;
    }

    internal static ZocdocIntegrationException FromTokenResponse(
        HttpStatusCode statusCode, TimeSpan? retryAfter = null) => new(
        statusCode == HttpStatusCode.TooManyRequests ? ZocdocFailureKind.Throttling
            : (int)statusCode >= 500 ? ZocdocFailureKind.TemporaryRemoteFailure
            : ZocdocFailureKind.Authentication,
        statusCode == HttpStatusCode.TooManyRequests
            ? "Zocdoc OAuth token acquisition was throttled."
            : (int)statusCode >= 500
                ? "Zocdoc OAuth service is temporarily unavailable."
                : "Zocdoc rejected the configured client credentials.",
        statusCode,
        statusCode == HttpStatusCode.TooManyRequests ? retryAfter : null);
}

// Transport DTOs intentionally remain internal to the Zocdoc boundary.
internal sealed record ZocdocCollectionResponse<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; init; } = [];
    [JsonPropertyName("next_page_token")] public string? NextPageToken { get; init; }
}

internal sealed record ZocdocSchedulableEntityDto
{
    [JsonPropertyName("provider_location_id")] public string ProviderLocationId { get; init; } = string.Empty;
    [JsonPropertyName("provider_id")] public string? ProviderId { get; init; }
    [JsonPropertyName("npi")] public string? Npi { get; init; }
    [JsonPropertyName("first_name")] public string FirstName { get; init; } = string.Empty;
    [JsonPropertyName("last_name")] public string LastName { get; init; } = string.Empty;
    [JsonPropertyName("default_visit_reason_id")] public string? DefaultVisitReasonId { get; init; }
    [JsonPropertyName("address1")] public string? Address1 { get; init; }
    [JsonPropertyName("address2")] public string? Address2 { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("zip")] public string? Zip { get; init; }
}

internal sealed record ZocdocVisitReasonDto
{
    [JsonPropertyName("visit_reason_id")] public string VisitReasonId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("specialty_id")] public string? SpecialtyId { get; init; }
}

internal static class ZocdocMapper
{
    public static IReadOnlyList<ExternalSchedulingEntity> ToCanonical(
        IEnumerable<ZocdocSchedulableEntityDto> schedulableEntities,
        IEnumerable<ZocdocVisitReasonDto> visitReasons)
    {
        var result = new List<ExternalSchedulingEntity>();
        foreach (var entity in schedulableEntities)
        {
            var (providerId, locationId) = SplitProviderLocation(entity);
            var providerName = string.Join(' ', new[] { entity.FirstName, entity.LastName }
                .Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (!string.IsNullOrWhiteSpace(providerId))
                result.Add(new(SchedulingResourceType.Provider, providerId,
                    string.IsNullOrWhiteSpace(providerName) ? providerId : providerName));
            if (!string.IsNullOrWhiteSpace(locationId))
            {
                var locationName = string.Join(", ", new[]
                    { entity.Address1, entity.Address2, entity.City, entity.State, entity.Zip }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                result.Add(new(SchedulingResourceType.Location, locationId,
                    string.IsNullOrWhiteSpace(locationName) ? locationId : locationName));
            }
        }
        result.AddRange(visitReasons.Where(x => !string.IsNullOrWhiteSpace(x.VisitReasonId))
            .Select(x => new ExternalSchedulingEntity(SchedulingResourceType.VisitReason,
                x.VisitReasonId, string.IsNullOrWhiteSpace(x.Name) ? x.VisitReasonId : x.Name)));
        return result.DistinctBy(x => new { x.EntityType, x.ExternalId }).ToList();
    }

    private static (string ProviderId, string LocationId) SplitProviderLocation(ZocdocSchedulableEntityDto entity)
    {
        var parts = entity.ProviderLocationId.Split('|', 2, StringSplitOptions.TrimEntries);
        var providerId = !string.IsNullOrWhiteSpace(entity.ProviderId) ? entity.ProviderId : parts.FirstOrDefault();
        var locationId = parts.Length == 2 ? parts[1] : string.Empty;
        return (providerId ?? string.Empty, locationId);
    }
}

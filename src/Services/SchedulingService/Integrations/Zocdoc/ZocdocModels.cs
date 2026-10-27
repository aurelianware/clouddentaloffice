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

// Transport DTOs intentionally remain internal to the Zocdoc boundary. Shapes follow
// the responses observed from the developer sandbox (see Zocdoc.IntegrationTests).
internal sealed record ZocdocResponse<T>
{
    [JsonPropertyName("data")] public T? Data { get; init; }
    [JsonPropertyName("page")] public int? Page { get; init; }
    [JsonPropertyName("next_url")] public string? NextUrl { get; init; }
}

internal sealed record ZocdocSchedulableEntitiesData
{
    [JsonPropertyName("schedulable_entities")] public List<ZocdocSchedulableEntityDto> SchedulableEntities { get; init; } = [];
}

internal sealed record ZocdocSchedulableEntityDto
{
    // Zocdoc returns "<provider_id>|<location_id>" as the provider-location entity ID.
    [JsonPropertyName("id")] public string ProviderLocationId { get; init; } = string.Empty;
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

// GET /v1/providers?npis=... supplies the names and addresses the entity feed omits.
internal sealed record ZocdocNpiProvidersDto
{
    [JsonPropertyName("npi")] public string? Npi { get; init; }
    [JsonPropertyName("providers")] public List<ZocdocProviderDto> Providers { get; init; } = [];
}

internal sealed record ZocdocProviderDto
{
    [JsonPropertyName("provider_id")] public string? ProviderId { get; init; }
    [JsonPropertyName("first_name")] public string? FirstName { get; init; }
    [JsonPropertyName("last_name")] public string? LastName { get; init; }
    [JsonPropertyName("default_visit_reason_id")] public string? DefaultVisitReasonId { get; init; }
    [JsonPropertyName("locations")] public List<ZocdocProviderLocationDto> Locations { get; init; } = [];
}

internal sealed record ZocdocProviderLocationDto
{
    [JsonPropertyName("provider_location_id")] public string ProviderLocationId { get; init; } = string.Empty;
    [JsonPropertyName("address1")] public string? Address1 { get; init; }
    [JsonPropertyName("address2")] public string? Address2 { get; init; }
    [JsonPropertyName("city")] public string? City { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("zip_code")] public string? ZipCode { get; init; }
}

internal sealed record ZocdocVisitReasonDto
{
    [JsonPropertyName("id")] public string VisitReasonId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("specialty_id")] public string? SpecialtyId { get; init; }
}

internal sealed record ZocdocTimeslotPutRequest(
    [property: JsonPropertyName("timeslots")] IReadOnlyList<ZocdocTimeslotRequest> Timeslots);

internal sealed record ZocdocTimeslotRequest
{
    [JsonPropertyName("provider_id")] public required string ProviderId { get; init; }
    [JsonPropertyName("location_id")] public required string LocationId { get; init; }
    [JsonPropertyName("start_time")] public required string StartTime { get; init; }
    [JsonPropertyName("time_zone")] public required string TimeZone { get; init; }
    [JsonPropertyName("allowed_visit_reason_ids")] public required IReadOnlyList<string> AllowedVisitReasonIds { get; init; }
    [JsonPropertyName("patient_type")] public string? PatientType { get; init; }
}

internal sealed record ZocdocAppointmentDto
{
    [JsonPropertyName("appointment_id")] public string AppointmentId { get; init; } = string.Empty;
    [JsonPropertyName("appointment_status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("start_time")] public DateTimeOffset StartTime { get; init; }
    [JsonPropertyName("provider_location_id")] public string ProviderLocationId { get; init; } = string.Empty;
    [JsonPropertyName("visit_reason_id")] public string VisitReasonId { get; init; } = string.Empty;
    [JsonPropertyName("patient_type")] public string? PatientType { get; init; }
    // Not part of GET /v1/appointments/{id}; filled from the participants endpoint.
    [JsonPropertyName("patient")] public ZocdocPatientDto? Patient { get; init; }
}

internal sealed record ZocdocParticipantsDto
{
    [JsonPropertyName("provider_id")] public string? ProviderId { get; init; }
    [JsonPropertyName("location_id")] public string? LocationId { get; init; }
    [JsonPropertyName("patient")] public ZocdocPatientDto? Patient { get; init; }
}

internal sealed record ZocdocPatientDto
{
    [JsonPropertyName("patient_id")] public string? PatientId { get; init; }
    [JsonPropertyName("developer_patient_id")] public string? DeveloperPatientId { get; init; }
    [JsonPropertyName("first_name")] public string FirstName { get; init; } = string.Empty;
    [JsonPropertyName("last_name")] public string LastName { get; init; } = string.Empty;
    [JsonPropertyName("date_of_birth")] public DateOnly DateOfBirth { get; init; }
    [JsonPropertyName("sex_at_birth")] public string? SexAtBirth { get; init; }
    [JsonPropertyName("phone_number")] public string? PhoneNumber { get; init; }
    [JsonPropertyName("email_address")] public string? EmailAddress { get; init; }
    [JsonPropertyName("insurance")] public ZocdocInsuranceDto? Insurance { get; init; }
}

// Insurance the patient entered on Zocdoc; a hint for eligibility, not verified coverage.
internal sealed record ZocdocInsuranceDto
{
    [JsonPropertyName("insurance_plan_id")] public string? InsurancePlanId { get; init; }
    [JsonPropertyName("insurance_group_number")] public string? GroupNumber { get; init; }
    [JsonPropertyName("insurance_member_id")] public string? MemberId { get; init; }
    [JsonPropertyName("is_self_pay")] public bool IsSelfPay { get; init; }
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

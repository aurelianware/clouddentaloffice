using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudDentalOffice.Contracts.Scheduling;

namespace SchedulingService.Integrations.Zocdoc;

internal interface IZocdocApiClient
{
    Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots,
        CancellationToken cancellationToken = default);
    Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId,
        SchedulingIntegrationConfiguration configuration, string appointmentId,
        CancellationToken cancellationToken = default);
    Task ConfirmAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, CancellationToken cancellationToken = default);
}

internal sealed class ZocdocApiClient(
    HttpClient httpClient,
    IZocdocAccessTokenProvider tokenProvider,
    ILogger<ZocdocApiClient> logger) : IZocdocApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        _ = await GetPageAsync<ZocdocSchedulableEntityDto>(tenantId, configuration,
            "ValidateConnection", "v1/schedulable_entities?page_size=1", cancellationToken);

    public Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default) => GetAllPagesAsync<ZocdocSchedulableEntityDto>(
            tenantId, configuration, "GetSchedulableEntities", "v1/schedulable_entities?page_size=10000",
            cancellationToken);

    public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default) => GetAllPagesAsync<ZocdocVisitReasonDto>(
            tenantId, configuration, "GetVisitReasons", "v1/visit_reasons?page_size=10000", cancellationToken);

    public async Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId,
        SchedulingIntegrationConfiguration configuration, string appointmentId,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(appointmentId)) throw new ArgumentException("Appointment ID is required.");
        var endpoints = ZocdocEndpoints.For(ZocdocEndpoints.Parse(configuration.Environment));
        var token = await tokenProvider.GetAccessTokenAsync(tenantId, configuration, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(endpoints.ApiBaseUri, $"v1/appointments/{Uri.EscapeDataString(appointmentId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var correlationId = CorrelationId(response);
        if (!response.IsSuccessStatusCode) throw FromApiResponse(response, correlationId);
        return await response.Content.ReadFromJsonAsync<ZocdocAppointmentDto>(JsonOptions, cancellationToken)
            ?? throw new ZocdocIntegrationException(ZocdocFailureKind.TemporaryRemoteFailure,
                "Zocdoc returned an empty appointment response.", response.StatusCode,
                externalCorrelationId: correlationId);
    }

    public async Task ConfirmAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        var endpoints = ZocdocEndpoints.For(ZocdocEndpoints.Parse(configuration.Environment));
        var token = await tokenProvider.GetAccessTokenAsync(tenantId, configuration, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(endpoints.ApiBaseUri, "v1/appointments/confirm"))
        { Content = JsonContent.Create(new { appointment_id = appointmentId }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw FromApiResponse(response, CorrelationId(response));
    }

    public async Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(externalProviderId))
            throw new ArgumentException("External provider identifier is required.", nameof(externalProviderId));
        if (timeslots.Count > 1500)
            throw new ArgumentException("Zocdoc accepts at most 1500 timeslots per provider/date.", nameof(timeslots));
        var configurationEndpoints = ZocdocEndpoints.For(ZocdocEndpoints.Parse(configuration.Environment));
        var path = $"v1/providers/{Uri.EscapeDataString(externalProviderId)}/calendar/timeslots?date={localDate:yyyy-MM-dd}";
        var stopwatch = Stopwatch.StartNew();
        string? correlationId = null;
        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(tenantId, configuration, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(configurationEndpoints.ApiBaseUri, path))
            {
                Content = JsonContent.Create(new ZocdocTimeslotPutRequest(timeslots))
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            correlationId = CorrelationId(response);
            if (!response.IsSuccessStatusCode) throw FromApiResponse(response, correlationId);
            LogResult(tenantId, "ReplaceTimeslots", correlationId, "Success", stopwatch.Elapsed);
        }
        catch (ZocdocIntegrationException ex)
        {
            LogResult(tenantId, "ReplaceTimeslots", ex.ExternalCorrelationId ?? correlationId,
                ex.Kind.ToString(), stopwatch.Elapsed);
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogResult(tenantId, "ReplaceTimeslots", correlationId, "TemporaryRemoteFailure", stopwatch.Elapsed);
            throw new ZocdocIntegrationException(ZocdocFailureKind.TemporaryRemoteFailure,
                "Zocdoc could not be reached.", externalCorrelationId: correlationId, innerException: ex);
        }
    }

    private async Task<IReadOnlyList<T>> GetAllPagesAsync<T>(string tenantId,
        SchedulingIntegrationConfiguration configuration, string operation, string initialPath,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var path = initialPath;
        do
        {
            var page = await GetPageAsync<T>(tenantId, configuration, operation, path, cancellationToken);
            results.AddRange(page.Data);
            path = string.IsNullOrWhiteSpace(page.NextPageToken) ? string.Empty
                : $"{initialPath}&next_page_token={Uri.EscapeDataString(page.NextPageToken)}";
        } while (!string.IsNullOrEmpty(path));
        return results;
    }

    private async Task<ZocdocCollectionResponse<T>> GetPageAsync<T>(string tenantId,
        SchedulingIntegrationConfiguration configuration, string operation, string path,
        CancellationToken cancellationToken)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        var environment = ZocdocEndpoints.Parse(configuration.Environment);
        var endpoints = ZocdocEndpoints.For(environment);
        var stopwatch = Stopwatch.StartNew();
        string? correlationId = null;
        try
        {
            var token = await tokenProvider.GetAccessTokenAsync(tenantId, configuration, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoints.ApiBaseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            correlationId = CorrelationId(response);
            if (!response.IsSuccessStatusCode)
                throw FromApiResponse(response, correlationId);
            var payload = await response.Content.ReadFromJsonAsync<ZocdocCollectionResponse<T>>(
                JsonOptions, cancellationToken);
            if (payload is null)
                throw new ZocdocIntegrationException(ZocdocFailureKind.TemporaryRemoteFailure,
                    "Zocdoc returned an empty response.", response.StatusCode,
                    externalCorrelationId: correlationId);
            LogResult(tenantId, operation, correlationId, "Success", stopwatch.Elapsed);
            return payload;
        }
        catch (ZocdocIntegrationException ex)
        {
            LogResult(tenantId, operation, ex.ExternalCorrelationId ?? correlationId,
                ex.Kind.ToString(), stopwatch.Elapsed);
            throw;
        }
        catch (HttpRequestException ex)
        {
            LogResult(tenantId, operation, correlationId, "TemporaryRemoteFailure", stopwatch.Elapsed);
            throw new ZocdocIntegrationException(ZocdocFailureKind.TemporaryRemoteFailure,
                "Zocdoc could not be reached.", externalCorrelationId: correlationId, innerException: ex);
        }
    }

    private static ZocdocIntegrationException FromApiResponse(
        HttpResponseMessage response, string? correlationId)
    {
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var kind = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => ZocdocFailureKind.Authentication,
            HttpStatusCode.Forbidden => ZocdocFailureKind.Authorization,
            HttpStatusCode.TooManyRequests => ZocdocFailureKind.Throttling,
            HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity =>
                ZocdocFailureKind.RemoteValidation,
            _ when (int)response.StatusCode >= 500 => ZocdocFailureKind.TemporaryRemoteFailure,
            _ => ZocdocFailureKind.RemoteValidation
        };
        return new(kind, $"Zocdoc request failed with HTTP {(int)response.StatusCode}.",
            response.StatusCode, retryAfter, correlationId);
    }

    private static string? CorrelationId(HttpResponseMessage response)
    {
        foreach (var header in new[] { "x-correlation-id", "x-request-id", "request-id" })
            if (response.Headers.TryGetValues(header, out var values)) return values.FirstOrDefault();
        return null;
    }

    private void LogResult(string tenantId, string operation, string? correlationId,
        string result, TimeSpan duration) => logger.LogInformation(
        "Zocdoc operation for tenant {TenantId}, channel {Channel}, operation {Operation}, " +
        "external correlation {ExternalCorrelationId}, result {Result}, duration {DurationMs}ms",
        tenantId, SchedulingChannel.Zocdoc, operation, correlationId, result, duration.TotalMilliseconds);
}

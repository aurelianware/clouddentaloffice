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
    Task CancelAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, CancellationToken cancellationToken = default);
    Task RescheduleAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, DateTimeOffset startTime, CancellationToken cancellationToken = default);
    Task UpdateAppointmentStatusAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, string status, CancellationToken cancellationToken = default);
}

internal sealed class ZocdocApiClient(
    HttpClient httpClient,
    IZocdocAccessTokenProvider tokenProvider,
    ILogger<ZocdocApiClient> logger) : IZocdocApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const int ProviderLookupBatchSize = 50;

    public async Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        _ = await GetAsync<ZocdocSchedulableEntitiesData>(tenantId, configuration,
            "ValidateConnection", "v1/schedulable_entities?page_size=1", cancellationToken);

    public async Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var entities = await GetAllPagesAsync<ZocdocSchedulableEntitiesData, ZocdocSchedulableEntityDto>(
            tenantId, configuration, "GetSchedulableEntities", "v1/schedulable_entities?page_size=10000",
            data => data.SchedulableEntities, cancellationToken);

        // The entity feed carries only IDs and NPIs, so names and addresses come from the
        // provider directory, looked up by NPI in the batches Zocdoc allows.
        var locations = new Dictionary<string, (ZocdocProviderDto Provider, ZocdocProviderLocationDto Location)>(
            StringComparer.Ordinal);
        foreach (var batch in entities.Select(x => x.Npi).Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal).Chunk(ProviderLookupBatchSize))
        {
            var page = await GetAsync<List<ZocdocNpiProvidersDto>>(tenantId, configuration, "GetProviders",
                $"v1/providers?npis={string.Join(',', batch.Select(npi => Uri.EscapeDataString(npi!)))}", cancellationToken);
            foreach (var provider in (page.Data ?? Enumerable.Empty<ZocdocNpiProvidersDto>()).SelectMany(x => x.Providers))
            foreach (var location in provider.Locations.Where(x => !string.IsNullOrWhiteSpace(x.ProviderLocationId)))
                locations.TryAdd(location.ProviderLocationId, (provider, location));
        }

        return entities.Select(entity => locations.TryGetValue(entity.ProviderLocationId, out var match)
            ? entity with
            {
                ProviderId = match.Provider.ProviderId ?? entity.ProviderId,
                FirstName = match.Provider.FirstName ?? entity.FirstName,
                LastName = match.Provider.LastName ?? entity.LastName,
                DefaultVisitReasonId = match.Provider.DefaultVisitReasonId ?? entity.DefaultVisitReasonId,
                Address1 = match.Location.Address1, Address2 = match.Location.Address2,
                City = match.Location.City, State = match.Location.State, Zip = match.Location.ZipCode
            }
            : entity).ToList();
    }

    public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(
        string tenantId, SchedulingIntegrationConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        GetAllPagesAsync<List<ZocdocVisitReasonDto>, ZocdocVisitReasonDto>(
            tenantId, configuration, "GetVisitReasons", "v1/visit_reasons?page_size=10000",
            data => data, cancellationToken);

    public async Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId,
        SchedulingIntegrationConfiguration configuration, string appointmentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appointmentId)) throw new ArgumentException("Appointment ID is required.");
        var id = Uri.EscapeDataString(appointmentId);
        var appointment = (await GetAsync<ZocdocAppointmentDto>(tenantId, configuration, "GetAppointment",
            $"v1/appointments/{id}", cancellationToken)).Data
            ?? throw new ZocdocIntegrationException(ZocdocFailureKind.TemporaryRemoteFailure,
                "Zocdoc returned an empty appointment response.");

        // Patient demographics and insurance are only returned by the participants endpoint.
        ZocdocParticipantsDto? participants;
        try
        {
            participants = (await GetAsync<ZocdocParticipantsDto>(tenantId, configuration,
                "GetAppointmentParticipants", $"v1/appointments/{id}/participants", cancellationToken)).Data;
        }
        catch (ZocdocIntegrationException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            participants = null;
        }

        return appointment with
        {
            AppointmentId = string.IsNullOrWhiteSpace(appointment.AppointmentId) ? appointmentId : appointment.AppointmentId,
            Patient = participants?.Patient
        };
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

    public Task CancelAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, CancellationToken cancellationToken = default) => SendAppointmentActionAsync(
        tenantId, configuration, HttpMethod.Post, "v1/appointments/cancel",
        new { appointment_id = appointmentId, cancellation_reason_type = "other_provider_reason" }, cancellationToken);

    public Task RescheduleAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, DateTimeOffset startTime, CancellationToken cancellationToken = default) => SendAppointmentActionAsync(
        tenantId, configuration, HttpMethod.Post, "v1/appointments/reschedule",
        new { appointment_id = appointmentId, start_time = startTime.ToString("O") }, cancellationToken);

    public Task UpdateAppointmentStatusAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        string appointmentId, string status, CancellationToken cancellationToken = default)
    {
        if (status is not ("arrived" or "no_show")) throw new ArgumentException("Unsupported Zocdoc status.", nameof(status));
        return SendAppointmentActionAsync(tenantId, configuration, HttpMethod.Put, "v1/appointments/update-status",
            new { appointment_id = appointmentId, appointment_status = status }, cancellationToken);
    }

    private async Task SendAppointmentActionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
        HttpMethod method, string path, object payload, CancellationToken cancellationToken)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        var endpoints = ZocdocEndpoints.For(ZocdocEndpoints.Parse(configuration.Environment));
        var token = await tokenProvider.GetAccessTokenAsync(tenantId, configuration, cancellationToken);
        using var request = new HttpRequestMessage(method, new Uri(endpoints.ApiBaseUri, path))
            { Content = JsonContent.Create(payload) };
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

    private async Task<IReadOnlyList<TItem>> GetAllPagesAsync<TData, TItem>(string tenantId,
        SchedulingIntegrationConfiguration configuration, string operation, string initialPath,
        Func<TData, IEnumerable<TItem>> items, CancellationToken cancellationToken)
    {
        var results = new List<TItem>();
        var pageNumber = 0;
        while (true)
        {
            var path = pageNumber == 0 ? initialPath : $"{initialPath}&page={pageNumber}";
            var page = await GetAsync<TData>(tenantId, configuration, operation, path, cancellationToken);
            if (page.Data is not null) results.AddRange(items(page.Data));
            if (string.IsNullOrWhiteSpace(page.NextUrl)) return results;
            pageNumber = (page.Page ?? pageNumber) + 1;
        }
    }

    private async Task<ZocdocResponse<T>> GetAsync<T>(string tenantId,
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
            ZocdocResponse<T>? payload;
            try
            {
                payload = await response.Content.ReadFromJsonAsync<ZocdocResponse<T>>(JsonOptions, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new ZocdocIntegrationException(ZocdocFailureKind.RemoteValidation,
                    "Zocdoc returned a response in an unexpected format.", response.StatusCode,
                    externalCorrelationId: correlationId, innerException: ex);
            }
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

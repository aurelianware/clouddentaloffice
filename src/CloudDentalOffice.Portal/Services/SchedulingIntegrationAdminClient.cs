using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace CloudDentalOffice.Portal.Services;

public enum SchedulingResourceType { Provider, Location, VisitReason }

public sealed record SchedulingIntegrationOverview(
    SchedulingChannel Channel, bool Enabled, string Environment, string ConnectionStatus,
    bool CredentialReferenceConfigured, string? CredentialReference, string TimeZoneId,
    int MinimumBookingLeadMinutes, int MaximumBookingHorizonDays,
    DateTime? LastSuccessfulSynchronization, string? LastError,
    int MappedProviders, int MappedLocations, int MappedVisitReasons);
public sealed class SchedulingIntegrationConfigurationInput
{
    public SchedulingIntegrationConfigurationInput(bool enabled, string environment, string? credentialReference,
        string timeZoneId, int minimumBookingLeadMinutes, int maximumBookingHorizonDays) =>
        (Enabled, Environment, CredentialReference, TimeZoneId, MinimumBookingLeadMinutes, MaximumBookingHorizonDays) =
        (enabled, environment, credentialReference, timeZoneId, minimumBookingLeadMinutes, maximumBookingHorizonDays);
    public bool Enabled { get; set; }
    public string Environment { get; set; }
    public string? CredentialReference { get; set; }
    public string TimeZoneId { get; set; }
    public int MinimumBookingLeadMinutes { get; set; }
    public int MaximumBookingHorizonDays { get; set; }
}
public sealed record SchedulingMapping(
    Guid Id, string TenantId, SchedulingChannel Channel, SchedulingResourceType EntityType,
    string InternalId, string ExternalId, string? ExternalDisplayName, bool IsActive,
    DateTime CreatedAt, DateTime UpdatedAt);
public sealed record SchedulingInternalEntity(
    SchedulingResourceType EntityType, string InternalId, string DisplayName, bool IsActive);
public sealed record ExternalSchedulingEntity(
    SchedulingResourceType EntityType, string ExternalId, string DisplayName);
public sealed record IntegrationDiagnostic(
    string? Diagnostic, string? LastSyncError, string Status, DateTime? LastAttemptAt,
    DateTime? UpdatedAt, string? PendingOperation);
internal sealed record AvailabilityDiagnosticDto(
    string? Diagnostic, int Status, DateTime? LastAttemptAt);
internal sealed record AppointmentDiagnosticDto(
    string? LastSyncError, int SyncStatus, DateTime? UpdatedAt, string? PendingOperation);

public interface ISchedulingIntegrationAdminClient
{
    Task<SchedulingIntegrationOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(SchedulingIntegrationConfigurationInput input, CancellationToken cancellationToken = default);
    Task TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalSchedulingEntity>> RefreshExternalEntitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingMapping>> GetMappingsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingInternalEntity>> GetUnmappedAsync(SchedulingResourceType type, CancellationToken cancellationToken = default);
    Task SaveMappingAsync(SchedulingResourceType type, string internalId, string externalId,
        string displayName, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(DateTimeOffset from, DateTimeOffset to,
        int? providerId, Guid? locationId, string? appointmentTypeId, PatientRelationship relationship,
        CancellationToken cancellationToken = default);
    Task ReconcileAsync(DateTimeOffset from, DateTimeOffset to, int? providerId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IntegrationDiagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
}

public sealed class SchedulingIntegrationAdminClient(HttpClient http) : ISchedulingIntegrationAdminClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<SchedulingIntegrationOverview> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        GetAsync<SchedulingIntegrationOverview>("api/scheduling-integrations/zocdoc/overview", cancellationToken);
    public async Task SaveConfigurationAsync(SchedulingIntegrationConfigurationInput input, CancellationToken cancellationToken = default)
    {
        // Uses the channel route while preserving the same concrete URL shape accepted before it was generalized.
        using var response = await http.PutAsJsonAsync("api/scheduling-integrations/Zocdoc/configuration", input, cancellationToken);
        await SendAsync(response, cancellationToken);
    }
    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/scheduling-integrations/zocdoc/test-connection", null, cancellationToken);
        await SendAsync(response, cancellationToken);
    }
    public Task<IReadOnlyList<ExternalSchedulingEntity>> RefreshExternalEntitiesAsync(CancellationToken cancellationToken = default) =>
        PostAndReadAsync<ExternalSchedulingEntity>("api/scheduling-integrations/zocdoc/external-entities/refresh", cancellationToken);
    public Task<IReadOnlyList<SchedulingMapping>> GetMappingsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<SchedulingMapping>("api/scheduling-integrations/zocdoc/mappings/?includeInactive=false", cancellationToken);
    public Task<IReadOnlyList<SchedulingInternalEntity>> GetUnmappedAsync(SchedulingResourceType type, CancellationToken cancellationToken = default) =>
        GetListAsync<SchedulingInternalEntity>($"api/scheduling-integrations/zocdoc/mappings/unmapped/{type}", cancellationToken);
    public async Task SaveMappingAsync(SchedulingResourceType type, string internalId, string externalId,
        string displayName, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "api/scheduling-integrations/zocdoc/mappings/", new
            { EntityType = type, InternalId = internalId, ExternalId = externalId, ExternalDisplayName = displayName, IsActive = true },
            cancellationToken);
        await SendAsync(response, cancellationToken);
    }
    public Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(DateTimeOffset from, DateTimeOffset to,
        int? providerId, Guid? locationId, string? appointmentTypeId, PatientRelationship relationship,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/scheduling-integrations/Zocdoc/availability?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}&patientRelationship={relationship}";
        if (providerId.HasValue) url += $"&providerId={providerId}";
        if (locationId.HasValue) url += $"&locationId={locationId}";
        if (!string.IsNullOrWhiteSpace(appointmentTypeId)) url += $"&appointmentTypeId={Uri.EscapeDataString(appointmentTypeId)}";
        return GetListAsync<SchedulingAvailabilitySlot>(url, cancellationToken);
    }
    public async Task ReconcileAsync(DateTimeOffset from, DateTimeOffset to, int? providerId,
        CancellationToken cancellationToken = default)
    {
        var url = $"api/scheduling-integrations/zocdoc/availability/reconcile?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        if (providerId.HasValue) url += $"&providerId={providerId}";
        using var response = await http.PostAsync(url, null, cancellationToken);
        await SendAsync(response, cancellationToken);
    }
    public async Task<IReadOnlyList<IntegrationDiagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var availability = await GetListAsync<AvailabilityDiagnosticDto>(
            "api/scheduling-integrations/zocdoc/availability/status", cancellationToken);
        var lifecycle = await GetListAsync<AppointmentDiagnosticDto>(
            "api/scheduling-integrations/zocdoc/appointments/status", cancellationToken);
        return availability.Select(x => new IntegrationDiagnostic(x.Diagnostic, null,
                ((AvailabilityStatus)x.Status).ToString(), x.LastAttemptAt, null, null))
            .Concat(lifecycle.Select(x => new IntegrationDiagnostic(null, x.LastSyncError,
                ((AppointmentStatus)x.SyncStatus).ToString(), null, x.UpdatedAt, x.PendingOperation)))
            .OrderByDescending(x => x.LastAttemptAt ?? x.UpdatedAt).Take(100).ToList();
    }
    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken); await SendAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken)
            ?? throw new InvalidOperationException("Scheduling integration returned no data.");
    }
    private async Task<IReadOnlyList<T>> GetListAsync<T>(string url, CancellationToken cancellationToken) =>
        await GetAsync<List<T>>(url, cancellationToken);
    private async Task<IReadOnlyList<T>> PostAndReadAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(url, null, cancellationToken); await SendAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<List<T>>(Json, cancellationToken) ?? [];
    }
    private static async Task SendAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(Sanitize(body, response.ReasonPhrase));
    }
    public static string Sanitize(string? value, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback ?? "Scheduling integration request failed.";
        try
        {
            using var json = JsonDocument.Parse(value);
            if (json.RootElement.TryGetProperty("message", out var message)) return message.GetString() ?? "Request failed.";
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var error in errors.EnumerateObject())
                {
                    if (error.Value.ValueKind != JsonValueKind.Array) continue;
                    foreach (var detail in error.Value.EnumerateArray())
                    {
                        var text = detail.GetString();
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            if (json.RootElement.TryGetProperty("title", out var title)) return title.GetString() ?? "Request failed.";
        }
        catch (JsonException) { }
        return "Scheduling integration request failed.";
    }
}

internal enum AvailabilityStatus { Pending, Succeeded, Failed, SkippedMapping, Disabled }
internal enum AppointmentStatus { Synced, Pending, Failed, Conflict }

public sealed class SchedulingAdminAuthorizationHandler(
    AuthenticationStateProvider authenticationStateProvider, IConfiguration configuration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var user = (await authenticationStateProvider.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true || !user.IsInRole("Admin"))
            throw new UnauthorizedAccessException("Administrator access is required.");
        var tenantId = user.FindFirst("TenantId")?.Value ?? user.FindFirst("tenant_id")?.Value
            ?? user.FindFirst("tenantId")?.Value;
        if (string.IsNullOrWhiteSpace(tenantId)) throw new UnauthorizedAccessException("Tenant context is required.");
        var key = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Scheduling admin authentication is not configured.");
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "CloudDentalOffice",
            audience: configuration["Jwt:Audience"] ?? "CloudDentalOffice",
            claims: [new("tenant_id", tenantId), new(ClaimTypes.Role, "Admin")],
            expires: DateTime.UtcNow.AddMinutes(2),
            signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return await base.SendAsync(request, cancellationToken);
    }
}

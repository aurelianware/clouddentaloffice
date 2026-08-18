using System.Net;
using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Scheduling;

public interface IPublicSchedulingClient
{
    Task<IReadOnlyList<PublicSchedulingAvailabilitySlot>> GetAvailabilityAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default);
    Task<ValidatedPublicSchedulingSelection?> ValidateAsync(string tenantId, string token,
        PatientRelationship relationship, CancellationToken cancellationToken = default);
}

public sealed class PublicSchedulingClient(HttpClient http, IConfiguration configuration) : IPublicSchedulingClient
{
    public async Task<IReadOnlyList<PublicSchedulingAvailabilitySlot>> GetAvailabilityAsync(string tenantId,
        PublicSchedulingAvailabilityRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await Send(tenantId, "availability", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<PublicSchedulingAvailabilitySlot>>(cancellationToken) ?? [];
    }

    public async Task<ValidatedPublicSchedulingSelection?> ValidateAsync(string tenantId, string token,
        PatientRelationship relationship, CancellationToken cancellationToken = default)
    {
        using var response = await Send(tenantId, "validate", new ValidateSlot(token, relationship), cancellationToken);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ValidatedPublicSchedulingSelection>(cancellationToken);
    }

    private async Task<HttpResponseMessage> Send<T>(string tenantId, string operation, T body, CancellationToken cancellationToken)
    {
        var client = configuration.GetSection("Services:SchedulingServiceClients").GetChildren()
            .FirstOrDefault(x => x["TenantId"] == tenantId);
        var key = client?["ApiKey"];
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Scheduling service access is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/internal/public-scheduling/{operation}")
        { Content = JsonContent.Create(body) };
        request.Headers.Add("X-CDO-Service-Key", key);
        return await http.SendAsync(request, cancellationToken);
    }

    private sealed record ValidateSlot(string AvailabilityToken, PatientRelationship PatientRelationship);
}

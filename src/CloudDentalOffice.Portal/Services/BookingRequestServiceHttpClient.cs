using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Scheduling;

namespace CloudDentalOffice.Portal.Services;

public interface IBookingRequestService
{
    Task<List<BookingRequestDto>> GetAsync(string? status = null);
    Task<BookingRequestDto?> GetAsync(Guid id);
    Task<BookingRequestDto> MatchPatientAsync(Guid id, int patientId, string? reviewedBy = null, string? notes = null);
    Task<BookingRequestDto> ChangeStatusAsync(Guid id, BookingRequestStatus status, string? reason = null, string? notes = null);
    Task<BookingRequestDto> ApproveAsync(Guid id, ApproveBookingRequest approval);
}

public sealed class BookingRequestServiceHttpClient(HttpClient http) : IBookingRequestService
{
    public async Task<List<BookingRequestDto>> GetAsync(string? status = null)
    {
        var url = "/api/booking-requests";
        if (!string.IsNullOrWhiteSpace(status)) url += $"?status={Uri.EscapeDataString(status)}";
        return await http.GetFromJsonAsync<List<BookingRequestDto>>(url) ?? [];
    }

    public async Task<BookingRequestDto?> GetAsync(Guid id) =>
        await http.GetFromJsonAsync<BookingRequestDto>($"/api/booking-requests/{id}");

    public async Task<BookingRequestDto> MatchPatientAsync(Guid id, int patientId, string? reviewedBy = null, string? notes = null)
    {
        var response = await http.PostAsJsonAsync($"/api/booking-requests/{id}/match-patient",
            new MatchBookingPatientRequest(patientId, reviewedBy, notes));
        await EnsureSuccess(response);
        return (await response.Content.ReadFromJsonAsync<BookingRequestDto>())!;
    }

    public async Task<BookingRequestDto> ChangeStatusAsync(Guid id, BookingRequestStatus status, string? reason = null, string? notes = null)
    {
        var response = await http.PostAsJsonAsync($"/api/booking-requests/{id}/status",
            new ChangeBookingRequestStatusRequest(status, null, reason, notes));
        await EnsureSuccess(response);
        return (await response.Content.ReadFromJsonAsync<BookingRequestDto>())!;
    }

    public async Task<BookingRequestDto> ApproveAsync(Guid id, ApproveBookingRequest approval)
    {
        var response = await http.PostAsJsonAsync($"/api/booking-requests/{id}/approve", approval);
        await EnsureSuccess(response);
        return (await GetAsync(id))!;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
    }
}

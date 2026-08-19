using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Portal.Models;
// Disambiguate from the internal AppointmentStatus (integration sync status) declared
// in this same namespace by the scheduling integration admin client.
using SchedStatus = CloudDentalOffice.Contracts.Scheduling.AppointmentStatus;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Appointments are owned by the SchedulingService, which is also where the
/// booking-request approval workflow creates them. This client makes the staff
/// calendar read and write that same store (through the API gateway, with a
/// scheduling tenant token) so approved and online-booked appointments appear
/// alongside manually scheduled ones. It replaces the legacy monolith
/// <see cref="AppointmentServiceImpl"/> that used the portal's own database.
/// </summary>
public sealed class AppointmentServiceHttpClient(HttpClient http) : IAppointmentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // Tolerate both string- and number-encoded enums from the scheduling service.
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<List<Appointment>> GetAppointmentsAsync(DateTime date)
    {
        var localStart = DateTime.SpecifyKind(date.Date, DateTimeKind.Local);
        var from = localStart.ToUniversalTime();
        var to = localStart.AddDays(1).ToUniversalTime();
        var url = $"/api/appointments?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";

        var dtos = await http.GetFromJsonAsync<List<AppointmentDto>>(url, Json) ?? [];
        return dtos.Select(ToModel).OrderBy(a => a.AppointmentDateTime).ToList();
    }

    public async Task<Appointment?> GetAppointmentByIdAsync(string appointmentId)
    {
        if (!Guid.TryParse(appointmentId, out var id)) return null;
        var response = await http.GetAsync($"/api/appointments/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccess(response);
        var dto = await response.Content.ReadFromJsonAsync<AppointmentDto>(Json);
        return dto is null ? null : ToModel(dto);
    }

    public async Task<Appointment> CreateAppointmentAsync(Appointment appointment)
    {
        var start = NormalizeToUtc(appointment.AppointmentDateTime);
        var request = new CreateAppointmentRequest
        {
            PatientId = appointment.PatientId,
            ProviderId = appointment.ProviderId,
            StartTime = start,
            EndTime = start.AddMinutes(appointment.DurationMinutes),
            Notes = appointment.Notes,
            AppointmentTypeId = NullIfBlank(appointment.AppointmentType),
            ReasonForVisit = NullIfBlank(appointment.ReasonForVisit)
        };
        var response = await http.PostAsJsonAsync("/api/appointments", request, Json);
        await EnsureSuccess(response);
        var created = (await response.Content.ReadFromJsonAsync<AppointmentDto>(Json))!;

        // The create endpoint always starts an appointment as Scheduled; honor a
        // different status chosen at creation time with a follow-up update.
        var desired = FromDisplayStatus(appointment.Status);
        if (desired != SchedStatus.Scheduled)
        {
            var model = ToModel(created);
            model.Status = appointment.Status;
            return await UpdateAppointmentAsync(model);
        }
        return ToModel(created);
    }

    public async Task<Appointment> UpdateAppointmentAsync(Appointment appointment)
    {
        if (!Guid.TryParse(appointment.ExternalId, out var id))
            throw new InvalidOperationException("This appointment cannot be updated because it has no scheduling identifier.");

        var start = NormalizeToUtc(appointment.AppointmentDateTime);
        var request = new UpdateAppointmentRequest
        {
            PatientId = appointment.PatientId,
            ProviderId = appointment.ProviderId,
            StartTime = start,
            EndTime = start.AddMinutes(appointment.DurationMinutes),
            Status = FromDisplayStatus(appointment.Status),
            Notes = appointment.Notes,
            AppointmentTypeId = NullIfBlank(appointment.AppointmentType),
            ReasonForVisit = NullIfBlank(appointment.ReasonForVisit)
        };
        var response = await http.PutAsJsonAsync($"/api/appointments/{id}", request, Json);
        await EnsureSuccess(response);
        var updated = (await response.Content.ReadFromJsonAsync<AppointmentDto>(Json))!;
        return ToModel(updated);
    }

    public async Task DeleteAppointmentAsync(string appointmentId)
    {
        if (!Guid.TryParse(appointmentId, out var id)) return;
        var response = await http.DeleteAsync($"/api/appointments/{id}");
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccess(response);
    }

    private static Appointment ToModel(AppointmentDto dto) => new()
    {
        ExternalId = dto.Id.ToString(),
        PatientId = dto.PatientId,
        ProviderId = dto.ProviderId,
        AppointmentDateTime = EnsureUtc(dto.StartTime),
        DurationMinutes = Math.Max(0, (int)(dto.EndTime - dto.StartTime).TotalMinutes),
        AppointmentType = dto.AppointmentTypeId ?? string.Empty,
        Status = ToDisplayStatus(dto.Status),
        Notes = dto.Notes,
        ReasonForVisit = dto.ReasonForVisit
    };

    private static string ToDisplayStatus(SchedStatus status) => status switch
    {
        SchedStatus.CheckedIn => "Checked-In",
        _ => status.ToString()
    };

    private static SchedStatus FromDisplayStatus(string? status) => status switch
    {
        "Checked-In" => SchedStatus.CheckedIn,
        _ when Enum.TryParse<SchedStatus>(status, ignoreCase: true, out var parsed) => parsed,
        _ => SchedStatus.Scheduled
    };

    private static DateTime NormalizeToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        // UI inputs are local wall-clock time with an unspecified kind.
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };

    private static DateTime EnsureUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase : detail);
    }
}

using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Scheduling;

namespace CloudDentalOffice.Portal.Services;

public interface IPatientAcquisitionClient
{
    Task<PatientAcquisitionDashboard> GetAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default);
}

public sealed class PatientAcquisitionClient(HttpClient http) : IPatientAcquisitionClient
{
    public async Task<PatientAcquisitionDashboard> GetAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        var url = $"api/reports/patient-acquisition?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}";
        return await http.GetFromJsonAsync<PatientAcquisitionDashboard>(url, cancellationToken)
            ?? throw new InvalidOperationException("Patient acquisition report returned no data.");
    }
}

using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// HTTP-based patient service that calls the PatientService microservice
/// through the API Gateway. Drop-in replacement for PatientServiceImpl.
/// 
/// The Portal's Razor pages inject IPatientService and call methods like
/// GetPatientsAsync() — they don't know or care whether the implementation
/// hits a DbContext or an HTTP endpoint. This is the strangler fig pattern.
/// </summary>
public class PatientServiceHttpClient : IPatientService
{
    private readonly HttpClient _http;
    private readonly ILogger<PatientServiceHttpClient> _logger;
    private readonly string? _tenantId;

    public PatientServiceHttpClient(
        HttpClient http,
        ILogger<PatientServiceHttpClient> logger,
        Tenancy.ITenantProvider tenantProvider)
    {
        _http = http;
        _logger = logger;
        _tenantId = tenantProvider.TenantId;
    }

    public async Task<List<Patient>> GetPatientsAsync()
    {
        try
        {
            var qs = !string.IsNullOrEmpty(_tenantId) ? $"?tenantId={_tenantId}" : "";
            var dtos = await _http.GetFromJsonAsync<List<PatientDto>>($"/api/patients{qs}");
            return dtos?.Select(MapToModel).ToList() ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving patients from microservice");
            return [];
        }
    }

    public async Task<Patient?> GetPatientByIdAsync(string patientId)
    {
        if (!int.TryParse(patientId, out var id))
            return null;

        try
        {
            var response = await _http.GetAsync($"/api/patients/{id}");
            if (!response.IsSuccessStatusCode) return null;
            var dto = await response.Content.ReadFromJsonAsync<PatientDto>();
            return dto is not null ? MapToModel(dto) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving patient {PatientId} from microservice", patientId);
            return null;
        }
    }

    public async Task<Patient> CreatePatientAsync(Patient patient)
    {
        try
        {
            var request = new CreatePatientRequest
            {
                FirstName = patient.FirstName,
                LastName = patient.LastName,
                MiddleName = patient.MiddleName,
                PreferredName = patient.PreferredName,
                DateOfBirth = patient.DateOfBirth,
                Gender = patient.Gender,
                Email = patient.Email,
                PrimaryPhone = patient.PrimaryPhone,
                SecondaryPhone = patient.SecondaryPhone,
                Address1 = patient.Address1,
                Address2 = patient.Address2,
                City = patient.City,
                State = patient.State,
                ZipCode = patient.ZipCode,
            };

            var qs = !string.IsNullOrEmpty(_tenantId) ? $"?tenantId={_tenantId}" : "";
            var response = await _http.PostAsJsonAsync($"/api/patients{qs}", request);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<PatientDto>();
            return dto is not null ? MapToModel(dto) : patient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating patient via microservice");
            throw;
        }
    }

    public async Task<Patient> UpdatePatientAsync(Patient patient)
    {
        try
        {
            var request = new UpdatePatientRequest
            {
                FirstName = patient.FirstName,
                LastName = patient.LastName,
                MiddleName = patient.MiddleName,
                PreferredName = patient.PreferredName,
                DateOfBirth = patient.DateOfBirth,
                Gender = patient.Gender,
                Email = patient.Email,
                PrimaryPhone = patient.PrimaryPhone,
                SecondaryPhone = patient.SecondaryPhone,
                Address1 = patient.Address1,
                Address2 = patient.Address2,
                City = patient.City,
                State = patient.State,
                ZipCode = patient.ZipCode,
                Status = patient.Status,
            };

            var response = await _http.PutAsJsonAsync($"/api/patients/{patient.PatientId}", request);
            response.EnsureSuccessStatusCode();

            var dto = await response.Content.ReadFromJsonAsync<PatientDto>();
            return dto is not null ? MapToModel(dto) : patient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating patient {PatientId} via microservice", patient.PatientId);
            throw;
        }
    }

    public async Task DeletePatientAsync(string patientId)
    {
        if (!int.TryParse(patientId, out var id))
            return;

        try
        {
            var response = await _http.DeleteAsync($"/api/patients/{id}");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting patient {PatientId} via microservice", patientId);
            throw;
        }
    }

    // ── Map DTO → Portal Model ──

    private static Patient MapToModel(PatientDto dto) => new()
    {
        PatientId = dto.PatientId,
        FirstName = dto.FirstName,
        LastName = dto.LastName,
        MiddleName = dto.MiddleName,
        PreferredName = dto.PreferredName,
        DateOfBirth = dto.DateOfBirth,
        Gender = dto.Gender,
        Email = dto.Email,
        PrimaryPhone = dto.PrimaryPhone,
        SecondaryPhone = dto.SecondaryPhone,
        Address1 = dto.Address1,
        Address2 = dto.Address2,
        City = dto.City,
        State = dto.State,
        ZipCode = dto.ZipCode,
        Status = dto.Status,
        CreatedDate = dto.CreatedDate,
        ModifiedDate = dto.ModifiedDate,
        Insurances = dto.Insurances.Select(MapInsurance).ToList(),
    };

    private static PatientInsurance MapInsurance(PatientInsuranceDto dto) => new()
    {
        PatientInsuranceId = dto.PatientInsuranceId,
        PatientId = dto.PatientId,
        InsurancePlanId = dto.InsurancePlanId,
        MemberId = dto.MemberId,
        GroupNumber = dto.GroupNumber,
        SequenceNumber = dto.SequenceNumber,
        EffectiveDate = dto.EffectiveDate,
        TerminationDate = dto.TerminationDate,
        IsActive = dto.IsActive,
        RelationshipToSubscriber = dto.RelationshipToSubscriber,
        SubscriberFirstName = dto.SubscriberFirstName,
        SubscriberLastName = dto.SubscriberLastName,
        SubscriberDateOfBirth = dto.SubscriberDateOfBirth,
        InsurancePlan = dto.InsurancePlan is not null ? new InsurancePlan
        {
            InsurancePlanId = dto.InsurancePlan.InsurancePlanId,
            PayerId = dto.InsurancePlan.PayerId,
            PayerName = dto.InsurancePlan.PayerName,
            PlanName = dto.InsurancePlan.PlanName,
            PlanType = dto.InsurancePlan.PlanType,
            Phone = dto.InsurancePlan.Phone,
            EdiPayerId = dto.InsurancePlan.EdiPayerId,
            EdiEnabled = dto.InsurancePlan.EdiEnabled,
            EdiSubmissionType = dto.InsurancePlan.EdiSubmissionType,
            IsActive = dto.InsurancePlan.IsActive,
        } : null!,
    };
}

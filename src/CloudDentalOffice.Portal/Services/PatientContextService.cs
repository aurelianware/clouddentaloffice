using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Holds the provider's active patient for the lifetime of the Blazor circuit.
/// This keeps patient context while navigating between clinical workflows.
/// </summary>
public sealed class PatientContextService
{
    public Patient? CurrentPatient { get; private set; }
    public int? PatientId => CurrentPatient?.PatientId;

    public void Select(Patient patient) => CurrentPatient = patient;

    public void Clear() => CurrentPatient = null;
}

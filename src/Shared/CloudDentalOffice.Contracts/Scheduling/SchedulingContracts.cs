namespace CloudDentalOffice.Contracts.Scheduling;

public record AppointmentDto
{
    public Guid Id { get; init; }
    public Guid PatientId { get; init; }
    public Guid ProviderId { get; init; }
    public string? PatientName { get; init; }
    public string? ProviderName { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public AppointmentStatus Status { get; init; }
    public string? ProcedureCodes { get; init; }
    public string? Notes { get; init; }
    public string? Operatory { get; init; }
    public Guid? LocationId { get; init; }
}

public record CreateAppointmentRequest
{
    public Guid PatientId { get; init; }
    public Guid ProviderId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string? ProcedureCodes { get; init; }
    public string? Notes { get; init; }
    public string? Operatory { get; init; }
    public Guid? LocationId { get; init; }
}

public enum AppointmentStatus
{
    Scheduled,
    Confirmed,
    CheckedIn,
    InProgress,
    Completed,
    Cancelled,
    NoShow,
    Rescheduled
}

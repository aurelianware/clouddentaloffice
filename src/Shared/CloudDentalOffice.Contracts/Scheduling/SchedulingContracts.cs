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

/// <summary>
/// Public booking intake submitted from a practice website (e.g. 3rd Set Smiles).
/// Deliberately does NOT accept Patient/Provider/Location identifiers — the
/// SchedulingService resolves those server-side from configuration so an internet
/// caller cannot target arbitrary records. Contact details are carried through to
/// the appointment notes for staff follow-up.
/// </summary>
public record PublicBookingRequest
{
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string? Email { get; init; }
    public DateTime PreferredStart { get; init; }
    public int? DurationMinutes { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
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
    Rescheduled,
    // Unconfirmed intake from a public website booking form. Appended last so
    // existing persisted integer values are unchanged.
    Requested
}

namespace CloudDentalOffice.Contracts.Scheduling;

public record AppointmentDto
{
    public Guid Id { get; init; }
    public int PatientId { get; init; }
    public int ProviderId { get; init; }
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
    public int PatientId { get; init; }
    public int ProviderId { get; init; }
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
    public PatientRelationship PatientRelationship { get; init; } = PatientRelationship.Unknown;
}

public enum PatientRelationship
{
    Unknown,
    New,
    Existing
}

public enum BookingRequestStatus
{
    New,
    InReview,
    PatientMatched,
    Approved,
    Rejected,
    Cancelled,
    NeedsFollowUp
}

public record BookingRequestDto
{
    public Guid Id { get; init; }
    public Guid EventId { get; init; }
    public string TenantId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string? Email { get; init; }
    public PatientRelationship PatientRelationship { get; init; }
    public DateTime PreferredStartUtc { get; init; }
    public int? PreferredDurationMinutes { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? SourceReference { get; init; }
    public BookingRequestStatus Status { get; init; }
    public int? MatchedPatientId { get; init; }
    public int? RequestedProviderId { get; init; }
    public Guid? RequestedLocationId { get; init; }
    public Guid? ApprovedAppointmentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? RejectedAt { get; init; }
    public string? ReviewedBy { get; init; }
    public string? ApprovedBy { get; init; }
    public string? RejectionReason { get; init; }
    public string? StaffNotes { get; init; }
}

public record MatchBookingPatientRequest(int PatientId, string? ReviewedBy, string? StaffNotes);

public record ChangeBookingRequestStatusRequest(
    BookingRequestStatus Status,
    string? ReviewedBy,
    string? Reason,
    string? StaffNotes);

public record ApproveBookingRequest
{
    public int PatientId { get; init; }
    public int ProviderId { get; init; }
    public Guid? LocationId { get; init; }
    public DateTime StartTimeUtc { get; init; }
    public int DurationMinutes { get; init; }
    public string? Notes { get; init; }
    public string? Operatory { get; init; }
    public string? ApprovedBy { get; init; }
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

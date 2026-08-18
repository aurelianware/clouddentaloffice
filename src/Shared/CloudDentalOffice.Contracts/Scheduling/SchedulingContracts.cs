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
    public string? RequestId { get; init; }
    public string? Status { get; init; }
    public DateTime? CreatedAt { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string? Email { get; init; }
    public DateTime PreferredStart { get; init; }
    public int? DurationMinutes { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
    public PatientRelationship PatientRelationship { get; init; } = PatientRelationship.Unknown;
    public string? PreferredContact { get; init; }
    public DateTime? AlternateStart { get; init; }
    public string? InsuranceIntent { get; init; }
    public string? InsuranceCarrier { get; init; }
    public string? Source { get; init; }
    public string? Campaign { get; init; }
    public Dictionary<string, string>? Attribution { get; init; }
}

public enum PatientRelationship
{
    Unknown,
    New,
    Existing
}

/// <summary>
/// Identifies the scheduling boundary through which a request originated.
/// Vendor-specific behavior belongs in an adapter registered for the channel.
/// </summary>
public enum SchedulingChannel
{
    Internal,
    PublicWebsite,
    Zocdoc,
    Google,
    Other
}

public sealed record SchedulingProvider
{
    public required string TenantId { get; init; }
    public required int ProviderId { get; init; }
    public required Guid LocationId { get; init; }
    public IReadOnlyDictionary<SchedulingChannel, string> ExternalMappings { get; init; }
        = new Dictionary<SchedulingChannel, string>();
}

public sealed record SchedulingAppointmentType
{
    public required string TenantId { get; init; }
    public required string AppointmentTypeId { get; init; }
    public required string DisplayName { get; init; }
    public required int DurationMinutes { get; init; }
    public int? ProviderId { get; init; }
    public Guid? LocationId { get; init; }
    public bool NewPatientAllowed { get; init; }
    public bool ExistingPatientAllowed { get; init; }
    public bool IsActive { get; init; } = true;
}

public sealed record SchedulingAvailabilitySlot
{
    public required string TenantId { get; init; }
    public required int ProviderId { get; init; }
    public required Guid LocationId { get; init; }
    public required string AppointmentTypeId { get; init; }
    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }
    public PatientRelationship PatientRelationship { get; init; } = PatientRelationship.Unknown;
}

public sealed record SchedulingAvailabilityQuery
{
    public required string TenantId { get; init; }
    public required DateTime FromUtc { get; init; }
    public required DateTime ToUtc { get; init; }
    public int? ProviderId { get; init; }
    public Guid? LocationId { get; init; }
    public string? AppointmentTypeId { get; init; }
    public PatientRelationship PatientRelationship { get; init; } = PatientRelationship.Unknown;
}

/// <summary>
/// Canonical, vendor-neutral booking input. PatientRelationship is routing
/// information only; ResolvedPatientId is required before an Appointment may
/// be created by an internal scheduling application service.
/// </summary>
public sealed record SchedulingBookingCommand
{
    public required string TenantId { get; init; }
    public required SchedulingChannel Channel { get; init; }
    public required string ExternalEventId { get; init; }
    public required string ExternalAppointmentId { get; init; }
    public required int ResolvedPatientId { get; init; }
    public required int ProviderId { get; init; }
    public required Guid LocationId { get; init; }
    public required string AppointmentTypeId { get; init; }
    public required DateTime StartUtc { get; init; }
    public required DateTime EndUtc { get; init; }
    public PatientRelationship PatientRelationship { get; init; } = PatientRelationship.Unknown;
    public string? ExternalProviderId { get; init; }
    public string? ExternalLocationId { get; init; }
    public string? ExternalVisitReasonId { get; init; }
}

public sealed record SchedulingBookingResult(Guid AppointmentId, bool Created);

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
    public string? WebsiteRequestId { get; init; }
    public PatientRelationship PatientRelationship { get; init; }
    public DateTime PreferredStartUtc { get; init; }
    public DateTime? AlternateStartUtc { get; init; }
    public int? PreferredDurationMinutes { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
    public string? PreferredContact { get; init; }
    public string? InsuranceIntent { get; init; }
    public string? InsuranceCarrier { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? Campaign { get; init; }
    public string? AttributionId { get; init; }
    public string? AttributionMetadataJson { get; init; }
    public string? SourceReference { get; init; }
    public BookingRequestStatus Status { get; init; }
    public int? MatchedPatientId { get; init; }
    public int? RequestedProviderId { get; init; }
    public Guid? RequestedLocationId { get; init; }
    public Guid? ApprovedAppointmentId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime SubmittedAtUtc { get; init; }
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

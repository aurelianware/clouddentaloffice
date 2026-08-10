namespace CloudDentalOffice.Contracts.Events;

/// <summary>
/// Base record for all integration events published between services.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
    public string? CorrelationId { get; init; }
}

// ── Patient Events ──
public record PatientCreatedEvent(Guid PatientId, string FirstName, string LastName) : IntegrationEvent;
public record PatientUpdatedEvent(Guid PatientId) : IntegrationEvent;

// ── Scheduling Events ──

/// <summary>
/// Raised by the public IntakeService when a website visitor submits a booking
/// request. Carries only visitor-supplied contact details and a preferred time —
/// no PatientId/ProviderId/LocationId. A private consumer (SchedulingService)
/// resolves those from configuration and creates the (unconfirmed) appointment,
/// so the internet-facing tier never touches the scheduling database or PHI.
/// </summary>
public record BookingRequestedEvent(
    string Name,
    string Phone,
    string? Email,
    DateTime PreferredStartUtc,
    int? DurationMinutes,
    string? Reason,
    string? Message) : IntegrationEvent;

public record AppointmentScheduledEvent(Guid AppointmentId, Guid PatientId, Guid ProviderId, DateTime StartTime) : IntegrationEvent;
public record AppointmentCompletedEvent(Guid AppointmentId, Guid PatientId, string? ProcedureCodes) : IntegrationEvent;
public record AppointmentCancelledEvent(Guid AppointmentId, Guid PatientId, string? Reason) : IntegrationEvent;

// ── Claims Events ──
public record ClaimCreatedEvent(Guid ClaimId, Guid PatientId, decimal TotalCharge) : IntegrationEvent;
public record ClaimSubmittedEvent(Guid ClaimId, string? ClaimControlNumber, string SubmissionMethod) : IntegrationEvent;
public record ClaimAdjudicatedEvent(Guid ClaimId, string Status, decimal? PaidAmount) : IntegrationEvent;
public record ClaimDeniedEvent(Guid ClaimId, string? DenialReason) : IntegrationEvent;

// ── Eligibility Events ──
public record EligibilityCheckedEvent(Guid PatientId, string PayerId, string Status) : IntegrationEvent;

// ── ERA Events ──
public record EraReceivedEvent(Guid EraFileId, string PayerId, int ClaimCount, decimal TotalPayment) : IntegrationEvent;
public record EraPostedEvent(Guid EraFileId, int MatchedClaims, int UnmatchedClaims) : IntegrationEvent;

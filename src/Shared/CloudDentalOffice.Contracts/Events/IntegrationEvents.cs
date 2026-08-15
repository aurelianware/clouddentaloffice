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
/// persists a BookingRequest for explicit staff review. The public tier has no
/// read access to patient, clinical, or scheduling databases.
/// </summary>
public record BookingRequestedEvent(
    string Name,
    string Phone,
    string? Email,
    DateTime PreferredStartUtc,
    int? DurationMinutes,
    string? Reason,
    string? Message,
    Scheduling.PatientRelationship PatientRelationship = Scheduling.PatientRelationship.Unknown,
    string TenantId = "default",
    string Source = "PublicWebsite",
    string? SourceReference = null) : IntegrationEvent
{
    // Additive v2 fields keep the original positional contract deserializable
    // for existing publishers and consumers.
    public int ContractVersion { get; init; } = 2;
    public string? WebsiteRequestId { get; init; }
    public string? PreferredContact { get; init; }
    public DateTime? AlternateStartUtc { get; init; }
    public string? InsuranceIntent { get; init; }
    public string? InsuranceCarrier { get; init; }
    public string? Campaign { get; init; }
    public string? AttributionId { get; init; }
    public IReadOnlyDictionary<string, string>? AttributionMetadata { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
}

public record AppointmentScheduledEvent(Guid AppointmentId, int PatientId, int ProviderId, DateTime StartTime) : IntegrationEvent;
public record AppointmentCompletedEvent(Guid AppointmentId, int PatientId, string? ProcedureCodes) : IntegrationEvent;
public record AppointmentCancelledEvent(Guid AppointmentId, int PatientId, string? Reason) : IntegrationEvent;

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

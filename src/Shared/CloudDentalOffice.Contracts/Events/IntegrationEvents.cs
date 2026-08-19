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
/// request. Carries visitor-supplied contact details plus optional routing IDs
/// that IntakeService resolved from a server-validated opaque slot token. It
/// never carries PatientId or arbitrary public-supplied CDO identifiers. A private consumer (SchedulingService)
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
    // Missing on legacy payloads, so v1 must remain the deserialization default.
    // V2 publishers set this explicitly when they populate the additive fields.
    public int ContractVersion { get; init; } = 1;
    public string? WebsiteRequestId { get; init; }
    public string? PreferredContact { get; init; }
    public DateTime? AlternateStartUtc { get; init; }
    public string? InsuranceIntent { get; init; }
    public string? InsuranceCarrier { get; init; }
    public string? Campaign { get; init; }
    public string? AttributionId { get; init; }
    public IReadOnlyDictionary<string, string>? AttributionMetadata { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    // V3 fields are resolved from an opaque availability token by IntakeService.
    // They are never accepted as arbitrary identifiers from the public caller.
    public int? RequestedProviderId { get; init; }
    public Guid? RequestedLocationId { get; init; }
    public string? RequestedAppointmentTypeId { get; init; }
}

public record AppointmentScheduledEvent(Guid AppointmentId, int PatientId, int ProviderId, DateTime StartTime) : IntegrationEvent;
public record AppointmentCompletedEvent(Guid AppointmentId, int PatientId, string? ProcedureCodes) : IntegrationEvent;
public record AppointmentCancelledEvent(Guid AppointmentId, int PatientId, string? Reason) : IntegrationEvent;

/// <summary>Requests targeted external-availability reconciliation without carrying patient data.</summary>
public record SchedulingAvailabilityChangedEvent(
    string TenantId,
    int? ProviderId,
    DateTime FromUtc,
    DateTime ToUtc,
    string Reason) : IntegrationEvent;

/// <summary>Verified Zocdoc webhook metadata. Contains no patient demographics.</summary>
public record ZocdocAppointmentWebhookEvent(
    string TenantId,
    string ExternalEventId,
    string AppointmentId,
    string UpdateType) : IntegrationEvent
{
    public DateTime? ExternalUpdatedAt { get; init; }
}

/// <summary>PHI-free request to synchronize a locally initiated appointment lifecycle change.</summary>
public record AppointmentLifecycleChangedEvent(
    string TenantId,
    Guid AppointmentId,
    string Operation,
    string Source,
    DateTime? StartUtc = null) : IntegrationEvent;

/// <summary>Verified, PHI-free Stripe Connect payment event accepted by IntakeService.</summary>
public record StripePaymentWebhookEvent(
    string TenantId,
    string ExternalEventId,
    string EventType,
    string ConnectedAccountId,
    string CheckoutSessionId,
    string? PaymentIntentId,
    string PaymentReference,
    long AmountMinor,
    string Currency,
    string PaymentStatus,
    bool LiveMode) : IntegrationEvent;

/// <summary>Verified, PHI-free Stripe Connect refund event accepted by IntakeService.</summary>
public record StripeRefundWebhookEvent(
    string TenantId,
    string ExternalEventId,
    string EventType,
    string ConnectedAccountId,
    string ExternalRefundId,
    string? PaymentIntentId,
    string? RefundReference,
    long AmountMinor,
    string Currency,
    string RefundStatus,
    bool LiveMode) : IntegrationEvent;

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

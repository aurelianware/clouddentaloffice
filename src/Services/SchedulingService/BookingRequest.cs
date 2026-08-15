using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using CloudDentalOffice.Contracts.Scheduling;

[Index(nameof(TenantId), nameof(EventId), IsUnique = true)]
[Index(nameof(TenantId), nameof(Status), nameof(CreatedAt))]
public sealed class BookingRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EventId { get; set; }
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(30)] public string Phone { get; set; } = string.Empty;
    [MaxLength(320)] public string? Email { get; set; }
    [MaxLength(128)] public string? WebsiteRequestId { get; set; }
    public PatientRelationship PatientRelationship { get; set; }
    public DateTime PreferredStartUtc { get; set; }
    public DateTime? AlternateStartUtc { get; set; }
    public int? PreferredDurationMinutes { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    [MaxLength(2000)] public string? Message { get; set; }
    [MaxLength(20)] public string? PreferredContact { get; set; }
    [MaxLength(20)] public string? InsuranceIntent { get; set; }
    [MaxLength(120)] public string? InsuranceCarrier { get; set; }
    [MaxLength(100)] public string Source { get; set; } = "PublicWebsite";
    [MaxLength(200)] public string? Campaign { get; set; }
    [MaxLength(200)] public string? AttributionId { get; set; }
    [MaxLength(2000)] public string? AttributionMetadataJson { get; set; }
    [MaxLength(200)] public string? SourceReference { get; set; }
    public BookingRequestStatus Status { get; set; } = BookingRequestStatus.New;
    public int? MatchedPatientId { get; set; }
    public int? RequestedProviderId { get; set; }
    public Guid? RequestedLocationId { get; set; }
    public Guid? ApprovedAppointmentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? RejectedAt { get; set; }
    [MaxLength(200)] public string? ReviewedBy { get; set; }
    [MaxLength(200)] public string? ApprovedBy { get; set; }
    [MaxLength(1000)] public string? RejectionReason { get; set; }
    [MaxLength(2000)] public string? StaffNotes { get; set; }

    public BookingRequestDto ToDto() => new()
    {
        Id = Id, EventId = EventId, TenantId = TenantId, Name = Name, Phone = Phone,
        Email = Email, WebsiteRequestId = WebsiteRequestId, PatientRelationship = PatientRelationship,
        PreferredStartUtc = PreferredStartUtc, AlternateStartUtc = AlternateStartUtc,
        PreferredDurationMinutes = PreferredDurationMinutes, Reason = Reason, Message = Message,
        PreferredContact = PreferredContact, InsuranceIntent = InsuranceIntent, InsuranceCarrier = InsuranceCarrier,
        Source = Source, Campaign = Campaign, AttributionId = AttributionId,
        AttributionMetadataJson = AttributionMetadataJson, SourceReference = SourceReference, Status = Status,
        MatchedPatientId = MatchedPatientId, RequestedProviderId = RequestedProviderId,
        RequestedLocationId = RequestedLocationId, ApprovedAppointmentId = ApprovedAppointmentId,
        CreatedAt = CreatedAt, SubmittedAtUtc = SubmittedAtUtc, UpdatedAt = UpdatedAt, ReviewedAt = ReviewedAt,
        ApprovedAt = ApprovedAt, RejectedAt = RejectedAt, ReviewedBy = ReviewedBy,
        ApprovedBy = ApprovedBy, RejectionReason = RejectionReason, StaffNotes = StaffNotes
    };
}

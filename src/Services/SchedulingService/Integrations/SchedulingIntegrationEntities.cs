using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

public enum SchedulingResourceType { Provider, Location, VisitReason }
public enum SchedulingIntegrationEventStatus { Processing, Completed, Failed }
public enum ExternalAppointmentSyncStatus { Synced, Pending, Failed, Conflict }
public enum AvailabilitySyncStatus { Pending, Succeeded, Failed, SkippedMapping, Disabled }

[Index(nameof(TenantId), nameof(Channel), IsUnique = true)]
public sealed class SchedulingIntegrationConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public SchedulingChannel Channel { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(40)] public string Environment { get; set; } = "Production";
    [MaxLength(512)] public string? CredentialReference { get; set; }
    [MaxLength(100)] public string TimeZoneId { get; set; } = "UTC";
    public int MinimumBookingLeadMinutes { get; set; }
    public int MaximumBookingHorizonDays { get; set; } = 90;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(Channel), nameof(ResourceType), nameof(ExternalId), IsUnique = true)]
[Index(nameof(TenantId), nameof(Channel), nameof(ResourceType), nameof(InternalId), IsUnique = true)]
public sealed class ExternalSchedulingResourceMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public SchedulingChannel Channel { get; set; }
    public SchedulingResourceType ResourceType { get; set; }
    [MaxLength(128)] public string InternalId { get; set; } = string.Empty;
    [MaxLength(256)] public string ExternalId { get; set; } = string.Empty;
    [MaxLength(300)] public string? ExternalDisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(AppointmentTypeId), IsUnique = true)]
public sealed class SchedulingAppointmentTypeDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(128)] public string AppointmentTypeId { get; set; } = string.Empty;
    [MaxLength(200)] public string DisplayName { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public int? ProviderId { get; set; }
    public Guid? LocationId { get; set; }
    public bool NewPatientAllowed { get; set; }
    public bool ExistingPatientAllowed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool Allows(CloudDentalOffice.Contracts.Scheduling.PatientRelationship relationship) => relationship switch
    {
        CloudDentalOffice.Contracts.Scheduling.PatientRelationship.New => NewPatientAllowed,
        CloudDentalOffice.Contracts.Scheduling.PatientRelationship.Existing => ExistingPatientAllowed,
        _ => false
    };
}

[Index(nameof(TenantId), nameof(ProviderId), nameof(LocationId), nameof(DayOfWeek))]
public sealed class SchedulingProviderWorkingHours
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public int ProviderId { get; set; }
    public Guid LocationId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public TimeOnly StartLocal { get; set; }
    public TimeOnly EndLocal { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(StartUtc), nameof(EndUtc))]
public sealed class SchedulingBlockedTime
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public int? ProviderId { get; set; }
    public Guid? LocationId { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    [MaxLength(200)] public string? Reason { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(Channel), nameof(ExternalAppointmentId), IsUnique = true)]
[Index(nameof(TenantId), nameof(AppointmentId), nameof(Channel), IsUnique = true)]
public sealed class ExternalAppointmentReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid AppointmentId { get; set; }
    public SchedulingChannel Channel { get; set; }
    [MaxLength(256)] public string ExternalAppointmentId { get; set; } = string.Empty;
    [MaxLength(256)] public string? ExternalProviderId { get; set; }
    [MaxLength(256)] public string? ExternalLocationId { get; set; }
    [MaxLength(256)] public string? ExternalVisitReasonId { get; set; }
    public ExternalAppointmentSyncStatus SyncStatus { get; set; } = ExternalAppointmentSyncStatus.Synced;
    [MaxLength(40)] public string? PendingOperation { get; set; }
    public DateTime? PendingStartUtc { get; set; }
    [MaxLength(1000)] public string? LastSyncError { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? LastExternalUpdatedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(Channel), nameof(ExternalEventId), IsUnique = true)]
public sealed class SchedulingIntegrationEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public SchedulingChannel Channel { get; set; }
    [MaxLength(256)] public string ExternalEventId { get; set; } = string.Empty;
    public SchedulingIntegrationEventStatus Status { get; set; } = SchedulingIntegrationEventStatus.Processing;
    public Guid? AppointmentId { get; set; }
    [MaxLength(1000)] public string? FailureReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

[Index(nameof(TenantId), nameof(Channel), nameof(ProviderId), nameof(LocalDate), IsUnique = true)]
public sealed class SchedulingAvailabilitySyncState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public SchedulingChannel Channel { get; set; }
    public int ProviderId { get; set; }
    public DateOnly LocalDate { get; set; }
    [MaxLength(128)] public string? ContentHash { get; set; }
    public AvailabilitySyncStatus Status { get; set; } = AvailabilitySyncStatus.Pending;
    [MaxLength(1000)] public string? Diagnostic { get; set; }
    public DateTime LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
}

using System.ComponentModel.DataAnnotations;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

public enum SchedulingResourceType { Provider, Location, VisitReason }
public enum SchedulingIntegrationEventStatus { Processing, Completed, Failed }

[Index(nameof(TenantId), nameof(Channel), IsUnique = true)]
public sealed class SchedulingIntegrationConfiguration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public SchedulingChannel Channel { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(40)] public string Environment { get; set; } = "Production";
    [MaxLength(512)] public string? CredentialReference { get; set; }
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

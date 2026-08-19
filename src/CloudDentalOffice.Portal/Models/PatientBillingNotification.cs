using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Models;

public enum PatientBillingNotificationType { NewStatement, BalanceDue, PaymentReceived, PaymentFailed }
public enum PatientBillingNotificationStatus { Scheduled, Sending, Sent, Suppressed, Failed }

[Index(nameof(TenantId), nameof(NotificationType), nameof(SourceType), nameof(SourceId), IsUnique = true)]
[Index(nameof(Status), nameof(ScheduledAt), nameof(NextAttemptAt))]
public sealed class PatientBillingNotification : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PatientAccountId { get; set; }
    public PatientBillingNotificationType NotificationType { get; set; }
    [MaxLength(32)] public string SourceType { get; set; } = string.Empty;
    [MaxLength(128)] public string SourceId { get; set; } = string.Empty;
    [MaxLength(320)] public string RecipientEmail { get; set; } = string.Empty;
    [MaxLength(120)] public string PracticeName { get; set; } = string.Empty;
    public PatientBillingNotificationStatus Status { get; set; } = PatientBillingNotificationStatus.Scheduled;
    public DateTime ScheduledAt { get; set; }
    public DateTime? SentAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    [MaxLength(128)] public string? FailureReason { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

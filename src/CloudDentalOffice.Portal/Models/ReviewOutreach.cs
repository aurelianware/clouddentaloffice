using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Models;

public enum ReviewOutreachStatus { Scheduled, Sending, Sent, Suppressed, Failed, Cancelled }
public enum ReviewOutreachChannel { Email }

[Index(nameof(TenantId), nameof(AppointmentId), nameof(Campaign), IsUnique = true)]
[Index(nameof(Status), nameof(ScheduledAt), nameof(NextAttemptAt))]
public sealed class ReviewOutreach : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public int AppointmentId { get; set; }
    public int PatientId { get; set; }
    [MaxLength(40)] public string Campaign { get; set; } = "google-review";
    public ReviewOutreachChannel Channel { get; set; } = ReviewOutreachChannel.Email;
    public ReviewOutreachStatus Status { get; set; } = ReviewOutreachStatus.Scheduled;
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

[Index(nameof(TenantId), IsUnique = true)]
public sealed class ReviewOutreachSettings : ITenantEntity
{
    public int Id { get; set; }
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public int DelayMinutes { get; set; } = 240;
    [MaxLength(2048)] public string? ReviewLandingPageUrl { get; set; }
    [MaxLength(2048)] public string? GoogleReviewUrl { get; set; }
    [MaxLength(120)] public string SenderName { get; set; } = string.Empty;
}

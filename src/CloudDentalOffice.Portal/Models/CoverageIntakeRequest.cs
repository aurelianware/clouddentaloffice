namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// One request to a patient, by emailed link, for their dental coverage: sent when
/// an upcoming appointment has no coverage on file, reminded once before the
/// visit, and answered at most once through the public intake page. A typed plan
/// waits for staff to add it as coverage; "no dental insurance" makes the
/// patient's appointments self-pay. Times are UTC.
/// </summary>
public class CoverageIntakeRequest : ITenantEntity
{
    public Guid Id { get; set; }

    /// <summary>No default: always written for the practice that sent it.</summary>
    public string TenantId { get; set; } = string.Empty;

    public int PatientId { get; set; }

    /// <summary>The appointment row that prompted the request.</summary>
    public long CoverageVerificationId { get; set; }

    public string RecipientEmail { get; set; } = string.Empty;
    public CoverageIntakeStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public int SendAttempts { get; set; }
    public string? LastError { get; set; }

    public DateTime? AnsweredAt { get; set; }
    public CoverageIntakeAnswer? Answer { get; set; }

    // What the patient typed, for staff to add as coverage. Never echoed in reasons or logs.
    public string? CarrierName { get; set; }
    public string? MemberId { get; set; }
    public string? GroupNumber { get; set; }
    public string? RelationshipToSubscriber { get; set; }
    public string? SubscriberFirstName { get; set; }
    public string? SubscriberLastName { get; set; }
    public DateOnly? SubscriberDateOfBirth { get; set; }
}

public enum CoverageIntakeStatus
{
    /// <summary>Created; the email hasn't gone out yet (or will be retried).</summary>
    Pending,
    Sent,
    /// <summary>The email could not be delivered; staff need to ask the patient another way.</summary>
    Failed,
    Answered
}

public enum CoverageIntakeAnswer
{
    /// <summary>The patient typed in a dental plan.</summary>
    Plan,
    /// <summary>The patient says they have no dental insurance.</summary>
    NoInsurance
}

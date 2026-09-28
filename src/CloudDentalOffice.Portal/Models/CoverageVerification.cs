namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// Where one upcoming appointment stands on dental coverage. One row per
/// scheduled appointment, kept current by the coverage verification worker:
/// it checks the patient's primary coverage when the appointment first appears,
/// again before the visit, and retries when the payer can't be reached. The
/// individual checks are in <see cref="EligibilityVerification"/>. Times are
/// UTC <see cref="DateTime"/>s so they can be compared in queries on every provider.
/// </summary>
public class CoverageVerification : ITenantEntity
{
    public long Id { get; set; }

    /// <summary>No default: always written for the tenant whose schedule it came from.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The appointment's identifier in the SchedulingService.</summary>
    public Guid AppointmentId { get; set; }

    public int PatientId { get; set; }
    public int ProviderId { get; set; }
    public DateTime AppointmentStart { get; set; }

    /// <summary>The coverage last checked for this appointment; null when the patient has none on file.</summary>
    public int? PatientInsuranceId { get; set; }

    /// <summary>The latest answer for this appointment; null until the first check.</summary>
    public EligibilityVerificationState? State { get; set; }

    /// <summary>Staff-readable reason for any state other than Verified. Never echoes member IDs or names.</summary>
    public string? Reason { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Consecutive checks that ended Unavailable. Reset by any other answer.</summary>
    public int Attempts { get; set; }

    /// <summary>When an Unavailable check may be retried.</summary>
    public DateTime? NextCheckAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Set when the appointment is cancelled, completed, dropped from the schedule, or past.</summary>
    public DateTime? ClosedAt { get; set; }
}

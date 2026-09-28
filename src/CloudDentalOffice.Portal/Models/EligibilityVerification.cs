namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// One eligibility check against a patient's coverage, kept so staff can see when
/// coverage was last verified and what the payer said. Written for every check
/// that reached a saved coverage, including ones that failed. Holds the
/// normalized summary only, never the raw 271.
/// </summary>
public class EligibilityVerification : ITenantEntity
{
    public long Id { get; set; }

    /// <summary>No default: always written for the tenant the check ran under.</summary>
    public string TenantId { get; set; } = string.Empty;

    public int PatientInsuranceId { get; set; }
    public int PatientId { get; set; }

    public DateOnly ServiceDate { get; set; }
    public EligibilityVerificationState State { get; set; }

    /// <summary>Staff-readable reason for any state other than Verified. Never echoes member IDs or names.</summary>
    public string? Reason { get; set; }

    /// <summary>Matches the transaction audit record and the clearinghouse logs for this check.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Where the answer came from as shown to staff ("Clearinghouse"); null when no answer came back.</summary>
    public string? Source { get; set; }

    public DateTimeOffset CheckedAt { get; set; }

    /// <summary>Serialized <see cref="EligibilityResult"/> when the payer answered; null otherwise.</summary>
    public string? ResultJson { get; set; }

    public virtual PatientInsurance PatientInsurance { get; set; } = null!;
}

/// <summary>What a check means for the front desk.</summary>
public enum EligibilityVerificationState
{
    /// <summary>Coverage is active and includes dental care.</summary>
    Verified,

    /// <summary>The plan is active but reports dental care as not covered (often a medical card).</summary>
    NotDental,

    /// <summary>The payer reports coverage inactive for the service date.</summary>
    Inactive,

    /// <summary>The payer answered without confirming coverage either way.</summary>
    Unconfirmed,

    /// <summary>The coverage is missing information, or the payer rejected it; staff must correct it.</summary>
    NeedsInfo,

    /// <summary>The check could not be completed (timeout, clearinghouse unavailable, setup). Retry later.</summary>
    Unavailable
}

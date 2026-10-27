namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// What the front desk needs about a patient's primary dental coverage at a glance:
/// whose plan it is, how the last eligibility check came out, and the benefits it
/// reported. <see cref="Benefits"/> is set only when that last check verified
/// active dental coverage.
/// </summary>
public sealed record CoverageSnapshot(
    int PatientId,
    int PatientInsuranceId,
    string? PayerName,
    EligibilityVerificationState? State,
    DateTimeOffset? LastVerifiedAt,
    DentalBenefitSummary? Benefits);

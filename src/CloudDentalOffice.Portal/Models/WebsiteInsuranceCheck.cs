namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// A "Check My Insurance" request from the practice website. Staff match the
/// visitor to a patient (or create one), then email them the secure coverage link,
/// which collects the member ID and plan holder. The website collects only what is
/// here: no member IDs, dates of birth or clinical detail. Times are UTC.
/// </summary>
public class WebsiteInsuranceCheck : ITenantEntity
{
    /// <summary>The relayed event's ID, so a redelivered or resubmitted form is stored once.</summary>
    public Guid Id { get; set; }

    /// <summary>No default: always written for the practice whose website sent it.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string Carrier { get; set; } = string.Empty;
    public string? Plan { get; set; }

    /// <summary>Yes or No: whether the visitor also asked for an appointment.</summary>
    public string? RequestingAppointment { get; set; }
    public string? Source { get; set; }
    public string? Campaign { get; set; }
    public string? AttributionId { get; set; }

    public DateTime SubmittedAt { get; set; }
    public DateTime ReceivedAt { get; set; }

    public int? PatientId { get; set; }

    /// <summary>The secure link emailed to the patient for this check.</summary>
    public Guid? CoverageIntakeRequestId { get; set; }

    /// <summary>Set when staff finish with it (coverage added, or nothing to do).</summary>
    public DateTime? ClosedAt { get; set; }
    public string? ClosedBy { get; set; }
}

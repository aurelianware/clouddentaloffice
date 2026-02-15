namespace CloudDentalOffice.Contracts.Claims;

public record ClaimDto
{
    public Guid Id { get; init; }
    public Guid PatientId { get; init; }
    public Guid ProviderId { get; init; }
    public string PayerId { get; init; } = string.Empty;
    public string? PayerName { get; init; }
    public string SubscriberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }
    public ClaimStatus Status { get; init; }
    public DateOnly ServiceDate { get; init; }
    public decimal TotalCharge { get; init; }
    public decimal? PaidAmount { get; init; }
    public string? ClaimControlNumber { get; init; }
    public List<ClaimLineDto> Lines { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime? SubmittedAt { get; init; }
    public DateTime? AdjudicatedAt { get; init; }
}

public record ClaimLineDto
{
    public int LineNumber { get; init; }
    public string CdtCode { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? ToothNumber { get; init; }
    public string? Surface { get; init; }
    public string? Area { get; init; }
    public decimal Charge { get; init; }
    public decimal? PaidAmount { get; init; }
}

public record CreateClaimRequest
{
    public Guid PatientId { get; init; }
    public Guid ProviderId { get; init; }
    public string PayerId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }
    public DateOnly ServiceDate { get; init; }
    public List<CreateClaimLineRequest> Lines { get; init; } = [];
}

public record CreateClaimLineRequest
{
    public string CdtCode { get; init; } = string.Empty;
    public string? ToothNumber { get; init; }
    public string? Surface { get; init; }
    public string? Area { get; init; }
    public decimal Charge { get; init; }
}

public record SubmitClaimRequest
{
    public Guid ClaimId { get; init; }
    public string SubmissionMethod { get; init; } = "EDI"; // EDI, Portal, Paper
}

public enum ClaimStatus
{
    Draft,
    Validated,
    Submitted,
    Acknowledged,
    Pending,
    Adjudicated,
    Paid,
    Denied,
    Rejected,
    Voided
}

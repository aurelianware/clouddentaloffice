namespace CloudDentalOffice.Contracts.Era;

public record EraFileDto
{
    public Guid Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string PayerId { get; init; } = string.Empty;
    public string? PayerName { get; init; }
    public string? CheckNumber { get; init; }
    public decimal TotalPayment { get; init; }
    public DateOnly PaymentDate { get; init; }
    public int ClaimCount { get; init; }
    public EraProcessingStatus ProcessingStatus { get; init; }
    public List<EraClaimDto> Claims { get; init; } = [];
    public DateTime ReceivedAt { get; init; }
    public DateTime? PostedAt { get; init; }
}

public record EraClaimDto
{
    public string PatientControlNumber { get; init; } = string.Empty;
    public string? ClaimControlNumber { get; init; }
    public string ClaimStatus { get; init; } = string.Empty; // Processed, Denied, Reversed
    public decimal ChargedAmount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal AdjustmentAmount { get; init; }
    public decimal PatientResponsibility { get; init; }
    public Guid? MatchedClaimId { get; init; }
    public List<EraAdjustmentDto> Adjustments { get; init; } = [];
}

public record EraAdjustmentDto
{
    public string GroupCode { get; init; } = string.Empty; // CO, PR, OA, PI, CR
    public string ReasonCode { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public string? Description { get; init; }
}

public enum EraProcessingStatus
{
    Received,
    Parsed,
    Matched,
    ReviewRequired,
    Posted,
    PartiallyPosted,
    Failed
}

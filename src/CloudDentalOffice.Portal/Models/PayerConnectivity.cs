namespace CloudDentalOffice.Portal.Models;

[Flags]
public enum TradingPartnerCapability
{
    None = 0,
    Eligibility = 1,
    PaymentEstimate = 2,
    ClaimSubmission = 4,
    ClaimStatus = 8,
    Remittance = 16,
    Predetermination = 32,
    AdvancedEob = 64
}

public enum CoverageStatus { Active, Inactive, Unknown }

public sealed record NormalizedEligibilityRequest
{
    public required string TenantId { get; init; }
    public required string PayerId { get; init; }
    public required string MemberId { get; init; }
    public string? SubscriberId { get; init; }
    public required string ProviderNpi { get; init; }
    public DateOnly ServiceDate { get; init; }
    public IReadOnlyList<string> ServiceTypeCodes { get; init; } = ["35"];
}

public sealed record EligibilityResult
{
    public required string CorrelationId { get; init; }
    public CoverageStatus CoverageStatus { get; init; }
    public string? PlanName { get; init; }
    public string? PlanType { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public DateOnly? TerminationDate { get; init; }
    public decimal? Deductible { get; init; }
    public decimal? DeductibleRemaining { get; init; }
    public decimal? AnnualMaximum { get; init; }
    public decimal? AnnualMaximumRemaining { get; init; }
    public decimal? Copay { get; init; }
    public decimal? Coinsurance { get; init; }
    public IReadOnlyList<EligibilityBenefit> Benefits { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
    public required string Source { get; init; }
    public DateTimeOffset VerifiedAt { get; init; }
    public string? ExternalTransactionId { get; init; }
}

public sealed record EligibilityBenefit(
    string ServiceTypeCode, string Description, CoverageStatus Status,
    decimal? Copay = null, decimal? Coinsurance = null, decimal? Limit = null, decimal? Remaining = null);

public sealed record PayerEstimateRoutingRequest
{
    public required string PayerId { get; init; }
    public required TreatmentEstimateRequest EstimateRequest { get; init; }
}

public sealed record RoutedTreatmentEstimate(TreatmentEstimateResult Result, string AdapterType, int Priority);

public sealed record TransactionAuditRecord
{
    public required string CorrelationId { get; init; }
    public required string TenantId { get; init; }
    public required string PayerId { get; init; }
    public required string AdapterType { get; init; }
    public required string TransactionType { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset RespondedAt { get; init; }
    public bool Succeeded { get; init; }
    public string? ResponseStatus { get; init; }
    public long ElapsedMilliseconds { get; init; }
}

namespace CloudDentalOffice.Contracts.Eligibility;

public record EligibilityRequest
{
    public Guid PatientId { get; init; }
    public string PayerId { get; init; } = string.Empty;
    public string SubscriberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }
    public string PatientFirstName { get; init; } = string.Empty;
    public string PatientLastName { get; init; } = string.Empty;
    public DateOnly PatientDob { get; init; }
    public DateOnly? ServiceDate { get; init; }
    public string? ServiceTypeCode { get; init; } // 35 = Dental
    public string? ProviderId { get; init; }
}

public record EligibilityResponse
{
    public Guid RequestId { get; init; }
    public EligibilityStatus Status { get; init; }
    public string? PlanName { get; init; }
    public string? PayerName { get; init; }
    public DateOnly? CoverageEffective { get; init; }
    public DateOnly? CoverageTermination { get; init; }
    public List<BenefitInfo> Benefits { get; init; } = [];
    public string? RawX12Response { get; init; }
    public DateTime CheckedAt { get; init; }
}

public record BenefitInfo
{
    public string ServiceType { get; init; } = string.Empty;
    public string BenefitType { get; init; } = string.Empty; // ActiveCoverage, Deductible, CoInsurance, Copay, Limitation
    public string? CoverageLevel { get; init; } // Individual, Family
    public decimal? Amount { get; init; }
    public decimal? Percentage { get; init; }
    public string? TimePeriod { get; init; } // CalendarYear, Lifetime, ServiceYear
    public string? InNetworkIndicator { get; init; }
    public string? Description { get; init; }
}

public enum EligibilityStatus
{
    Active,
    Inactive,
    Pending,
    Unknown,
    Error
}

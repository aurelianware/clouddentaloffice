namespace CloudDentalOffice.Portal.Models;

public enum EstimateStatus { Completed, Partial }
public enum EstimateAuthority { CloudHealthOfficeEstimate, PayerEstimate, PayerAdjudication }
public enum EstimateConfidence { High, Medium, Low, InsufficientData }

public sealed record TreatmentEstimateRequest
{
    public required string TenantId { get; init; }
    public required string TreatmentPlanId { get; init; }
    public required string PatientId { get; init; }
    public required string MemberId { get; init; }
    public string? GroupNumber { get; init; }
    public required Guid BenefitPlanId { get; init; }
    public required string RenderingProviderNpi { get; init; }
    public required DateOnly ServiceDate { get; init; }
    public IReadOnlyList<TreatmentEstimateRequestLine> Lines { get; init; } = [];
}

public sealed record TreatmentEstimateRequestLine
{
    public required string LineId { get; init; }
    public required int LineNumber { get; init; }
    public required string ProcedureCode { get; init; }
    public required decimal ChargeAmount { get; init; }
    public int Units { get; init; } = 1;
    public string? ToothNumber { get; init; }
    public string? Surface { get; init; }
}

public sealed record TreatmentEstimateResult
{
    public EstimateStatus Status { get; init; }
    public EstimateAuthority Authority { get; init; }
    public EstimateConfidence Confidence { get; init; }
    public decimal TotalCharges { get; init; }
    public decimal EstimatedAllowed { get; init; }
    public decimal EstimatedInsurancePayment { get; init; }
    public decimal EstimatedPatientResponsibility { get; init; }
    public decimal EstimatedContractAdjustment { get; init; }
    public IReadOnlyList<TreatmentEstimateLine> Lines { get; init; } = [];
    public IReadOnlyList<EstimateWarning> Warnings { get; init; } = [];
    public string Disclaimer { get; init; } = TreatmentEstimateDefaults.Disclaimer;
}

public sealed record TreatmentEstimateLine
{
    public required string LineId { get; init; }
    public int LineNumber { get; init; }
    public required string ProcedureCode { get; init; }
    public string? Description { get; init; }
    public decimal ChargeAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal InsurancePayment { get; init; }
    public decimal PatientResponsibility { get; init; }
    public decimal ContractAdjustment { get; init; }
    public decimal Deductible { get; init; }
    public decimal? BenefitPercentage { get; init; }
    public string Status { get; init; } = "Estimated";
    public string PriorAuthorization { get; init; } = "Not indicated";
    public IReadOnlyList<string> Explanations { get; init; } = [];
}

public sealed record EstimateWarning(string Code, string Message);

public static class TreatmentEstimateDefaults
{
    public const string Disclaimer = "This is an estimate, not a guarantee of payment. Final benefits and payment may change before claim adjudication.";
}

public sealed class TreatmentEstimateValidationException(string message) : Exception(message);
public sealed class TreatmentEstimateUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

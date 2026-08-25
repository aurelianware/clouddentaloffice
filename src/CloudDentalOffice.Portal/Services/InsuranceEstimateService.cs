using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public interface IInsuranceEstimateService
{
    Task<TreatmentEstimateResult> EstimateAsync(TreatmentEstimateRequest request, CancellationToken cancellationToken = default);
}

public sealed class CloudHealthOfficeOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string EstimatePath { get; set; } = "/api/v1/adjudication/estimate";
    public string IntelligencePath { get; set; } = "/api/claims/{claimId}/intelligence";
    /// <summary>Optional host for claim intelligence when it is not served from BaseUrl.</summary>
    public string? IntelligenceBaseUrl { get; set; }
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public Dictionary<string, Guid> BenefitPlanMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class TreatmentEstimateMapper
{
    public static TreatmentEstimateRequest Map(
        TreatmentPlan plan, Patient patient, PatientInsurance? insurance, Provider? provider,
        DateOnly serviceDate, string tenantId, IReadOnlyDictionary<string, Guid> benefitPlanMappings)
    {
        if (insurance is null) throw new TreatmentEstimateValidationException("This patient does not have active insurance selected.");
        if (string.IsNullOrWhiteSpace(insurance.MemberId)) throw new TreatmentEstimateValidationException("We can’t calculate an insurance estimate because this patient does not have a member ID on file.");
        if (insurance.InsurancePlan is null || string.IsNullOrWhiteSpace(insurance.InsurancePlan.PayerId)) throw new TreatmentEstimateValidationException("The selected insurance plan is not mapped to a payer.");
        if (!benefitPlanMappings.TryGetValue(insurance.InsurancePlan.PayerId, out var benefitPlanId) || benefitPlanId == Guid.Empty)
            throw new TreatmentEstimateValidationException("The selected payer is not mapped to a CloudHealthOffice benefit plan.");
        if (provider is null || string.IsNullOrWhiteSpace(provider.NPI)) throw new TreatmentEstimateValidationException("Select a rendering provider with an NPI before estimating insurance.");
        if (plan.PlannedProcedures.Count == 0) throw new TreatmentEstimateValidationException("Add at least one procedure before estimating insurance.");
        if (plan.PatientId != patient.PatientId) throw new TreatmentEstimateValidationException("The treatment plan does not belong to the selected patient.");

        var lines = plan.PlannedProcedures.Select((procedure, index) =>
        {
            if (string.IsNullOrWhiteSpace(procedure.CDTCode))
                throw new TreatmentEstimateValidationException($"Procedure line {index + 1} needs a CDT code.");

            return new TreatmentEstimateRequestLine
            {
                LineId = procedure.PlannedProcedureId > 0 ? $"planned-{procedure.PlannedProcedureId}" : $"draft-{index + 1}",
                LineNumber = index + 1,
                ProcedureCode = procedure.CDTCode.Trim(),
                ChargeAmount = procedure.EstimatedFee,
                Units = 1,
                ToothNumber = procedure.ToothNumber,
                Surface = procedure.Surface
            };
        }).ToList();

        return new TreatmentEstimateRequest
        {
            TenantId = tenantId,
            TreatmentPlanId = plan.TreatmentPlanId > 0 ? plan.TreatmentPlanId.ToString() : "draft",
            PatientId = patient.PatientId.ToString(),
            MemberId = insurance.MemberId.Trim(),
            GroupNumber = insurance.GroupNumber,
            BenefitPlanId = benefitPlanId,
            RenderingProviderNpi = provider.NPI.Trim(),
            ServiceDate = serviceDate,
            Lines = lines
        };
    }
}

public sealed class CloudHealthOfficeInsuranceEstimateService : IInsuranceEstimateService
{
    private readonly HttpClient _httpClient;
    private readonly CloudHealthOfficeOptions _options;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<CloudHealthOfficeInsuranceEstimateService> _logger;

    public CloudHealthOfficeInsuranceEstimateService(HttpClient httpClient, IOptions<CloudHealthOfficeOptions> options,
        ITenantProvider tenantProvider, ILogger<CloudHealthOfficeInsuranceEstimateService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public async Task<TreatmentEstimateResult> EstimateAsync(TreatmentEstimateRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new TreatmentEstimateUnavailableException("Insurance estimates are not configured for this environment.");
        if (!Uri.TryCreate(_options.EstimatePath, UriKind.Relative, out _) || _options.EstimatePath.StartsWith("//", StringComparison.Ordinal))
            throw new TreatmentEstimateUnavailableException("Insurance estimates are misconfigured: the estimate path must be a relative URI.");
        if (string.IsNullOrWhiteSpace(_tenantProvider.TenantId) || request.TenantId != _tenantProvider.TenantId)
            throw new UnauthorizedAccessException("The estimate request is outside the active tenant.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), _options.EstimatePath));
        message.Headers.Add("X-Tenant-Id", request.TenantId);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) message.Headers.Add("X-Api-Key", _options.ApiKey);
        message.Content = JsonContent.Create(ToWireRequest(request));

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                throw new TreatmentEstimateValidationException("CloudHealthOffice could not estimate this plan. Review the insurance, provider, and procedure information.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Insurance estimate service returned HTTP {StatusCode} for tenant {TenantId}", (int)response.StatusCode, _tenantProvider.TenantId);
                throw new TreatmentEstimateUnavailableException("CloudHealthOffice is temporarily unavailable. Try the estimate again later.");
            }

            var result = await response.Content.ReadFromJsonAsync<CloudHealthOfficeEstimateResponse>(cancellationToken: cancellationToken);
            return result is null
                ? throw new TreatmentEstimateUnavailableException("CloudHealthOffice returned an incomplete estimate.")
                : Normalize(request, result);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TreatmentEstimateUnavailableException("The insurance estimate timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Insurance estimate service could not be reached for tenant {TenantId}", _tenantProvider.TenantId);
            throw new TreatmentEstimateUnavailableException("CloudHealthOffice is temporarily unavailable. Try the estimate again later.", ex);
        }
    }

    private static CloudHealthOfficeEstimateRequest ToWireRequest(TreatmentEstimateRequest request) => new()
    {
        RequestId = request.TreatmentPlanId,
        MemberId = request.MemberId,
        SubscriberId = request.MemberId,
        BenefitPlanId = request.BenefitPlanId,
        ProviderNpi = request.RenderingProviderNpi,
        ServiceDate = request.ServiceDate,
        ClaimType = "Dental",
        LineOfBusiness = "Dental",
        Lines = request.Lines.Select(line => new CloudHealthOfficeEstimateLineRequest
        {
            LineNumber = line.LineNumber,
            ProcedureCode = line.ProcedureCode,
            CodeType = "CDT",
            ChargeAmount = line.ChargeAmount,
            Units = line.Units,
            ToothNumber = line.ToothNumber,
            ToothSurface = line.Surface
        }).ToList()
    };

    private static TreatmentEstimateResult Normalize(TreatmentEstimateRequest request, CloudHealthOfficeEstimateResponse response)
    {
        var requestLines = request.Lines.ToDictionary(x => x.LineNumber);
        var normalizedLines = response.Lines.Select(line =>
        {
            if (!requestLines.TryGetValue(line.LineNumber, out var source))
                throw new TreatmentEstimateUnavailableException($"CloudHealthOffice returned an unknown estimate line {line.LineNumber}.");
            var coinsuranceBase = line.AllowedAmount - line.DeductibleAmount - line.CopayAmount;
            return new TreatmentEstimateLine
            {
                LineId = source.LineId,
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
                ChargeAmount = line.BilledAmount,
                AllowedAmount = line.AllowedAmount,
                InsurancePayment = line.PayerResponsibility,
                PatientResponsibility = line.PatientResponsibility,
                ContractAdjustment = line.ContractualAdjustment,
                Deductible = line.DeductibleAmount,
                BenefitPercentage = coinsuranceBase > 0 ? line.PayerResponsibility / coinsuranceBase : null,
                Status = line.Status,
                PriorAuthorization = line.Messages.Any(x => x.Code == "PRIOR_AUTH_REQUIRED") ? "Required or needs review" : "Not indicated",
                Explanations = line.Messages.Select(x => x.Description).ToList()
            };
        }).ToList();

        return new TreatmentEstimateResult
        {
            Status = response.Status.Equals("insufficient_data", StringComparison.OrdinalIgnoreCase) ? EstimateStatus.Partial : EstimateStatus.Completed,
            Authority = response.Authority switch
            {
                CloudHealthOfficeEstimateAuthority.AuthoritativePayer => EstimateAuthority.PayerAdjudication,
                CloudHealthOfficeEstimateAuthority.PayerEstimate => EstimateAuthority.PayerEstimate,
                _ => EstimateAuthority.CloudHealthOfficeEstimate
            },
            Confidence = response.Confidence.Level switch
            {
                CloudHealthOfficeConfidenceLevel.Medium => EstimateConfidence.Medium,
                CloudHealthOfficeConfidenceLevel.Low => EstimateConfidence.Low,
                CloudHealthOfficeConfidenceLevel.InsufficientData => EstimateConfidence.InsufficientData,
                _ => EstimateConfidence.High
            },
            TotalCharges = response.Totals.BilledAmount,
            EstimatedAllowed = response.Totals.AllowedAmount,
            EstimatedInsurancePayment = response.Totals.PayerResponsibility,
            EstimatedPatientResponsibility = response.Totals.PatientResponsibility,
            EstimatedContractAdjustment = response.Totals.ContractualAdjustment,
            Lines = normalizedLines,
            Warnings = response.Warnings.Select(x => new EstimateWarning(x.Code, x.Description)).ToList(),
            Disclaimer = string.IsNullOrWhiteSpace(response.Disclaimer) ? TreatmentEstimateDefaults.Disclaimer : response.Disclaimer
        };
    }
}

internal sealed record CloudHealthOfficeEstimateRequest
{
    public string? RequestId { get; init; }
    public required string MemberId { get; init; }
    public string? SubscriberId { get; init; }
    public Guid BenefitPlanId { get; init; }
    public required string ProviderNpi { get; init; }
    public DateOnly ServiceDate { get; init; }
    public string ClaimType { get; init; } = "Dental";
    public string? LineOfBusiness { get; init; }
    public List<CloudHealthOfficeEstimateLineRequest> Lines { get; init; } = [];
}

internal sealed record CloudHealthOfficeEstimateLineRequest
{
    public int LineNumber { get; init; }
    public required string ProcedureCode { get; init; }
    public string CodeType { get; init; } = "CDT";
    public decimal ChargeAmount { get; init; }
    public decimal Units { get; init; } = 1;
    public string? ToothNumber { get; init; }
    public string? ToothSurface { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum CloudHealthOfficeEstimateAuthority { Simulation, PayerEstimate, AuthoritativePayer }
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum CloudHealthOfficeConfidenceLevel { High, Medium, Low, InsufficientData }
internal sealed record CloudHealthOfficeEstimateResponse
{
    public string Status { get; init; } = "estimated";
    public CloudHealthOfficeEstimateAuthority Authority { get; init; }
    public CloudHealthOfficeEstimateTotals Totals { get; init; } = new();
    public List<CloudHealthOfficeEstimateLine> Lines { get; init; } = [];
    public List<CloudHealthOfficeEstimateMessage> Warnings { get; init; } = [];
    public CloudHealthOfficeEstimateConfidence Confidence { get; init; } = new();
    public string? Disclaimer { get; init; }
}
internal sealed record CloudHealthOfficeEstimateTotals
{
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal PayerResponsibility { get; init; }
    public decimal PatientResponsibility { get; init; }
}
internal sealed record CloudHealthOfficeEstimateLine
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = string.Empty;
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal PayerResponsibility { get; init; }
    public decimal PatientResponsibility { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public string Status { get; init; } = "payable";
    public List<CloudHealthOfficeEstimateMessage> Messages { get; init; } = [];
}
internal sealed record CloudHealthOfficeEstimateMessage { public string Code { get; init; } = string.Empty; public string Description { get; init; } = string.Empty; }
internal sealed record CloudHealthOfficeEstimateConfidence { public CloudHealthOfficeConfidenceLevel Level { get; init; } public List<string> Reasons { get; init; } = []; public List<string> MissingData { get; init; } = []; }

public static class TreatmentEstimateDisplay
{
    public static string Authority(EstimateAuthority value) => value switch
    {
        EstimateAuthority.PayerAdjudication => "Payer Adjudication",
        EstimateAuthority.PayerEstimate => "Payer Estimate",
        _ => "CloudHealthOffice Estimate"
    };

    public static string Confidence(EstimateConfidence value) => $"{value} confidence";
}

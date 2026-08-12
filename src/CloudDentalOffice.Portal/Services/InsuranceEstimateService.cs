using System.Net;
using System.Net.Http.Json;
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
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
}

public static class TreatmentEstimateMapper
{
    public static TreatmentEstimateRequest Map(
        TreatmentPlan plan, Patient patient, PatientInsurance? insurance, Provider? provider,
        DateOnly serviceDate, string tenantId)
    {
        if (insurance is null) throw new TreatmentEstimateValidationException("This patient does not have active insurance selected.");
        if (string.IsNullOrWhiteSpace(insurance.MemberId)) throw new TreatmentEstimateValidationException("We can’t calculate an insurance estimate because this patient does not have a member ID on file.");
        if (insurance.InsurancePlan is null || string.IsNullOrWhiteSpace(insurance.InsurancePlan.PayerId)) throw new TreatmentEstimateValidationException("The selected insurance plan is not mapped to a payer ID.");
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
            PayerId = insurance.InsurancePlan.PayerId.Trim(),
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
        if (string.IsNullOrWhiteSpace(_tenantProvider.TenantId) || request.TenantId != _tenantProvider.TenantId)
            throw new UnauthorizedAccessException("The estimate request is outside the active tenant.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), _options.EstimatePath));
        message.Headers.Add("X-Tenant-Id", request.TenantId);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey)) message.Headers.Add("X-Api-Key", _options.ApiKey);
        message.Content = JsonContent.Create(request);

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                throw new TreatmentEstimateValidationException("CloudHealthOffice could not estimate this plan. Review the insurance, provider, and procedure information.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Insurance estimate service returned HTTP {StatusCode} for tenant {TenantId}", (int)response.StatusCode, request.TenantId);
                throw new TreatmentEstimateUnavailableException("CloudHealthOffice is temporarily unavailable. Try the estimate again later.");
            }

            var result = await response.Content.ReadFromJsonAsync<TreatmentEstimateResult>(cancellationToken: cancellationToken);
            return result ?? throw new TreatmentEstimateUnavailableException("CloudHealthOffice returned an incomplete estimate.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TreatmentEstimateUnavailableException("The insurance estimate timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Insurance estimate service could not be reached for tenant {TenantId}", request.TenantId);
            throw new TreatmentEstimateUnavailableException("CloudHealthOffice is temporarily unavailable. Try the estimate again later.", ex);
        }
    }
}

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

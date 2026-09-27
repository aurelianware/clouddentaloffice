using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Models;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services.Stedi;

/// <summary>Per-request tenant stamp read by <see cref="StediCredentialHandler"/>.</summary>
public static class StediRequest
{
    public static readonly HttpRequestOptionsKey<string> Tenant = new("cdo.stedi.tenant");
}

/// <summary>
/// Attaches the calling practice's Stedi key to each request. The tenant comes
/// only from the request's <see cref="StediRequest.Tenant"/> option — never
/// from ambient state — and a request without one is refused before it leaves
/// the process. Any Authorization header already present is replaced.
/// </summary>
public sealed class StediCredentialHandler(IStediCredentialProvider credentials) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(StediRequest.Tenant, out var tenantId) || string.IsNullOrWhiteSpace(tenantId))
            throw new StediCredentialUnavailableException(string.Empty, StediCredentialFailure.MissingTenant,
                "Stedi request has no tenant; refusing to send it without that practice's credential.");

        var credential = await credentials.GetAsync(tenantId, cancellationToken);
        request.Headers.Remove("Authorization");
        // Stedi authenticates with the raw API key in the Authorization header.
        request.Headers.TryAddWithoutValidation("Authorization", credential.ApiKey);
        return await base.SendAsync(request, cancellationToken);
    }
}

public interface IStediEligibilityClient
{
    Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Direct 270/271 to Stedi with the practice's own key. Produces the same
/// <see cref="EligibilityResult"/> as the CloudHealthOffice path by normalizing
/// Stedi's response into the shared benefit summary. Logs tenant, payer, status
/// and correlation ID only.
/// </summary>
public sealed class StediEligibilityClient(
    HttpClient httpClient, IOptions<StediOptions> options, ILogger<StediEligibilityClient> logger) : IStediEligibilityClient
{
    // Shown to staff ("via …"); vendor names never appear in the portal.
    internal const string SourceName = "Clearinghouse";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            throw new TreatmentEstimateUnavailableException(StaffMessage(StediCredentialFailure.MissingTenant),
                new StediCredentialUnavailableException(string.Empty, StediCredentialFailure.MissingTenant,
                    "Eligibility request has no tenant."));
        // The practice's key rides on this request, so it may only go to the
        // configured HTTPS origin: an absolute or protocol-relative path would
        // replace the base URL.
        var target = ResolveTarget(options.Value);
        if (target is null)
            throw new TreatmentEstimateUnavailableException("Eligibility checks are misconfigured. Contact support.");

        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");
        using var message = new HttpRequestMessage(HttpMethod.Post, target);
        message.Options.Set(StediRequest.Tenant, request.TenantId);
        message.Content = JsonContent.Create(StediEligibilityWire.ToRequest(request, correlationId), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (StediCredentialUnavailableException ex)
        {
            throw new TreatmentEstimateUnavailableException(StaffMessage(ex.Failure), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Stedi eligibility check {CorrelationId} for tenant {TenantId} timed out",
                ClaimLifecycleMapper.SanitizeForLog(correlationId), ClaimLifecycleMapper.SanitizeForLog(request.TenantId));
            throw new TreatmentEstimateUnavailableException("The eligibility check timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Stedi could not be reached for check {CorrelationId} tenant {TenantId} ({ErrorType})",
                ClaimLifecycleMapper.SanitizeForLog(correlationId), ClaimLifecycleMapper.SanitizeForLog(request.TenantId), ex.GetType().Name);
            throw new TreatmentEstimateUnavailableException("Eligibility checks are temporarily unavailable. Try again in a minute.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            logger.LogInformation("Stedi eligibility check {CorrelationId} for tenant {TenantId} payer {PayerId} returned HTTP {StatusCode}",
                ClaimLifecycleMapper.SanitizeForLog(correlationId), ClaimLifecycleMapper.SanitizeForLog(request.TenantId), ClaimLifecycleMapper.SanitizeForLog(request.PayerId), status);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await ReadAsync(response, cancellationToken)
                        ?? throw new TreatmentEstimateUnavailableException("The eligibility response was incomplete. Try again.");
                    return CloudHealthOfficeEligibilityMapper.ToResult(
                        StediEligibilityWire.ToSummary(body, correlationId), correlationId, SourceName);
                }
                case HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity:
                    throw new TreatmentEstimateValidationException(
                        "The payer or clearinghouse rejected the request. Check the payer ID, member ID, names and dates of birth on the coverage.");
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    logger.LogError("Stedi refused the credential for tenant {TenantId} (HTTP {StatusCode})",
                        ClaimLifecycleMapper.SanitizeForLog(request.TenantId), status);
                    throw new TreatmentEstimateUnavailableException(
                        "This practice's Stedi account did not accept our credentials. Contact support.");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout:
                    throw new TreatmentEstimateUnavailableException("Eligibility checks are temporarily unavailable. Try again in a minute.");
                default:
                    throw new TreatmentEstimateUnavailableException(
                        "The eligibility check could not be completed. This is not a problem with the patient's information; try again later or contact support.");
            }
        }
    }

    internal static Uri? ResolveTarget(StediOptions options)
    {
        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo))
            return null;
        var path = options.EligibilityPath;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') || !Uri.TryCreate(path, UriKind.Relative, out _))
            return null;
        var target = new Uri(baseUri, path);
        return target.Scheme == Uri.UriSchemeHttps &&
               string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) &&
               target.Port == baseUri.Port
            ? target
            : null;
    }

    internal static string StaffMessage(StediCredentialFailure failure) => failure switch
    {
        StediCredentialFailure.NotConnected or StediCredentialFailure.SecretNotFound =>
            "This practice's Stedi account isn't connected yet. Contact support to finish setup.",
        StediCredentialFailure.NotActive => "This practice's Stedi connection is not active. Contact support.",
        StediCredentialFailure.KeyVaultUnavailable => "Eligibility checks are temporarily unavailable. Try again in a minute.",
        _ => "Eligibility checks are not configured correctly for this practice. Contact support."
    };

    private static async Task<StediEligibilityResponse?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<StediEligibilityResponse>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Stedi eligibility v3 JSON, and its projection onto the shared benefit summary.</summary>
internal static class StediEligibilityWire
{
    public static StediEligibilityRequest ToRequest(NormalizedEligibilityRequest r, string correlationId) => new()
    {
        TradingPartnerServiceId = r.PayerId,
        ExternalPatientId = correlationId,
        Provider = new StediProvider { Npi = r.ProviderNpi },
        Subscriber = new StediPerson
        {
            MemberId = r.MemberId,
            FirstName = r.SubscriberFirstName,
            LastName = r.SubscriberLastName,
            DateOfBirth = Date(r.SubscriberDateOfBirth),
            GroupNumber = r.GroupNumber
        },
        Dependents = r.Dependent is { } d
            ? [new StediPerson { MemberId = d.MemberId, FirstName = d.FirstName, LastName = d.LastName, DateOfBirth = Date(d.DateOfBirth) }]
            : null,
        Encounter = new StediEncounter
        {
            ServiceTypeCodes = r.ServiceTypeCodes.Count > 0 ? r.ServiceTypeCodes.ToList() : ["35"],
            DateOfService = Date(r.ServiceDate)
        }
    };

    public static ChoEligibilityResponse ToSummary(StediEligibilityResponse s, string correlationId)
    {
        if (s.Errors is { Count: > 0 })
        {
            var reasons = s.Errors.Select(e => e.Description).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            return new ChoEligibilityResponse
            {
                Outcome = "Rejected",
                CoverageStatus = nameof(CoverageStatus.Unknown),
                Message = reasons.Count > 0 ? string.Join("; ", reasons) : "Payer rejected the eligibility inquiry.",
                CorrelationId = correlationId,
                CheckedAtUtc = DateTimeOffset.UtcNow
            };
        }

        var benefits = s.BenefitsInformation ?? [];
        var status = CoverageFrom(s.PlanStatus, benefits);
        return new ChoEligibilityResponse
        {
            Outcome = "Completed",
            CoverageStatus = status.ToString(),
            Eligible = status == CoverageStatus.Active,
            Message = status == CoverageStatus.Inactive ? "Payer reports coverage is inactive for the service date." : null,
            PlanName = s.PlanInformation?.GroupDescription
                ?? s.PlanStatus?.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PlanDetails))?.PlanDetails,
            PlanId = s.PlanInformation?.PlanNumber,
            GroupNumber = s.PlanInformation?.GroupNumber,
            CoverageStart = ParseDate(s.PlanDateInformation?.EligibilityBegin) ?? ParseDate(s.PlanDateInformation?.PlanBegin),
            CoverageEnd = ParseDate(s.PlanDateInformation?.EligibilityEnd) ?? ParseDate(s.PlanDateInformation?.PlanEnd),
            Benefits = benefits.Select(ToBenefit).ToList(),
            CorrelationId = correlationId,
            CheckedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static ChoBenefit ToBenefit(StediBenefit b)
    {
        var amount = ParseDecimal(b.BenefitAmount);
        var percent = ParseDecimal(b.BenefitPercent) is { } p && p > 1m ? p / 100m : ParseDecimal(b.BenefitPercent);
        return new ChoBenefit
        {
            BenefitCode = b.Code,
            ServiceTypeCode = b.ServiceTypeCodes?.FirstOrDefault(),
            ServiceTypeName = b.Name,
            CoverageLevel = b.CoverageLevelCode,
            // In network only when the payer says so (Y) or omits the indicator.
            InNetwork = string.IsNullOrWhiteSpace(b.InPlanNetworkIndicatorCode) ||
                        string.Equals(b.InPlanNetworkIndicatorCode, "Y", StringComparison.OrdinalIgnoreCase),
            TimePeriod = string.IsNullOrWhiteSpace(b.TimeQualifier) ? b.TimeQualifierCode : b.TimeQualifier,
            Amount = amount,
            Percent = percent,
            CopayAmount = string.Equals(b.Code, "B", StringComparison.OrdinalIgnoreCase) ? amount : null,
            CoinsurancePercent = string.Equals(b.Code, "A", StringComparison.OrdinalIgnoreCase) ? percent : null,
            Messages = b.AdditionalInformation?.Select(a => a.Description).Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d!).ToList()
        };
    }

    // Active EB01 is "1"; inactive "6". Prefer plan status, then benefit lines.
    private static CoverageStatus CoverageFrom(List<StediPlanStatus>? planStatus, List<StediBenefit> benefits)
    {
        var active = planStatus?.Any(p => p.StatusCode == "1") == true || benefits.Any(b => b.Code == "1");
        if (active) return CoverageStatus.Active;
        var inactive = planStatus?.Any(p => p.StatusCode is "6" or "7" or "8") == true || benefits.Any(b => b.Code is "6" or "I");
        return inactive ? CoverageStatus.Inactive : CoverageStatus.Unknown;
    }

    private static string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
}

internal sealed class StediEligibilityRequest
{
    public string TradingPartnerServiceId { get; set; } = string.Empty;
    public string? ExternalPatientId { get; set; }
    public StediProvider Provider { get; set; } = new();
    public StediPerson Subscriber { get; set; } = new();
    public List<StediPerson>? Dependents { get; set; }
    public StediEncounter? Encounter { get; set; }
}

internal sealed class StediProvider
{
    public string? Npi { get; set; }
    public string? OrganizationName { get; set; }
}

internal sealed class StediPerson
{
    public string? MemberId { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? DateOfBirth { get; set; }
    public string? GroupNumber { get; set; }
}

internal sealed class StediEncounter
{
    public List<string>? ServiceTypeCodes { get; set; }
    public string? DateOfService { get; set; }
}

internal sealed class StediEligibilityResponse
{
    public StediPlanInformation? PlanInformation { get; set; }
    public List<StediPlanStatus>? PlanStatus { get; set; }
    public List<StediBenefit>? BenefitsInformation { get; set; }
    public StediPlanDates? PlanDateInformation { get; set; }
    public List<StediError>? Errors { get; set; }
}

internal sealed class StediPlanInformation
{
    public string? GroupNumber { get; set; }
    public string? GroupDescription { get; set; }
    public string? PlanNumber { get; set; }
}

internal sealed class StediPlanStatus
{
    public string? StatusCode { get; set; }
    public string? Status { get; set; }
    public string? PlanDetails { get; set; }
}

internal sealed class StediBenefit
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public List<string>? ServiceTypeCodes { get; set; }
    public string? CoverageLevelCode { get; set; }
    public string? TimeQualifierCode { get; set; }
    public string? TimeQualifier { get; set; }
    public string? BenefitAmount { get; set; }
    public string? BenefitPercent { get; set; }
    public string? InPlanNetworkIndicatorCode { get; set; }
    public List<StediAdditionalInformation>? AdditionalInformation { get; set; }
}

internal sealed class StediAdditionalInformation
{
    public string? Description { get; set; }
}

internal sealed class StediPlanDates
{
    public string? PlanBegin { get; set; }
    public string? PlanEnd { get; set; }
    public string? EligibilityBegin { get; set; }
    public string? EligibilityEnd { get; set; }
}

internal sealed class StediError
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}

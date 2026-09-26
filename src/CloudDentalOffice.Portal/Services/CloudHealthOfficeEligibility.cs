using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// CloudHealthOffice provider eligibility API (src/services/provider-eligibility-api
/// in the CHO repo). It runs as its own internal-only Container App, so it has
/// its own base URL and credential, separate from the estimate API.
/// </summary>
public sealed class CloudHealthOfficeEligibilityOptions
{
    /// <summary>Internal URL of the CHO provider eligibility app, e.g. https://provider-eligibility.internal.&lt;env-domain&gt;.</summary>
    public string BaseUrl { get; set; } = string.Empty;
    public string CheckPath { get; set; } = "/api/v1/eligibility/check";

    /// <summary>Client credential issued by CHO (Key Vault secret provider-eligibility-cdo-api-key).</summary>
    public string? ApiKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Builds an eligibility request from what CDO stores. When the patient is the
/// policyholder their own record supplies the subscriber's name and date of
/// birth; otherwise the coverage's subscriber fields do and the patient is sent
/// as a dependent. Messages are for front-desk staff and never echo values.
/// </summary>
public static partial class EligibilityRequestBuilder
{
    public static NormalizedEligibilityRequest Build(
        Patient patient, PatientInsurance? insurance, Provider? provider, DateOnly serviceDate, string tenantId)
    {
        if (insurance?.InsurancePlan is null)
            throw new TreatmentEstimateValidationException("Select an insurance coverage before checking eligibility.");
        if (string.IsNullOrWhiteSpace(insurance.InsurancePlan.PayerId))
            throw new TreatmentEstimateValidationException("This insurance plan does not have a payer ID. Add it to the plan before checking eligibility.");
        if (string.IsNullOrWhiteSpace(insurance.MemberId))
            throw new TreatmentEstimateValidationException("This coverage does not have a member ID on file.");
        if (provider is null || string.IsNullOrWhiteSpace(provider.NPI))
            throw new TreatmentEstimateValidationException("Select a rendering provider with an NPI before checking eligibility.");
        if (!NpiPattern().IsMatch(provider.NPI.Trim()))
            throw new TreatmentEstimateValidationException("The rendering provider's NPI must be 10 digits.");

        // CHO accepts service dates from two years back to one year ahead.
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (serviceDate < today.AddYears(-2) || serviceDate > today.AddYears(1))
            throw new TreatmentEstimateValidationException(
                "Eligibility can be checked for service dates from two years ago up to one year ahead. Choose a date in that range.");

        var relationship = NormalizeRelationship(insurance.RelationshipToSubscriber);

        string subscriberFirst, subscriberLast;
        DateOnly subscriberDob;
        EligibilityDependent? dependent = null;

        if (relationship is null)
        {
            subscriberFirst = Required(patient.FirstName, "The patient's first name is missing.");
            subscriberLast = Required(patient.LastName, "The patient's last name is missing.");
            subscriberDob = DateOfBirth(patient.DateOfBirth, "The patient's date of birth is missing or invalid.");
        }
        else
        {
            const string missingSubscriber =
                "This patient is covered under someone else's policy. Add the policyholder's first name, last name and date of birth to the coverage.";
            subscriberFirst = Required(insurance.SubscriberFirstName, missingSubscriber);
            subscriberLast = Required(insurance.SubscriberLastName, missingSubscriber);
            subscriberDob = DateOfBirth(insurance.SubscriberDateOfBirth, missingSubscriber);
            dependent = new EligibilityDependent(
                Required(patient.FirstName, "The patient's first name is missing."),
                Required(patient.LastName, "The patient's last name is missing."),
                DateOfBirth(patient.DateOfBirth, "The patient's date of birth is missing or invalid."),
                relationship);
        }

        // Same limits as CHO's validator, so staff get the message here instead of a rejected call.
        MaxLength(insurance.InsurancePlan.PayerId, 80, "The insurance plan's payer ID is too long.");
        MaxLength(insurance.MemberId, 80, "The member ID is too long (80 characters at most).");
        MaxLength(insurance.GroupNumber, 50, "The group number is too long (50 characters at most).");
        MaxLength(subscriberFirst, 60, "The policyholder's first name is too long (60 characters at most).");
        MaxLength(subscriberLast, 60, "The policyholder's last name is too long (60 characters at most).");
        MaxLength(dependent?.FirstName, 60, "The patient's first name is too long (60 characters at most).");
        MaxLength(dependent?.LastName, 60, "The patient's last name is too long (60 characters at most).");

        return new NormalizedEligibilityRequest
        {
            TenantId = tenantId,
            PayerId = insurance.InsurancePlan.PayerId.Trim(),
            MemberId = insurance.MemberId.Trim(),
            SubscriberFirstName = subscriberFirst,
            SubscriberLastName = subscriberLast,
            SubscriberDateOfBirth = subscriberDob,
            Dependent = dependent,
            GroupNumber = string.IsNullOrWhiteSpace(insurance.GroupNumber) ? null : insurance.GroupNumber.Trim(),
            ProviderNpi = provider.NPI.Trim(),
            ServiceDate = serviceDate
        };
    }

    /// <summary>
    /// Null means the patient is the subscriber. CDO's UI stores "Self",
    /// "Spouse", "Child" or "Other", but the field is free text through the
    /// API, so common synonyms and X12 relationship codes are accepted too.
    /// </summary>
    internal static string? NormalizeRelationship(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "18" or "self" or "subscriber" or "policyholder" or "policy holder" or "insured" or "employee" => null,
            "01" or "spouse" or "wife" or "husband" => EligibilityRelationship.Spouse,
            "19" or "child" or "son" or "daughter" or "dependent child" => EligibilityRelationship.Child,
            _ => EligibilityRelationship.Other
        };

    private static void MaxLength(string? value, int max, string message)
    {
        if (value is not null && value.Trim().Length > max) throw new TreatmentEstimateValidationException(message);
    }

    private static string Required(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new TreatmentEstimateValidationException(message) : value.Trim();

    private static DateOnly DateOfBirth(DateTime? value, string message)
    {
        if (value is not { } dob || dob.Year < 1900 || dob.Date > DateTime.Today)
            throw new TreatmentEstimateValidationException(message);
        return DateOnly.FromDateTime(dob);
    }

    [GeneratedRegex(@"^\d{10}$")]
    private static partial Regex NpiPattern();
}

public interface ICloudHealthOfficeEligibilityClient
{
    bool IsConfigured { get; }
    Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls the CHO provider eligibility API and turns its answer into CDO's
/// <see cref="EligibilityResult"/>. Every failure becomes a staff-readable
/// exception that says whether the problem is the patient's information
/// (fixable at the front desk) or on our side (try again or contact support).
/// Logs carry tenant, payer, status and correlation ID only — never member
/// IDs, names or dates of birth.
/// </summary>
public sealed class CloudHealthOfficeEligibilityClient : ICloudHealthOfficeEligibilityClient
{
    internal const string SourceName = "CloudHealthOffice";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly CloudHealthOfficeEligibilityOptions _options;
    private readonly ITenantProvider _tenantProvider;
    private readonly ILogger<CloudHealthOfficeEligibilityClient> _logger;

    public CloudHealthOfficeEligibilityClient(HttpClient httpClient, IOptions<CloudHealthOfficeOptions> options,
        ITenantProvider tenantProvider, ILogger<CloudHealthOfficeEligibilityClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value.Eligibility;
        _tenantProvider = tenantProvider;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public async Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            throw new TreatmentEstimateUnavailableException("Eligibility checks are not configured for this environment.");
        // The request carries the API key and the member's identity, so only a plain HTTPS origin is accepted.
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) ||
            !Uri.TryCreate(_options.CheckPath, UriKind.Relative, out _) || _options.CheckPath.StartsWith("//", StringComparison.Ordinal))
            throw new TreatmentEstimateUnavailableException("Eligibility checks are misconfigured. Contact support.");
        if (string.IsNullOrWhiteSpace(_tenantProvider.TenantId) || request.TenantId != _tenantProvider.TenantId)
            throw new UnauthorizedAccessException("The eligibility request is outside the active tenant.");

        var correlationId = request.CorrelationId ?? Guid.NewGuid().ToString("N");
        var logCorrelationId = ForLog(correlationId);
        var logTenantId = ForLog(request.TenantId);
        var logPayerId = ForLog(request.PayerId);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, _options.CheckPath));
        message.Headers.Add("X-Api-Key", _options.ApiKey);
        message.Headers.Add("X-Tenant-ID", request.TenantId);
        message.Content = JsonContent.Create(ToWire(request, correlationId), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Eligibility check {CorrelationId} for tenant {TenantId} payer {PayerId} timed out",
                logCorrelationId, logTenantId, logPayerId);
            throw new TreatmentEstimateUnavailableException("The eligibility check timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Eligibility service could not be reached for check {CorrelationId} tenant {TenantId}",
                logCorrelationId, logTenantId);
            throw new TreatmentEstimateUnavailableException("Eligibility checks are temporarily unavailable. Try again in a minute.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            _logger.LogInformation("Eligibility check {CorrelationId} for tenant {TenantId} payer {PayerId} returned HTTP {StatusCode}",
                logCorrelationId, logTenantId, logPayerId, status);

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                {
                    var body = await ReadAsync<ChoEligibilityResponse>(response, cancellationToken)
                        ?? throw new TreatmentEstimateUnavailableException("The eligibility response was incomplete. Try again.");
                    return CloudHealthOfficeEligibilityMapper.ToResult(body, correlationId);
                }
                case HttpStatusCode.BadRequest:
                {
                    var body = await ReadAsync<ChoValidationError>(response, cancellationToken);
                    throw new TreatmentEstimateValidationException(CloudHealthOfficeEligibilityMapper.ValidationMessage(body));
                }
                case HttpStatusCode.UnprocessableEntity:
                {
                    var body = await ReadAsync<ChoEligibilityResponse>(response, cancellationToken);
                    throw new TreatmentEstimateValidationException(CloudHealthOfficeEligibilityMapper.PayerProblemMessage(body));
                }
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    _logger.LogError("Eligibility service refused CDO's credential for tenant {TenantId} (HTTP {StatusCode})",
                        logTenantId, status);
                    throw new TreatmentEstimateUnavailableException(
                        "Eligibility checks are not authorized for this practice yet. Contact support.");
                case HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests:
                    throw new TreatmentEstimateUnavailableException(
                        "Eligibility checks are temporarily unavailable. Try again in a minute.");
                default:
                    throw new TreatmentEstimateUnavailableException(
                        "The eligibility check could not be completed. This is not a problem with the patient's information; try again later or contact support.");
            }
        }
    }

    // Tenant, payer and correlation IDs come from stored or caller-supplied data; strip line breaks
    // and other control characters so a value can't forge extra log entries.
    internal static string ForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : new string(value.Where(c => !char.IsControl(c)).ToArray());

    internal static ChoEligibilityRequest ToWire(NormalizedEligibilityRequest request, string correlationId) => new()
    {
        PayerId = request.PayerId,
        Provider = new ChoProvider { Npi = request.ProviderNpi },
        Subscriber = new ChoPerson
        {
            MemberId = request.MemberId,
            FirstName = request.SubscriberFirstName,
            LastName = request.SubscriberLastName,
            DateOfBirth = request.SubscriberDateOfBirth
        },
        Patient = request.Dependent is { } d
            ? new ChoPerson
            {
                MemberId = d.MemberId,
                FirstName = d.FirstName,
                LastName = d.LastName,
                DateOfBirth = d.DateOfBirth,
                RelationshipToSubscriber = d.Relationship
            }
            : null,
        GroupNumber = request.GroupNumber,
        ServiceTypeCode = request.ServiceTypeCodes.FirstOrDefault() ?? "35",
        ServiceDate = request.ServiceDate,
        CorrelationId = correlationId
    };

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Converts CHO's line-by-line benefits into the summary fields the portal
/// shows. Dental plans report the annual maximum as a limitation (EB01 "F")
/// and the deductible as "C"; the remaining amounts carry the "Remaining"
/// time period. Prefers in-network, individual-level lines. Leaves a field
/// empty rather than guessing when the payer did not send it.
/// </summary>
public static class CloudHealthOfficeEligibilityMapper
{
    private const string Deductible = "C";
    private const string Limitation = "F";
    private const string CoInsurance = "A";
    private const string CoPayment = "B";
    private const int MaxMessages = 20;

    // Dental care (35) or general plan coverage (30). Orthodontic (38) maxima
    // are lifetime limits and must not be read as the annual maximum.
    private static readonly HashSet<string> AnnualServiceTypes = new(StringComparer.OrdinalIgnoreCase) { "35", "30", "" };

    public static EligibilityResult ToResult(ChoEligibilityResponse response, string fallbackCorrelationId)
    {
        var status = ParseStatus(response.CoverageStatus);
        var benefits = response.Benefits ?? [];

        var messages = new List<string>();
        if (!string.IsNullOrWhiteSpace(response.Message)) messages.Add(response.Message.Trim());
        messages.AddRange(benefits.SelectMany(b => b.Messages ?? []).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()));

        return new EligibilityResult
        {
            CorrelationId = string.IsNullOrWhiteSpace(response.CorrelationId) ? fallbackCorrelationId : response.CorrelationId,
            CoverageStatus = status,
            PlanName = response.PlanName,
            EffectiveDate = response.CoverageStart,
            TerminationDate = response.CoverageEnd,
            Deductible = Amount(benefits, Deductible, remaining: false),
            DeductibleRemaining = Amount(benefits, Deductible, remaining: true),
            AnnualMaximum = Amount(benefits, Limitation, remaining: false),
            AnnualMaximumRemaining = Amount(benefits, Limitation, remaining: true),
            Benefits = benefits.Select(b => ToBenefit(b, status)).ToList(),
            Messages = messages.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxMessages).ToList(),
            Source = CloudHealthOfficeEligibilityClient.SourceName,
            VerifiedAt = response.CheckedAtUtc == default ? DateTimeOffset.UtcNow : response.CheckedAtUtc,
            ExternalTransactionId = response.CorrelationId
        };
    }

    public static string ValidationMessage(ChoValidationError? error)
    {
        var fields = error?.Fields?.Keys.Select(FieldLabel).Distinct().ToList() ?? [];
        return fields.Count == 0
            ? "The eligibility request was rejected. Review the patient's insurance details."
            : "Review the patient's insurance details: " + string.Join(", ", fields) + ".";
    }

    public static string PayerProblemMessage(ChoEligibilityResponse? response) => response?.ErrorCategory switch
    {
        "PayerNotFound" or "InvalidPayer" or "AmbiguousPayer" or "ExternalIdentifierMissing" =>
            "This plan's payer ID isn't recognized for eligibility checks. Check the payer ID on the insurance plan.",
        "EnrollmentRequired" =>
            "This payer requires enrollment before eligibility checks can be run for this practice.",
        "NotSupported" =>
            "This payer does not support electronic eligibility checks. Verify coverage by phone or the payer's portal.",
        _ when !string.IsNullOrWhiteSpace(response?.Message) =>
            "The payer could not verify this coverage: " + response!.Message!.Trim(),
        _ => "The payer could not verify this coverage. Review the member ID, name and date of birth on the coverage."
    };

    private static decimal? Amount(IReadOnlyList<ChoBenefit> benefits, string code, bool remaining)
    {
        // Full amounts come only from annual periods (or lines with no period,
        // ranked last). "Year to Date" is the amount already used, and visit or
        // episode limits are not annual figures, so they are never candidates.
        var candidates = benefits
            .Where(b => string.Equals(b.BenefitCode, code, StringComparison.OrdinalIgnoreCase))
            .Where(b => b.Amount is not null)
            .Where(b => remaining ? IsRemaining(b.TimePeriod) : IsAnnual(b.TimePeriod) || IsUnspecified(b.TimePeriod))
            .Where(b => code != Limitation || AnnualServiceTypes.Contains(b.ServiceTypeCode ?? string.Empty))
            .ToList();

        return candidates
            .OrderByDescending(b => b.InNetwork)
            .ThenByDescending(b => IsIndividual(b.CoverageLevel))
            .ThenByDescending(b => !IsUnspecified(b.TimePeriod))
            .Select(b => b.Amount)
            .FirstOrDefault();
    }

    private static EligibilityBenefit ToBenefit(ChoBenefit b, CoverageStatus overall)
    {
        var code = b.BenefitCode?.Trim().ToUpperInvariant();
        var remaining = IsRemaining(b.TimePeriod);
        var status = code switch
        {
            "1" => CoverageStatus.Active,
            "6" => CoverageStatus.Inactive,
            _ => overall
        };

        return new EligibilityBenefit(
            ServiceTypeCode: b.ServiceTypeCode ?? string.Empty,
            Description: Describe(b, code),
            Status: status,
            Copay: b.CopayAmount ?? (code == CoPayment ? b.Amount : null),
            Coinsurance: b.CoinsurancePercent ?? (code == CoInsurance ? b.Percent : null),
            Limit: code is Deductible or Limitation && (IsAnnual(b.TimePeriod) || IsUnspecified(b.TimePeriod)) ? b.Amount : null,
            Remaining: remaining ? b.Amount : null);
    }

    private static string Describe(ChoBenefit b, string? code)
    {
        var kind = code switch
        {
            "1" => "Active coverage",
            "6" => "Inactive",
            Deductible => "Deductible",
            Limitation => "Maximum",
            CoInsurance => "Coinsurance",
            CoPayment => "Copay",
            "G" => "Out-of-pocket",
            _ => null
        };
        var parts = new[]
        {
            kind,
            string.IsNullOrWhiteSpace(b.ServiceTypeName) ? null : b.ServiceTypeName.Trim(),
            string.IsNullOrWhiteSpace(b.TimePeriod) ? null : b.TimePeriod.Trim(),
            b.InNetwork ? null : "out of network"
        };
        var text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return string.IsNullOrWhiteSpace(text) ? "Benefit" : text;
    }

    private static CoverageStatus ParseStatus(string? value) =>
        Enum.TryParse<CoverageStatus>(value, ignoreCase: true, out var status) ? status : CoverageStatus.Unknown;

    // EB06 29 = Remaining. Stedi sends the text; other sources may send the code.
    private static bool IsRemaining(string? timePeriod) =>
        timePeriod?.Trim() is { } t && (t.Equals("Remaining", StringComparison.OrdinalIgnoreCase) || t == "29");

    // EB06 21 Years, 22 Service Year, 23 Calendar Year, 25 Contract. Stedi sends the text.
    private static readonly HashSet<string> AnnualPeriods = new(StringComparer.OrdinalIgnoreCase)
    {
        "21", "22", "23", "25", "Years", "Service Year", "Calendar Year", "Contract", "Plan Year", "Benefit Year"
    };

    private static bool IsAnnual(string? timePeriod) =>
        timePeriod is not null && AnnualPeriods.Contains(timePeriod.Trim());

    private static bool IsUnspecified(string? timePeriod) => string.IsNullOrWhiteSpace(timePeriod);

    private static bool IsIndividual(string? level) =>
        level?.Trim().ToUpperInvariant() is null or "" or "IND" or "INDIVIDUAL";

    private static string FieldLabel(string field) => field switch
    {
        "payerId" => "payer ID",
        "provider" or "provider.npi" => "provider NPI",
        "subscriber.memberId" => "member ID",
        "subscriber" or "subscriber.firstName" or "subscriber.lastName" => "policyholder name",
        "subscriber.dateOfBirth" => "policyholder date of birth",
        "patient.firstName" or "patient.lastName" => "patient name",
        "patient.dateOfBirth" => "patient date of birth",
        "patient.relationshipToSubscriber" => "relationship to policyholder",
        "groupNumber" => "group number",
        "serviceDate" => "service date",
        _ => "insurance details"
    };
}

// Wire contracts for the CHO provider eligibility API (camelCase JSON).

public sealed class ChoEligibilityRequest
{
    public string PayerId { get; set; } = string.Empty;
    public ChoProvider Provider { get; set; } = new();
    public ChoPerson Subscriber { get; set; } = new();
    public ChoPerson? Patient { get; set; }
    public string? GroupNumber { get; set; }
    public string? ServiceTypeCode { get; set; }
    public DateOnly? ServiceDate { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class ChoProvider
{
    public string Npi { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
}

public sealed class ChoPerson
{
    public string? MemberId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string? RelationshipToSubscriber { get; set; }
}

public sealed class ChoEligibilityResponse
{
    public string? Outcome { get; set; }
    public string? ErrorCategory { get; set; }
    public string? Message { get; set; }
    public bool Eligible { get; set; }
    public string? CoverageStatus { get; set; }
    public string? PlanName { get; set; }
    public string? PlanId { get; set; }
    public string? GroupNumber { get; set; }
    public DateOnly? CoverageStart { get; set; }
    public DateOnly? CoverageEnd { get; set; }
    public List<ChoBenefit>? Benefits { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CheckedAtUtc { get; set; }
}

public sealed class ChoBenefit
{
    public string? BenefitCode { get; set; }
    public string? ServiceTypeCode { get; set; }
    public string? ServiceTypeName { get; set; }
    public string? CoverageLevel { get; set; }
    public bool InNetwork { get; set; } = true;
    public string? TimePeriod { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Percent { get; set; }
    public decimal? CopayAmount { get; set; }
    public decimal? CoinsurancePercent { get; set; }
    public List<string>? Messages { get; set; }
}

public sealed class ChoValidationError
{
    public string? Error { get; set; }
    public Dictionary<string, string>? Fields { get; set; }
}

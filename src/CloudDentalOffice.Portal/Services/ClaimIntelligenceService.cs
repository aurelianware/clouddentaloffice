using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Practice-facing claim lifecycle. Built from Cloud Health Office claim
/// intelligence. Staff never see X12 transaction names or clearinghouse vendors.
/// </summary>
public sealed record ClaimLifecycleView(
    int ClaimId,
    string ClaimNumber,
    string Status,
    string StatusCode,
    string NextAction,
    string Expected,
    decimal? SubmittedAmount,
    decimal? AllowedAmount,
    decimal? PaidAmount,
    decimal? PatientResponsibility,
    bool HasRemittance,
    bool FinancialsPosted,
    bool ActionRequired,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<ClaimLifecycleEvent> Timeline,
    IReadOnlyList<ClaimLifecyclePosting> PostedFinancials);

public sealed record ClaimLifecycleEvent(string EventId, DateTimeOffset Timestamp, string Title, string? Detail);

public sealed record ClaimLifecyclePosting(string Type, decimal Amount, DateTime PostedAt, string Description);

public interface IClaimIntelligenceClient
{
    Task<ClaimIntelligenceWireView?> GetAsync(string tenantId, string cloudHealthOfficeClaimId, CancellationToken cancellationToken = default);
}

public interface IClaimLifecycleService
{
    Task<ClaimLifecycleView?> RefreshAsync(int claimId, CancellationToken cancellationToken = default);
}

public sealed class ClaimIntelligenceUnavailableException : Exception
{
    public ClaimIntelligenceUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class ClaimIntelligenceClient : IClaimIntelligenceClient
{
    private static readonly JsonSerializerOptions Json = CreateJson();

    private readonly HttpClient _httpClient;
    private readonly CloudHealthOfficeOptions _options;
    private readonly ILogger<ClaimIntelligenceClient> _logger;

    public ClaimIntelligenceClient(HttpClient httpClient, IOptions<CloudHealthOfficeOptions> options,
        ILogger<ClaimIntelligenceClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ClaimIntelligenceWireView?> GetAsync(string tenantId, string cloudHealthOfficeClaimId,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_options.IntelligenceBaseUrl) ? _options.BaseUrl : _options.IntelligenceBaseUrl;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(baseUrl))
            throw new ClaimIntelligenceUnavailableException("Claim status is not configured for this environment.");
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("Tenant is required.");
        if (string.IsNullOrWhiteSpace(cloudHealthOfficeClaimId) ||
            cloudHealthOfficeClaimId.Contains('/', StringComparison.Ordinal) ||
            cloudHealthOfficeClaimId.Contains('\\', StringComparison.Ordinal) ||
            cloudHealthOfficeClaimId.Contains('?', StringComparison.Ordinal))
            throw new ArgumentException("Claim id is invalid.", nameof(cloudHealthOfficeClaimId));

        var path = ResolvePath(cloudHealthOfficeClaimId);
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/')));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("X-Tenant-ID", tenantId);
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);

        try
        {
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.StatusCode is HttpStatusCode.BadRequest)
                throw new ClaimIntelligenceUnavailableException("Claim status could not be loaded. Confirm the practice is configured with Cloud Health Office.");
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Claim intelligence returned HTTP {StatusCode} for tenant {TenantId}",
                    (int)response.StatusCode, ClaimLifecycleMapper.SanitizeForLog(tenantId));
                throw new ClaimIntelligenceUnavailableException("Claim status is temporarily unavailable. Try again later.");
            }

            return await response.Content.ReadFromJsonAsync<ClaimIntelligenceWireView>(Json, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaimIntelligenceUnavailableException("Claim status timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Claim intelligence could not be reached for tenant {TenantId}",
                ClaimLifecycleMapper.SanitizeForLog(tenantId));
            throw new ClaimIntelligenceUnavailableException("Claim status is temporarily unavailable. Try again later.", ex);
        }
    }

    private string ResolvePath(string claimId)
    {
        var template = string.IsNullOrWhiteSpace(_options.IntelligencePath)
            ? "/api/claims/{claimId}/intelligence"
            : _options.IntelligencePath;
        if (!Uri.TryCreate(template, UriKind.Relative, out _) || template.StartsWith("//", StringComparison.Ordinal))
            throw new ClaimIntelligenceUnavailableException("Claim status is misconfigured: the intelligence path must be a relative URI.");
        return template.Replace("{claimId}", Uri.EscapeDataString(claimId), StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}

public sealed class ClaimLifecycleService : IClaimLifecycleService
{
    public const string LedgerSourcePrefix = "claim:";
    public const string PostedBy = "claim-intelligence";

    private readonly CloudDentalDbContext _db;
    private readonly IClaimIntelligenceClient _client;
    private readonly IPatientAccountService _accounts;
    private readonly ITenantProvider _tenantProvider;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClaimLifecycleService> _logger;

    public ClaimLifecycleService(
        CloudDentalDbContext db,
        IClaimIntelligenceClient client,
        IPatientAccountService accounts,
        ITenantProvider tenantProvider,
        TimeProvider clock,
        ILogger<ClaimLifecycleService> logger)
    {
        _db = db;
        _client = client;
        _accounts = accounts;
        _tenantProvider = tenantProvider;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ClaimLifecycleView?> RefreshAsync(int claimId, CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new UnauthorizedAccessException("Tenant is required.");

        var claim = await _db.Claims.FirstOrDefaultAsync(c => c.ClaimId == claimId, cancellationToken);
        if (claim is null)
            return null;
        if (!string.Equals(claim.TenantId, tenantId, StringComparison.Ordinal))
            return null;

        var choClaimId = FirstNonEmpty(claim.CloudHealthOfficeClaimId, claim.ClaimNumber);
        if (string.IsNullOrWhiteSpace(choClaimId) || IsLocalDraft(claim))
            return await BuildLocalViewAsync(claim, cancellationToken);

        ClaimIntelligenceWireView? wire;
        try
        {
            wire = await _client.GetAsync(tenantId, choClaimId, cancellationToken);
        }
        catch (ClaimIntelligenceUnavailableException)
        {
            var fallback = await BuildLocalViewAsync(claim, cancellationToken);
            return fallback;
        }

        if (wire is null)
            return await BuildLocalViewAsync(claim, cancellationToken);

        ApplyWire(claim, wire, _clock.GetUtcNow().UtcDateTime);
        var posted = await TryPostFinancialsAsync(claim, wire, cancellationToken);
        claim.LastIntelligenceAt = _clock.GetUtcNow().UtcDateTime;
        if (posted)
            claim.FinancialsPostedAt = claim.LastIntelligenceAt;
        claim.ModifiedDate = claim.LastIntelligenceAt;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Claim lifecycle refreshed tenant={TenantId} claim={ClaimId} status={Status} posted={Posted}",
            ClaimLifecycleMapper.SanitizeForLog(tenantId), claim.ClaimId,
            ClaimLifecycleMapper.SanitizeForLog(claim.LifecycleStatus), posted);

        return await BuildViewAsync(claim, wire, cancellationToken);
    }

    private static bool IsLocalDraft(Claim claim) =>
        string.Equals(claim.Status, "Draft", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(claim.CloudHealthOfficeClaimId) &&
        !claim.SubmittedDate.HasValue;

    private static void ApplyWire(Claim claim, ClaimIntelligenceWireView wire, DateTime nowUtc)
    {
        var mapped = ClaimLifecycleMapper.MapStatus(wire.LifecycleStatus);
        claim.LifecycleStatus = mapped.StatusCode;
        claim.Status = mapped.ClaimStatus;
        if (!string.IsNullOrWhiteSpace(wire.ClaimId) &&
            string.IsNullOrWhiteSpace(claim.CloudHealthOfficeClaimId))
            claim.CloudHealthOfficeClaimId = wire.ClaimId.Trim();

        if (wire.Financial.PaidAmount.HasValue)
            claim.PaidAmount = wire.Financial.PaidAmount;
        if (wire.Financial.PatientResponsibility.HasValue)
            claim.PatientResponsibility = wire.Financial.PatientResponsibility;
        if (mapped.ClaimStatus is "Paid" or "PartiallyPaid" or "Denied")
            claim.ProcessedDate ??= nowUtc;
    }

    private async Task<bool> TryPostFinancialsAsync(Claim claim, ClaimIntelligenceWireView wire, CancellationToken cancellationToken)
    {
        if (claim.FinancialsPostedAt.HasValue)
            return true;
        if (!ClaimLifecycleMapper.ShouldPostFinancials(wire))
            return false;

        var sourceId = LedgerSourcePrefix + claim.ClaimId;
        var effective = wire.GeneratedAtUtc.UtcDateTime;
        if (effective == default)
            effective = _clock.GetUtcNow().UtcDateTime;

        var charge = wire.Financial.SubmittedAmount ?? claim.TotalChargeAmount;
        var paid = wire.Financial.PaidAmount ?? 0m;
        var contractual = ClaimLifecycleMapper.ContractualAdjustment(wire.Financial, charge, paid);

        await PostQuietlyAsync(new PostPatientLedgerEntry(
            claim.TenantId, claim.PatientId, PatientLedgerEntryType.Charge, new Money(charge),
            effective, PatientLedgerSourceType.Claim, sourceId, "claim-charge", PostedBy), cancellationToken);

        if (paid > 0m)
        {
            await PostQuietlyAsync(new PostPatientLedgerEntry(
                claim.TenantId, claim.PatientId, PatientLedgerEntryType.InsurancePayment, new Money(paid),
                effective, PatientLedgerSourceType.Claim, sourceId, "claim-insurance", PostedBy), cancellationToken);
        }

        if (contractual > 0m)
        {
            await PostQuietlyAsync(new PostPatientLedgerEntry(
                claim.TenantId, claim.PatientId, PatientLedgerEntryType.ContractualAdjustment, new Money(contractual),
                effective, PatientLedgerSourceType.Claim, sourceId, "claim-adjustment", PostedBy), cancellationToken);
        }

        return true;
    }

    private async Task PostQuietlyAsync(PostPatientLedgerEntry command, CancellationToken cancellationToken)
    {
        if (command.Amount.Amount <= 0m)
            return;
        try
        {
            await _accounts.PostAsync(command, cancellationToken);
        }
        catch (DuplicateLedgerSourceException)
        {
            // Replay of the same remittance is a no-op.
        }
    }

    private async Task<ClaimLifecycleView> BuildLocalViewAsync(Claim claim, CancellationToken cancellationToken)
    {
        var status = ClaimLifecycleMapper.FromStored(claim.LifecycleStatus, claim.Status);
        var posted = await LoadPostedAsync(claim, cancellationToken);
        return new ClaimLifecycleView(
            claim.ClaimId,
            claim.ClaimNumber,
            status.Display,
            status.StatusCode,
            claim.SubmittedDate.HasValue ? "Waiting for payer" : "Submit claim",
            claim.SubmittedDate.HasValue ? "Pending payer response" : "Submit claim",
            claim.TotalChargeAmount,
            null,
            claim.PaidAmount,
            claim.PatientResponsibility,
            false,
            claim.FinancialsPostedAt.HasValue,
            false,
            claim.LastIntelligenceAt ?? claim.ModifiedDate ?? claim.CreatedDate,
            [],
            posted);
    }

    private async Task<ClaimLifecycleView> BuildViewAsync(Claim claim, ClaimIntelligenceWireView wire, CancellationToken cancellationToken)
    {
        var posted = await LoadPostedAsync(claim, cancellationToken);
        return ClaimLifecycleMapper.ToView(claim, wire, posted);
    }

    private async Task<IReadOnlyList<ClaimLifecyclePosting>> LoadPostedAsync(Claim claim, CancellationToken cancellationToken)
    {
        var sourceId = LedgerSourcePrefix + claim.ClaimId;
        var entries = await _db.PatientLedgerEntries.AsNoTracking()
            .Where(x => x.TenantId == claim.TenantId &&
                        x.SourceType == PatientLedgerSourceType.Claim &&
                        x.SourceId == sourceId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return entries.Select(ClaimLifecycleMapper.ToPosting).ToList();
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public static class ClaimLifecycleMapper
{
    /// <summary>
    /// Strips CR/LF so tenant and status tokens cannot forge additional log lines.
    /// </summary>
    public static string SanitizeForLog(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    public static ClaimLifecycleView ToView(Claim claim, ClaimIntelligenceWireView wire,
        IReadOnlyList<ClaimLifecyclePosting> posted)
    {
        var status = MapStatus(wire.LifecycleStatus);
        return new ClaimLifecycleView(
            claim.ClaimId,
            claim.ClaimNumber,
            status.Display,
            status.StatusCode,
            MapNextAction(wire.Workflow.NextAction),
            MapExpected(wire.LifecycleStatus, wire.Workflow.Expected),
            wire.Financial.SubmittedAmount ?? claim.TotalChargeAmount,
            wire.Financial.AllowedAmount,
            wire.Financial.PaidAmount ?? claim.PaidAmount,
            wire.Financial.PatientResponsibility ?? claim.PatientResponsibility,
            wire.Financial.HasRemittance,
            claim.FinancialsPostedAt.HasValue || posted.Count > 0,
            wire.Signals.ActionRequired,
            wire.GeneratedAtUtc,
            wire.Timeline.Select(MapEvent).ToList(),
            posted);
    }

    public static (string Display, string StatusCode, string ClaimStatus) MapStatus(string? lifecycle)
    {
        return Normalize(lifecycle) switch
        {
            "draft" => ("Draft", "Draft", "Draft"),
            "submitted" => ("Submitted", "Submitted", "Submitted"),
            "acceptedbyclearinghouse" => ("Accepted", "Accepted", "Accepted"),
            "acceptedbypayer" => ("Payer accepted", "AcceptedByPayer", "Accepted"),
            "processing" => ("Processing", "Processing", "Processing"),
            "pendinginformation" => ("Information needed", "PendingInformation", "Pending"),
            "denied" => ("Denied", "Denied", "Denied"),
            "paid" => ("Paid", "Paid", "Paid"),
            "partiallypaid" => ("Partially paid", "PartiallyPaid", "PartiallyPaid"),
            "completed" => ("Complete", "Completed", "Paid"),
            _ => ("Unknown", "Unknown", "Submitted")
        };
    }

    public static (string Display, string StatusCode) FromStored(string? lifecycleStatus, string? claimStatus)
    {
        if (!string.IsNullOrWhiteSpace(lifecycleStatus))
        {
            var mapped = MapStatus(lifecycleStatus);
            return (mapped.Display, mapped.StatusCode);
        }

        return claimStatus switch
        {
            "Draft" => ("Draft", "Draft"),
            "Ready" => ("Ready", "Ready"),
            "Submitted" => ("Submitted", "Submitted"),
            "Accepted" => ("Accepted", "Accepted"),
            "Rejected" => ("Denied", "Denied"),
            "Denied" => ("Denied", "Denied"),
            "Paid" => ("Paid", "Paid"),
            "PartiallyPaid" => ("Partially paid", "PartiallyPaid"),
            "Processing" => ("Processing", "Processing"),
            "Pending" => ("Information needed", "PendingInformation"),
            _ => (claimStatus ?? "Unknown", claimStatus ?? "Unknown")
        };
    }

    public static string MapNextAction(string? next) =>
        Normalize(next) switch
        {
            "none" or "" => "",
            "waitforclearinghouse" => "Waiting for submission confirmation",
            "waitforpayer" => "Waiting for payer",
            "provideinformation" => "Payer requested information",
            "correctandresubmit" => "Correct and resubmit",
            "readyforposting" => "Post payment to the patient account",
            _ => "Review claim"
        };

    public static string MapExpected(string? lifecycle, string? expected)
    {
        var fromLifecycle = Normalize(lifecycle) switch
        {
            "draft" => "Submit claim",
            "submitted" => "Pending submission confirmation",
            "acceptedbyclearinghouse" => "Pending payer acknowledgment",
            "acceptedbypayer" => "Pending payment",
            "processing" => "Pending payment",
            "pendinginformation" => "Payer requested information",
            "denied" => "Correction required",
            "paid" => "Ready for posting",
            "partiallypaid" => "Ready for posting",
            "completed" => "Complete",
            _ => null
        };
        if (fromLifecycle is not null)
            return fromLifecycle;
        return StripEdi(expected) ?? "Unknown";
    }

    public static ClaimLifecycleEvent MapEvent(ClaimIntelligenceWireEvent evt)
    {
        var title = EventTitle(evt.EventType, evt.SourceTransaction);
        var detail = EventDetail(evt.Status);
        return new ClaimLifecycleEvent(evt.EventId, evt.Timestamp, title, detail);
    }

    public static ClaimLifecyclePosting ToPosting(PatientLedgerEntry entry) =>
        new(PostingType(entry.EntryType), entry.Amount, entry.CreatedAt, PostingDescription(entry.DescriptionCode));

    public static bool ShouldPostFinancials(ClaimIntelligenceWireView wire)
    {
        if (!wire.Financial.HasRemittance)
            return false;
        var status = Normalize(wire.LifecycleStatus);
        if (status is "denied")
            return false;
        if (status is "paid" or "partiallypaid" or "completed")
            return true;
        if (Normalize(wire.Workflow.NextAction) is "readyforposting" or "none"
            && (wire.Financial.PaidAmount ?? 0m) > 0m)
            return true;
        return wire.Timeline.Any(e =>
            string.Equals(e.EventType, "Posted", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(e.EventType, "ReadyForPosting", StringComparison.OrdinalIgnoreCase));
    }

    public static decimal ContractualAdjustment(ClaimIntelligenceWireFinancial financial, decimal submitted, decimal paid)
    {
        if (financial.AllowedAmount is { } allowed && submitted > allowed)
            return decimal.Round(submitted - allowed, 2);
        if (financial.PatientResponsibility is { } patient)
        {
            var remainder = decimal.Round(submitted - paid - patient, 2);
            return remainder > 0m ? remainder : 0m;
        }
        return 0m;
    }

    private static string EventTitle(string? eventType, string? source)
    {
        return Normalize(eventType) switch
        {
            "837submitted" => "Claim submitted",
            "gatewayaccepted" => "Submission accepted",
            "277caaccepted" => "Payer accepted the claim",
            "277carejected" => "Payer rejected the claim",
            "275attachmentsubmitted" => "Documentation sent",
            "275attachmentreceived" => "Documentation received",
            "readyforposting" => "Payment ready to post",
            "posted" => "Payment posted",
            "835received" => "Payment received",
            _ when Normalize(eventType).StartsWith("276277", StringComparison.Ordinal) =>
                StatusInquiryTitle(eventType),
            _ => FallbackTitle(source)
        };
    }

    private static string StatusInquiryTitle(string? eventType)
    {
        var status = eventType is null ? "" : eventType.Replace("276277", "", StringComparison.OrdinalIgnoreCase);
        return Normalize(status) switch
        {
            "inprocess" => "Payer is processing the claim",
            "pending" => "Payer requested more information",
            "additionalinformationrequested" => "Payer requested more information",
            "finalized" => "Payer finalized the claim",
            "denied" => "Payer denied the claim",
            "paid" => "Payer reported payment",
            "norecordfound" => "Payer has no record of the claim",
            _ => "Payer status update"
        };
    }

    private static string FallbackTitle(string? source) =>
        Normalize(source) switch
        {
            "837" => "Claim submitted",
            "277ca" => "Payer acknowledgment",
            "276277" => "Payer status update",
            "275" => "Documentation update",
            "835" => "Payment update",
            _ => "Claim update"
        };

    private static string? EventDetail(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;
        return Normalize(status) switch
        {
            "accepted" => "Accepted",
            "rejected" => "Rejected",
            "posted" => "Posted",
            "availableforposting" => "Ready to post",
            "matched" => "Matched",
            "inprocess" => "In process",
            "pending" => "Pending",
            "denied" => "Denied",
            "transmitted" => "Sent",
            "submissionacceptedbygateway" => "Accepted",
            "acknowledgmentaccepted" => "Accepted",
            _ => Humanize(status)
        };
    }

    private static string PostingType(PatientLedgerEntryType type) => type switch
    {
        PatientLedgerEntryType.Charge => "Charge",
        PatientLedgerEntryType.InsurancePayment => "Insurance payment",
        PatientLedgerEntryType.ContractualAdjustment => "Contractual adjustment",
        _ => type.ToString()
    };

    private static string PostingDescription(string code) => code switch
    {
        "claim-charge" => "Billed amount",
        "claim-insurance" => "Insurance payment",
        "claim-adjustment" => "Contractual adjustment",
        _ => "Posted"
    };

    private static string? StripEdi(string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return expected;
        return expected
            .Replace("Pending 277CA", "Pending submission confirmation", StringComparison.OrdinalIgnoreCase)
            .Replace("Pending ERA", "Pending payment", StringComparison.OrdinalIgnoreCase)
            .Replace("277CA", "payer acknowledgment", StringComparison.OrdinalIgnoreCase)
            .Replace("835", "payment", StringComparison.OrdinalIgnoreCase)
            .Replace("ERA", "payment", StringComparison.OrdinalIgnoreCase);
    }

    private static string Humanize(string status)
    {
        var chars = status.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString());
        return string.Concat(chars);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant();
}

public sealed class ClaimIntelligenceWireView
{
    public string ClaimId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string LifecycleStatus { get; set; } = string.Empty;
    public ClaimIntelligenceWireFinancial Financial { get; set; } = new();
    public ClaimIntelligenceWireWorkflow Workflow { get; set; } = new();
    public ClaimIntelligenceWireSignals Signals { get; set; } = new();
    public List<ClaimIntelligenceWireEvent> Timeline { get; set; } = [];
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

public sealed class ClaimIntelligenceWireFinancial
{
    public decimal? SubmittedAmount { get; set; }
    public decimal? AllowedAmount { get; set; }
    public decimal? PaidAmount { get; set; }
    public decimal? PatientResponsibility { get; set; }
    public bool HasRemittance { get; set; }
}

public sealed class ClaimIntelligenceWireWorkflow
{
    public string? Expected { get; set; }
    public string? PatientResponsibilityDisplay { get; set; }
    public string NextAction { get; set; } = string.Empty;
}

public sealed class ClaimIntelligenceWireSignals
{
    public bool ActionRequired { get; set; }
    public bool NeedsFollowUp { get; set; }
}

public sealed class ClaimIntelligenceWireEvent
{
    public string EventId { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string SourceTransaction { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Metadata { get; set; }
}

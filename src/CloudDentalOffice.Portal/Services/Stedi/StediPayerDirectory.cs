using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services.Stedi;

/// <summary>What staff search for. Filters default to what a dental practice needs.</summary>
public sealed record StediPayerSearchRequest
{
    public string Query { get; init; } = string.Empty;
    public bool DentalOnly { get; init; } = true;
    public bool EligibilityOnly { get; init; } = true;

    /// <summary>Two-letter state; payers operating there or nationally are kept. Null means any state.</summary>
    public string? State { get; init; }
}

/// <summary>One payer from Stedi's directory, reduced to what staff need to choose it.</summary>
public sealed record StediPayerSummary(
    string StediId,
    string PrimaryPayerId,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> CoverageTypes,
    IReadOnlyList<string> OperatingStates,
    string EligibilitySupport,
    string DentalClaimSupport)
{
    public bool SupportsEligibility => IsAvailable(EligibilitySupport);
    public bool SupportsDentalClaims => IsAvailable(DentalClaimSupport);

    internal static bool IsAvailable(string? support) =>
        support is not null && (support.Equals("SUPPORTED", StringComparison.OrdinalIgnoreCase) ||
                                support.Equals("ENROLLMENT_REQUIRED", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Staff-readable failure from payer search or import. Never includes keys.</summary>
public sealed class StediPayerDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

public interface IStediPayerSearchClient
{
    Task<IReadOnlyList<StediPayerSummary>> SearchAsync(string tenantId, StediPayerSearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Searches Stedi's payer directory with the calling practice's own Stedi key
/// (attached by <see cref="StediCredentialHandler"/>), so any practice connected
/// to Stedi can use it. Filtering by coverage, eligibility support and state is
/// done here rather than through query-string filters, so results do not depend
/// on how the API encodes array parameters. Logs carry tenant, status and counts.
/// </summary>
public sealed class StediPayerSearchClient(
    HttpClient httpClient, IOptions<StediOptions> options, ILogger<StediPayerSearchClient> logger) : IStediPayerSearchClient
{
    internal const int PageSize = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<StediPayerSummary>> SearchAsync(
        string tenantId, StediPayerSearchRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new StediPayerDirectoryException(StediEligibilityClient.StaffMessage(StediCredentialFailure.MissingTenant),
                new StediCredentialUnavailableException(string.Empty, StediCredentialFailure.MissingTenant, "Payer search has no tenant."));

        var query = request.Query?.Trim() ?? string.Empty;
        if (query.Length is < 2 or > 80 || query.Any(char.IsControl))
            throw new StediPayerDirectoryException("Enter 2 to 80 characters to search for a payer.");

        var state = NormalizeState(request.State);
        var target = StediEndpoints.Resolve(options.Value.PayersBaseUrl, options.Value.PayerSearchPath)
            ?? throw new StediPayerDirectoryException("Payer search is misconfigured. Contact support.");

        using var message = new HttpRequestMessage(HttpMethod.Get,
            new Uri(target, $"?query={Uri.EscapeDataString(query)}&pageSize={PageSize}"));
        message.Options.Set(StediRequest.Tenant, tenantId);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(message, cancellationToken);
        }
        catch (StediCredentialUnavailableException ex)
        {
            throw new StediPayerDirectoryException(StediEligibilityClient.StaffMessage(ex.Failure), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StediPayerDirectoryException("The payer search timed out. Try again.", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Stedi payer search could not be reached for tenant {TenantId} ({ErrorType})",
                ClaimLifecycleMapper.SanitizeForLog(tenantId), ex.GetType().Name);
            throw new StediPayerDirectoryException("Payer search is temporarily unavailable. Try again in a minute.", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    break;
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    logger.LogError("Stedi refused the payer search credential for tenant {TenantId} (HTTP {StatusCode})",
                        ClaimLifecycleMapper.SanitizeForLog(tenantId), status);
                    throw new StediPayerDirectoryException("This practice's Stedi account did not accept our credentials. Contact support.");
                case HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout:
                    throw new StediPayerDirectoryException("Payer search is temporarily unavailable. Try again in a minute.");
                default:
                    throw new StediPayerDirectoryException("The payer search could not be completed. Try again later.");
            }

            StediPayerSearchResponse? body;
            try
            {
                body = await response.Content.ReadFromJsonAsync<StediPayerSearchResponse>(Json, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new StediPayerDirectoryException("The payer search response was incomplete. Try again.", ex);
            }

            var results = (body?.Items ?? [])
                .Select(i => i.Payer)
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.PrimaryPayerId) && !string.IsNullOrWhiteSpace(p.DisplayName))
                .Select(p => ToSummary(p!))
                .Where(p => !request.DentalOnly || p.CoverageTypes.Count == 0 ||
                            p.CoverageTypes.Contains("dental", StringComparer.OrdinalIgnoreCase))
                .Where(p => !request.EligibilityOnly || p.SupportsEligibility)
                .Where(p => state is null || p.OperatingStates.Count == 0 ||
                            p.OperatingStates.Contains(state, StringComparer.OrdinalIgnoreCase) ||
                            p.OperatingStates.Contains("NATIONAL", StringComparer.OrdinalIgnoreCase))
                .ToList();

            logger.LogInformation("Stedi payer search for tenant {TenantId} returned {Returned} payers, {Kept} after filters",
                ClaimLifecycleMapper.SanitizeForLog(tenantId), body?.Items?.Count ?? 0, results.Count);
            return results;
        }
    }

    internal static string? NormalizeState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var s = state.Trim().ToUpperInvariant();
        return s.Length == 2 && s.All(char.IsAsciiLetterUpper)
            ? s
            : throw new StediPayerDirectoryException("Choose a two-letter state, or any state.");
    }

    private static StediPayerSummary ToSummary(StediPayerRecord p) => new(
        p.StediId ?? string.Empty,
        p.PrimaryPayerId!.Trim(),
        p.DisplayName!.Trim(),
        p.Aliases ?? [],
        p.CoverageTypes ?? [],
        p.OperatingStates ?? [],
        p.TransactionSupport?.EligibilityCheck ?? "NOT_SUPPORTED",
        p.TransactionSupport?.DentalClaimSubmission ?? "NOT_SUPPORTED");
}

public sealed record PayerImportResult(InsurancePlan Plan, bool Created);

public interface IPayerImportService
{
    /// <summary>Adds the payer as an insurance plan for the caller's practice, or returns the existing active one.</summary>
    Task<PayerImportResult> AddAsync(StediPayerSummary payer, CancellationToken cancellationToken = default);

    /// <summary>Payer IDs of the practice's active plans, for marking search results already added.</summary>
    Task<IReadOnlySet<string>> ExistingPayerIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a Stedi directory payer into an insurance plan in the caller's
/// practice, using Stedi's primary payer ID so eligibility checks route
/// correctly. Adding the same payer twice returns the existing plan.
/// </summary>
public sealed class PayerImportService(
    CloudDentalDbContext db, ITenantProvider tenantProvider, TimeProvider time, ILogger<PayerImportService> logger)
    : IPayerImportService
{
    // InsurancePlan column limits.
    internal const int MaxPayerIdLength = 10;
    internal const int MaxPayerNameLength = 255;

    public async Task<PayerImportResult> AddAsync(StediPayerSummary payer, CancellationToken cancellationToken = default)
    {
        var payerId = payer.PrimaryPayerId?.Trim() ?? string.Empty;
        if (payerId.Length == 0 || payerId.Length > MaxPayerIdLength)
            throw new StediPayerDirectoryException(
                $"This payer's ID doesn't fit CDO's payer ID field ({MaxPayerIdLength} characters). Add it manually or contact support.");
        var name = payer.DisplayName?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new StediPayerDirectoryException("This payer has no name in the directory.");
        if (name.Length > MaxPayerNameLength) name = name[..MaxPayerNameLength];

        // Query filters scope this to the caller's practice.
        var existing = await db.InsurancePlans
            .Where(p => p.PayerId == payerId && p.IsActive)
            .OrderBy(p => p.InsurancePlanId)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
            return new PayerImportResult(existing, Created: false);

        var plan = new InsurancePlan
        {
            TenantId = tenantProvider.TenantId,
            PayerId = payerId,
            PayerName = name,
            IsActive = true,
            EdiEnabled = false,
            CreatedDate = time.GetUtcNow().UtcDateTime
        };
        db.InsurancePlans.Add(plan);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Imported payer {PayerId} from the Stedi directory as plan {InsurancePlanId} for tenant {TenantId}",
            ClaimLifecycleMapper.SanitizeForLog(payerId), plan.InsurancePlanId, ClaimLifecycleMapper.SanitizeForLog(tenantProvider.TenantId));
        return new PayerImportResult(plan, Created: true);
    }

    public async Task<IReadOnlySet<string>> ExistingPayerIdsAsync(CancellationToken cancellationToken = default) =>
        (await db.InsurancePlans.Where(p => p.IsActive).Select(p => p.PayerId).ToListAsync(cancellationToken))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

// Stedi payer search wire shape: items[] of { payer, score }.
internal sealed class StediPayerSearchResponse
{
    public List<StediPayerSearchItem>? Items { get; set; }
    public string? NextPageToken { get; set; }
}

internal sealed class StediPayerSearchItem
{
    public StediPayerRecord? Payer { get; set; }
    public double? Score { get; set; }
}

internal sealed class StediPayerRecord
{
    public string? StediId { get; set; }
    public string? PrimaryPayerId { get; set; }
    public string? DisplayName { get; set; }
    public List<string>? Aliases { get; set; }
    public List<string>? CoverageTypes { get; set; }
    public List<string>? OperatingStates { get; set; }
    public StediPayerTransactionSupport? TransactionSupport { get; set; }
}

internal sealed class StediPayerTransactionSupport
{
    public string? EligibilityCheck { get; set; }
    public string? DentalClaimSubmission { get; set; }
}

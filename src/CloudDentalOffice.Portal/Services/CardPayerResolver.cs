using System.Text.RegularExpressions;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Stedi;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// One payer the card could mean. <paramref name="InsurancePlanId"/> is set when the
/// practice already has it; otherwise <paramref name="DirectoryPayer"/> is added on use.
/// </summary>
public sealed record CardPayerCandidate(
    string PayerId, string PayerName, int? InsurancePlanId, StediPayerSummary? DirectoryPayer,
    bool MatchesCardPayerId, string Why);

/// <param name="Suggested">Preselected only when the card points at exactly one payer; staff still confirm.</param>
/// <param name="Message">Why the directory couldn't be searched, if it couldn't.</param>
public sealed record CardPayerResolution(
    IReadOnlyList<CardPayerCandidate> Candidates, CardPayerCandidate? Suggested, string? Message);

public interface ICardPayerResolver
{
    /// <summary>Candidate payers for what a card shows, from the practice's plans and the clearinghouse directory.</summary>
    Task<CardPayerResolution> ResolveAsync(string? cardPayerName, string? cardPayerId, string? patientState,
        CancellationToken cancellationToken = default);

    /// <summary>The practice's plan for the candidate, adding the directory payer first if needed.</summary>
    Task<InsurancePlan> UseAsync(CardPayerCandidate candidate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns what's printed on a card into the practice's clearinghouse payer. The
/// printed payer ID is only a hint: many cards print a claims ID that differs from
/// the eligibility payer ID, and "Delta Dental" is a dozen separate companies. So a
/// payer is suggested only when the evidence points at exactly one, and staff
/// always confirm. Directory search and adding a payer are staff-only (enforced by
/// <see cref="IPayerImportService"/>).
/// </summary>
public sealed partial class CardPayerResolver(
    CloudDentalDbContext db, IPayerImportService directory, ILogger<CardPayerResolver> logger) : ICardPayerResolver
{
    private const int MaxDirectoryResults = 8;

    // Words that appear in most payer names and say nothing about which payer it is.
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dental", "insurance", "health", "healthcare", "plan", "plans", "of", "the", "and", "inc", "co", "company",
        "corp", "corporation", "group", "services", "service", "life", "benefits", "benefit", "care", "network",
        "national", "america", "american", "ppo", "hmo", "dhmo", "premier", "member", "members",
        // Places: "Delta Dental of Arizona" and "Blue Cross Arizona" are different payers.
        "ak", "al", "alabama", "alaska", "ar", "arizona", "arkansas", "az", "ca", "california", "carolina", "co",
        "colorado", "columbia", "connecticut", "ct", "dakota", "dc", "de", "delaware", "district", "fl", "florida",
        "ga", "georgia", "hampshire", "hawaii", "hi", "ia", "id", "idaho", "il", "illinois", "in", "indiana", "iowa",
        "island", "jersey", "kansas", "kentucky", "ks", "ky", "la", "louisiana", "ma", "maine", "maryland",
        "massachusetts", "md", "me", "mexico", "mi", "michigan", "minnesota", "mississippi", "missouri", "mn", "mo",
        "montana", "ms", "mt", "nc", "nd", "ne", "nebraska", "nevada", "new", "nh", "nj", "nm", "north", "nv", "ny",
        "oh", "ohio", "ok", "oklahoma", "or", "oregon", "pa", "pennsylvania", "rhode", "ri", "sc", "sd", "south",
        "tennessee", "texas", "tn", "tx", "ut", "utah", "va", "vermont", "virginia", "vt", "wa", "washington",
        "west", "wi", "wisconsin", "wv", "wy", "wyoming", "york"
    };

    public async Task<CardPayerResolution> ResolveAsync(string? cardPayerName, string? cardPayerId, string? patientState,
        CancellationToken cancellationToken = default)
    {
        var printedId = Clean(cardPayerId);
        var name = Clean(cardPayerName);
        var nameWords = Significant(name);
        var state = patientState?.Trim().ToUpperInvariant() is { Length: 2 } s && s.All(char.IsAsciiLetterUpper) ? s : null;

        var plans = await db.InsurancePlans.AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);
        var candidates = new List<CardPayerCandidate>();

        foreach (var plan in plans)
        {
            var idMatch = printedId is not null && string.Equals(plan.PayerId, printedId, StringComparison.OrdinalIgnoreCase);
            var nameMatch = nameWords.Count > 0 && Significant(plan.PayerName).Overlaps(nameWords);
            if (idMatch || nameMatch)
                candidates.Add(new(plan.PayerId, plan.PayerName, plan.InsurancePlanId, null, idMatch,
                    idMatch ? "One of your payers; its payer ID matches the card." : "One of your payers; the name matches the card."));
        }

        string? message = null;
        try
        {
            var found = new List<StediPayerSummary>();
            if (printedId is { Length: >= 2 })
                found.AddRange(await directory.SearchAsync(new() { Query = printedId }, cancellationToken));
            if (name is { Length: >= 2 })
                found.AddRange(await directory.SearchAsync(new() { Query = Truncate(name, 80), State = state }, cancellationToken));

            foreach (var payer in found.DistinctBy(p => p.PrimaryPayerId, StringComparer.OrdinalIgnoreCase).Take(MaxDirectoryResults * 2))
            {
                if (candidates.Any(c => string.Equals(c.PayerId, payer.PrimaryPayerId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var idMatch = printedId is not null &&
                    (string.Equals(payer.PrimaryPayerId, printedId, StringComparison.OrdinalIgnoreCase) ||
                     payer.Aliases.Contains(printedId, StringComparer.OrdinalIgnoreCase));
                var nameMatch = nameWords.Count > 0 &&
                    (Significant(payer.DisplayName).Overlaps(nameWords) || payer.Aliases.Any(a => Significant(a).Overlaps(nameWords)));
                if (!idMatch && !nameMatch) continue;
                candidates.Add(new(payer.PrimaryPayerId, payer.DisplayName, null, payer, idMatch,
                    (idMatch ? "Clearinghouse directory; the card's payer ID matches." : "Clearinghouse directory; the name matches.") +
                    (state is not null && payer.OperatingStates.Contains(state, StringComparer.OrdinalIgnoreCase)
                        ? $" Operates in {state}." : "")));
            }
        }
        catch (StediPayerDirectoryException ex)
        {
            // The practice's own payers still work without the directory.
            message = $"The payer directory couldn't be searched: {ex.Message}";
            logger.LogInformation("Card payer lookup used practice payers only ({Reason}).", ex.GetType().Name);
        }

        var ordered = candidates
            .OrderByDescending(c => c.MatchesCardPayerId)
            .ThenByDescending(c => c.InsurancePlanId is not null)
            .Take(MaxDirectoryResults)
            .ToList();
        var idMatches = ordered.Where(c => c.MatchesCardPayerId).ToList();
        var suggested = idMatches.Count == 1 ? idMatches[0] : idMatches.Count == 0 && ordered.Count == 1 ? ordered[0] : null;
        return new(ordered, suggested, message);
    }

    public async Task<InsurancePlan> UseAsync(CardPayerCandidate candidate, CancellationToken cancellationToken = default)
    {
        if (candidate.InsurancePlanId is { } planId)
            return await db.InsurancePlans.AsNoTracking().SingleOrDefaultAsync(p => p.InsurancePlanId == planId && p.IsActive, cancellationToken)
                ?? throw new StediPayerDirectoryException("That payer is no longer active for this practice.");
        if (candidate.DirectoryPayer is null)
            throw new StediPayerDirectoryException("That payer can't be added from the directory.");
        return (await directory.AddAsync(candidate.DirectoryPayer, cancellationToken)).Plan;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>The words that identify a payer ("delta", "cigna", "guardian"), lower-cased.</summary>
    public static HashSet<string> Significant(string? name) =>
        name is null ? [] : Words().Matches(name).Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 1 && !CommonWords.Contains(w)).ToHashSet();

    [GeneratedRegex("[A-Za-z0-9]+")]
    private static partial Regex Words();
}

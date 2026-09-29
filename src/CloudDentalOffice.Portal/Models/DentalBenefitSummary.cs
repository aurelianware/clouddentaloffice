namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// The front desk's view of a dental plan: what the plan pays for preventive,
/// basic and major care ("100/80/50"), the annual maximum and deductible, and
/// what the payer's answer left out. Built from the eligibility benefit lines;
/// every figure is the payer's, never a guess.
/// </summary>
public sealed record DentalBenefitSummary
{
    public required CategoryCoverage Preventive { get; init; }
    public required CategoryCoverage Basic { get; init; }
    public required CategoryCoverage Major { get; init; }

    /// <summary>Orthodontics is reported against a lifetime maximum, never the annual one.</summary>
    public required CategoryCoverage Orthodontics { get; init; }
    public decimal? OrthodonticLifetimeMaximum { get; init; }
    public decimal? OrthodonticLifetimeMaximumRemaining { get; init; }

    public decimal? AnnualMaximum { get; init; }
    public decimal? AnnualMaximumRemaining { get; init; }
    public decimal? Deductible { get; init; }
    public decimal? DeductibleRemaining { get; init; }

    /// <summary>What staff must confirm by phone or the payer's portal, e.g. "Basic %", "Annual maximum".</summary>
    public IReadOnlyList<string> MissingFields { get; init; } = [];

    /// <summary>True when the answer has everything an estimate needs.</summary>
    public bool IsComplete => MissingFields.Count == 0;
}

/// <param name="PlanPaysPercent">Share the plan pays, 0–1 (1 − the patient's coinsurance); null when the payer didn't say.</param>
/// <param name="Covered">False when the payer reports the category as not covered; null when it didn't say.</param>
/// <param name="FromGeneralDentalLine">True when the percent came from the plan's general dental line, not a category-specific one.</param>
/// <param name="Notes">Payer remarks on the category (frequencies, waiting periods), verbatim and deduplicated.</param>
public sealed record CategoryCoverage(
    decimal? PlanPaysPercent, bool? Covered, bool FromGeneralDentalLine, IReadOnlyList<string> Notes)
{
    public static readonly CategoryCoverage NotReported = new(null, null, false, []);
}

/// <summary>X12 service type codes (EB03) a dental eligibility check asks about, and how they roll up into categories.</summary>
public static class DentalServiceTypes
{
    public const string DentalCare = "35";

    public static readonly IReadOnlyList<string> Preventive = ["41", "23"];
    public static readonly IReadOnlyList<string> Basic = ["25", "26", "24", "40"];
    public static readonly IReadOnlyList<string> Major = ["39", "36", "27"];
    public static readonly IReadOnlyList<string> Orthodontics = ["38"];

    /// <summary>The initial check: dental care only. Cheapest and accepted by every dental payer.</summary>
    public static readonly IReadOnlyList<string> BasicInquiry = [DentalCare];

    /// <summary>
    /// Dental care plus every code a category reads. <see cref="DentalCare"/> stays first:
    /// a gateway that sends only one code still asks about dental care.
    /// </summary>
    public static readonly IReadOnlyList<string> DetailedInquiry =
        [DentalCare, .. Preventive, .. Basic, .. Major, .. Orthodontics];
}

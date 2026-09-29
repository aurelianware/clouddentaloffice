using System.Globalization;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

/// <summary>Where a CDT code falls in a dental plan's benefit categories.</summary>
public enum DentalBenefitCategory { Preventive, Basic, Major, Orthodontics, Unknown }

/// <summary>
/// Estimates a treatment plan from the patient's last eligibility check: the
/// category percent the payer reported, the remaining deductible and the
/// remaining annual maximum. It knows nothing about fee schedules, frequency
/// limits, waiting periods or downgrades, and says so on every estimate.
/// </summary>
public static class BenefitSummaryEstimator
{
    /// <summary>
    /// CDT code to category, by the ranges most dental plans use. Crowns, buildups,
    /// prosthodontics and implants are major; fillings, endodontics, periodontics and
    /// oral surgery are basic. Adjunctive (D9xxx) codes vary too much between plans to guess.
    /// </summary>
    public static DentalBenefitCategory CategoryFor(string? cdtCode)
    {
        var code = cdtCode?.Trim().ToUpperInvariant();
        if (code is null || code.Length != 5 || code[0] != 'D' ||
            !int.TryParse(code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return DentalBenefitCategory.Unknown;

        return n switch
        {
            < 2000 => DentalBenefitCategory.Preventive,          // D0 diagnostic, D1 preventive
            >= 2700 and < 2800 => DentalBenefitCategory.Major,   // crowns
            >= 2950 and < 2960 => DentalBenefitCategory.Major,   // buildups, posts and cores
            < 5000 => DentalBenefitCategory.Basic,               // D2 restorative, D3 endo, D4 perio
            < 7000 => DentalBenefitCategory.Major,               // D5 removable, D6 implants and fixed
            < 8000 => DentalBenefitCategory.Basic,               // D7 oral surgery
            < 9000 => DentalBenefitCategory.Orthodontics,        // D8
            _ => DentalBenefitCategory.Unknown
        };
    }

    public static TreatmentEstimateResult Estimate(
        TreatmentEstimateRequest request, DentalBenefitSummary summary, DateTimeOffset verifiedAt)
    {
        // Only what the payer says is still available is used. A plan total is not what's
        // left, so a missing remaining amount is unknown, never the total.
        var deductibleLeft = summary.DeductibleRemaining ?? 0m;
        var annualLeft = summary.AnnualMaximumRemaining;
        var orthoLeft = summary.OrthodonticLifetimeMaximumRemaining;
        var estimatedOrtho = false;
        var unestimated = 0;
        var cappedByMaximum = false;

        var lines = new List<TreatmentEstimateLine>();
        foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
        {
            var charge = line.ChargeAmount * line.Units;
            var category = CategoryFor(line.ProcedureCode);
            var coverage = category switch
            {
                DentalBenefitCategory.Preventive => summary.Preventive,
                DentalBenefitCategory.Basic => summary.Basic,
                DentalBenefitCategory.Major => summary.Major,
                DentalBenefitCategory.Orthodontics => summary.Orthodontics,
                _ => null
            };
            var explanations = new List<string>();

            if (coverage is null || (coverage.PlanPaysPercent is null && coverage.Covered != false))
            {
                unestimated++;
                explanations.Add(category == DentalBenefitCategory.Unknown
                    ? "This code isn't in a benefit category the estimate knows. Confirm coverage with the payer."
                    : $"The payer didn't report a {Name(category)} percentage. Confirm coverage with the payer.");
                lines.Add(Line(line, charge, 0m, 0m, null, "Not estimated", explanations));
                continue;
            }

            if (coverage.Covered == false)
            {
                explanations.Add($"The payer reports {Name(category)} care as not covered.");
                lines.Add(Line(line, charge, 0m, 0m, 0m, "Not covered", explanations));
                continue;
            }

            var percent = coverage.PlanPaysPercent!.Value;
            explanations.Add($"{Name(category, capitalize: true)}: plan pays {percent * 100m:0}%" +
                             (coverage.FromGeneralDentalLine ? " (from the plan's general dental line)." : "."));

            // Most plans waive the deductible for preventive and diagnostic care.
            var deductible = 0m;
            if (category != DentalBenefitCategory.Preventive && deductibleLeft > 0m)
            {
                deductible = Math.Min(deductibleLeft, charge);
                deductibleLeft -= deductible;
                explanations.Add($"{deductible:C2} applied to the deductible.");
            }

            var insurance = Math.Round((charge - deductible) * percent, 2, MidpointRounding.AwayFromZero);

            // Orthodontics draws on its lifetime maximum; everything else on the annual one.
            if (category == DentalBenefitCategory.Orthodontics)
            {
                estimatedOrtho = true;
                if (orthoLeft is { } left && insurance > left)
                {
                    insurance = Math.Max(left, 0m);
                    cappedByMaximum = true;
                    explanations.Add("Limited by the orthodontic lifetime maximum.");
                }
                if (orthoLeft is not null) orthoLeft -= insurance;
            }
            else if (annualLeft is { } left)
            {
                if (insurance > left)
                {
                    insurance = Math.Max(left, 0m);
                    cappedByMaximum = true;
                    explanations.Add("Limited by the remaining annual maximum.");
                }
                annualLeft -= insurance;
            }

            lines.Add(Line(line, charge, insurance, deductible, percent, "Estimated", explanations));
        }

        var warnings = new List<EstimateWarning>
        {
            new("ELIGIBILITY_BASED", $"Based on the eligibility check from {verifiedAt.LocalDateTime:d} and the practice's fees. " +
                "In-network fee schedules, frequency limits, waiting periods and downgrades aren't applied.")
        };
        if (summary.MissingFields.Count > 0)
            warnings.Add(new("MISSING_BENEFITS", "The payer didn't report: " + string.Join(", ", summary.MissingFields) + "."));
        var deductibleUnknown = summary.DeductibleRemaining is null;
        var annualUnknown = summary.AnnualMaximumRemaining is null;
        var orthoUnknown = estimatedOrtho && summary.OrthodonticLifetimeMaximumRemaining is null;
        if (deductibleUnknown)
            warnings.Add(new("DEDUCTIBLE_UNKNOWN", "The payer didn't report how much deductible is left, so none was applied" +
                (summary.Deductible is { } d ? $". Up to {d:C2} may still apply." : ".")));
        if (annualUnknown)
            warnings.Add(new("MAXIMUM_UNKNOWN", "The payer didn't report how much of the annual maximum is left, so the estimate isn't limited by it" +
                (summary.AnnualMaximum is { } m ? $". The plan's maximum is {m:C2}." : ".")));
        if (orthoUnknown)
            warnings.Add(new("ORTHO_MAXIMUM_UNKNOWN", "The payer didn't report how much of the orthodontic lifetime maximum is left, so the estimate isn't limited by it" +
                (summary.OrthodonticLifetimeMaximum is { } o ? $". The lifetime maximum is {o:C2}." : ".")));
        if (cappedByMaximum)
            warnings.Add(new("MAXIMUM_REACHED", "Part of this plan exceeds the remaining maximum, so the patient pays more."));

        return new TreatmentEstimateResult
        {
            Status = unestimated > 0 ? EstimateStatus.Partial : EstimateStatus.Completed,
            Authority = EstimateAuthority.EligibilityBenefits,
            Confidence = unestimated > 0 || !summary.IsComplete || deductibleUnknown || annualUnknown || orthoUnknown
                ? EstimateConfidence.Low
                : EstimateConfidence.Medium,
            TotalCharges = lines.Sum(l => l.ChargeAmount),
            EstimatedAllowed = lines.Sum(l => l.AllowedAmount),
            EstimatedInsurancePayment = lines.Sum(l => l.InsurancePayment),
            EstimatedPatientResponsibility = lines.Sum(l => l.PatientResponsibility),
            EstimatedContractAdjustment = 0m,
            Lines = lines,
            Warnings = warnings
        };
    }

    private static TreatmentEstimateLine Line(
        TreatmentEstimateRequestLine source, decimal charge, decimal insurance, decimal deductible,
        decimal? percent, string status, IReadOnlyList<string> explanations) => new()
    {
        LineId = source.LineId,
        LineNumber = source.LineNumber,
        ProcedureCode = source.ProcedureCode,
        ChargeAmount = charge,
        // No fee schedule: the practice's fee is the allowed amount.
        AllowedAmount = charge,
        InsurancePayment = insurance,
        PatientResponsibility = charge - insurance,
        ContractAdjustment = 0m,
        Deductible = deductible,
        BenefitPercentage = percent,
        Status = status,
        Explanations = explanations
    };

    private static string Name(DentalBenefitCategory category, bool capitalize = false)
    {
        var name = category switch
        {
            DentalBenefitCategory.Preventive => "preventive",
            DentalBenefitCategory.Basic => "basic",
            DentalBenefitCategory.Major => "major",
            DentalBenefitCategory.Orthodontics => "orthodontic",
            _ => "this"
        };
        return capitalize ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }
}

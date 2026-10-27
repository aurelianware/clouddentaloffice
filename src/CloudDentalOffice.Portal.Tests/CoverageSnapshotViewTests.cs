using CloudDentalOffice.Portal.Components;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>How the schedule and chart word a patient's coverage.</summary>
public sealed class CoverageSnapshotViewTests
{
    [Fact]
    public void Benefit_line_reads_like_the_front_desk_says_it()
    {
        var benefits = new DentalBenefitSummary
        {
            Preventive = new CategoryCoverage(1m, true, false, []),
            Basic = new CategoryCoverage(0.8m, true, true, []),
            Major = new CategoryCoverage(null, false, false, []),
            Orthodontics = CategoryCoverage.NotReported,
            AnnualMaximumRemaining = 1000m,
            DeductibleRemaining = 50m
        };

        var line = CoverageSnapshotView.BenefitLine(benefits);

        Assert.StartsWith("Plan pays 100/80/0 · ", line);
        Assert.Contains("max left", line);
        Assert.Contains("deductible left", line);
    }

    [Fact]
    public void Unreported_percent_and_amounts_are_not_guessed()
    {
        var benefits = new DentalBenefitSummary
        {
            Preventive = new CategoryCoverage(1m, true, false, []),
            Basic = CategoryCoverage.NotReported,
            Major = new CategoryCoverage(0.5m, true, false, []),
            Orthodontics = CategoryCoverage.NotReported,
            AnnualMaximum = 1500m
        };

        Assert.Equal("Plan pays 100/?/50", CoverageSnapshotView.BenefitLine(benefits));
    }

    [Fact]
    public void Headline_names_the_last_check_and_its_date()
    {
        var at = new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero);

        Assert.StartsWith("Verified ", CoverageSnapshotView.Headline(Snapshot(EligibilityVerificationState.Verified, at)));
        Assert.StartsWith("No dental coverage ", CoverageSnapshotView.Headline(Snapshot(EligibilityVerificationState.NotDental, at)));
        Assert.Equal("Not checked", CoverageSnapshotView.Headline(Snapshot(null, null)));
    }

    private static CoverageSnapshot Snapshot(EligibilityVerificationState? state, DateTimeOffset? at) =>
        new(101, 501, "Delta Dental Arizona", state, at, null);
}

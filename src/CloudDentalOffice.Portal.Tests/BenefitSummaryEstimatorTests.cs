using CloudDentalOffice.Portal.Components;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>Treatment-plan estimates calculated from the patient's saved eligibility benefits.</summary>
public sealed class BenefitSummaryEstimatorTests
{
    private static readonly DateTimeOffset VerifiedAt = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);

    // 100/80/50, $50 deductible, $1,500 annual maximum with $1,000 left.
    private static DentalBenefitSummary Plan(
        decimal? deductibleRemaining = 50m, decimal? annualRemaining = 1000m, CategoryCoverage? major = null) => new()
    {
        Preventive = new CategoryCoverage(1m, true, false, []),
        Basic = new CategoryCoverage(0.8m, true, false, []),
        Major = major ?? new CategoryCoverage(0.5m, true, false, []),
        Orthodontics = new CategoryCoverage(null, false, false, []),
        Deductible = 50m,
        DeductibleRemaining = deductibleRemaining,
        AnnualMaximum = 1500m,
        AnnualMaximumRemaining = annualRemaining
    };

    [Theory]
    [InlineData("D0120", DentalBenefitCategory.Preventive)]
    [InlineData("D1110", DentalBenefitCategory.Preventive)]
    [InlineData("D2391", DentalBenefitCategory.Basic)]
    [InlineData("D2740", DentalBenefitCategory.Major)]
    [InlineData("D2950", DentalBenefitCategory.Major)]
    [InlineData("D3330", DentalBenefitCategory.Basic)]
    [InlineData("D4341", DentalBenefitCategory.Basic)]
    [InlineData("D5110", DentalBenefitCategory.Major)]
    [InlineData("D6010", DentalBenefitCategory.Major)]
    [InlineData("D7140", DentalBenefitCategory.Basic)]
    [InlineData("D8080", DentalBenefitCategory.Orthodontics)]
    [InlineData("D9110", DentalBenefitCategory.Unknown)]
    [InlineData("d2391 ", DentalBenefitCategory.Basic)]
    [InlineData("99213", DentalBenefitCategory.Unknown)]
    public void Cdt_codes_fall_into_the_usual_categories(string code, DentalBenefitCategory expected) =>
        Assert.Equal(expected, BenefitSummaryEstimator.CategoryFor(code));

    [Fact]
    public void Each_line_uses_its_category_percent_and_the_deductible_skips_preventive_care()
    {
        var result = BenefitSummaryEstimator.Estimate(Request(("D1110", 120m), ("D2391", 200m), ("D2740", 1200m)), Plan(annualRemaining: null), VerifiedAt);

        var cleaning = result.Lines[0];
        Assert.Equal(120m, cleaning.InsurancePayment);
        Assert.Equal(0m, cleaning.Deductible);

        // Deductible comes off the first line it applies to: (200 − 50) × 80%.
        var filling = result.Lines[1];
        Assert.Equal(50m, filling.Deductible);
        Assert.Equal(120m, filling.InsurancePayment);
        Assert.Equal(80m, filling.PatientResponsibility);

        var crown = result.Lines[2];
        Assert.Equal(0m, crown.Deductible);
        Assert.Equal(600m, crown.InsurancePayment);

        Assert.Equal(840m, result.EstimatedInsurancePayment);
        Assert.Equal(680m, result.EstimatedPatientResponsibility);
        Assert.Equal(EstimateAuthority.EligibilityBenefits, result.Authority);
        Assert.Equal(EstimateStatus.Completed, result.Status);
    }

    [Fact]
    public void Insurance_stops_at_the_remaining_annual_maximum()
    {
        var result = BenefitSummaryEstimator.Estimate(Request(("D2740", 1200m), ("D2740", 1200m)), Plan(deductibleRemaining: 0m, annualRemaining: 700m), VerifiedAt);

        Assert.Equal(600m, result.Lines[0].InsurancePayment);
        Assert.Equal(100m, result.Lines[1].InsurancePayment);
        Assert.Equal(700m, result.EstimatedInsurancePayment);
        Assert.Contains(result.Warnings, w => w.Code == "MAXIMUM_REACHED");
    }

    [Fact]
    public void Excluded_and_unknown_lines_leave_the_whole_fee_with_the_patient()
    {
        var result = BenefitSummaryEstimator.Estimate(
            Request(("D2740", 1200m), ("D9110", 90m), ("D8080", 5000m)),
            Plan(major: new CategoryCoverage(null, false, false, [])), VerifiedAt);

        Assert.Equal("Not covered", result.Lines[0].Status);
        Assert.Equal(1200m, result.Lines[0].PatientResponsibility);
        Assert.Equal("Not estimated", result.Lines[1].Status);
        Assert.Equal(90m, result.Lines[1].PatientResponsibility);
        Assert.Equal("Not covered", result.Lines[2].Status);
        Assert.Equal(0m, result.EstimatedInsurancePayment);
        Assert.Equal(EstimateStatus.Partial, result.Status);
        Assert.Equal(EstimateConfidence.Low, result.Confidence);
    }

    [Fact]
    public void Plan_totals_are_never_used_as_the_amount_left()
    {
        // $1,500 maximum and $50 deductible reported, but not how much of either is left.
        var summary = Plan(deductibleRemaining: null, annualRemaining: null);

        var result = BenefitSummaryEstimator.Estimate(Request(("D2391", 200m), ("D2740", 4000m)), summary, VerifiedAt);

        Assert.Equal(0m, result.Lines[0].Deductible);
        Assert.Equal(160m, result.Lines[0].InsurancePayment);
        Assert.Equal(2000m, result.Lines[1].InsurancePayment);
        Assert.Equal(EstimateConfidence.Low, result.Confidence);
        Assert.Contains(result.Warnings, w => w.Code == "DEDUCTIBLE_UNKNOWN" && w.Message.Contains("50"));
        Assert.Contains(result.Warnings, w => w.Code == "MAXIMUM_UNKNOWN" && w.Message.Contains("1,500"));
    }

    [Fact]
    public void Missing_benefits_are_named_on_the_estimate()
    {
        var summary = Plan() with { MissingFields = ["Deductible"], Deductible = null, DeductibleRemaining = null };

        var result = BenefitSummaryEstimator.Estimate(Request(("D2391", 200m)), summary, VerifiedAt);

        Assert.Equal(160m, result.EstimatedInsurancePayment);
        Assert.Equal(EstimateConfidence.Low, result.Confidence);
        Assert.Contains(result.Warnings, w => w.Code == "MISSING_BENEFITS" && w.Message.Contains("Deductible"));
        Assert.Contains(result.Warnings, w => w.Code == "DEDUCTIBLE_UNKNOWN");
        Assert.Contains(result.Warnings, w => w.Code == "ELIGIBILITY_BASED");
    }

    [Fact]
    public void Payers_mapped_to_a_benefit_plan_keep_using_the_estimate_service()
    {
        var options = new CloudHealthOfficeOptions { Enabled = true, BaseUrl = "https://cho.example" };
        options.BenefitPlanMappings["86027"] = Guid.NewGuid();
        PatientInsurance Coverage(string payerId) => new() { MemberId = "M1", InsurancePlan = new InsurancePlan { PayerId = payerId } };

        Assert.True(TreatmentPlanInsuranceEstimate.UsesEstimateService(options, Coverage("86027")));
        Assert.False(TreatmentPlanInsuranceEstimate.UsesEstimateService(options, Coverage("60054")));
        Assert.False(TreatmentPlanInsuranceEstimate.UsesEstimateService(new CloudHealthOfficeOptions(), Coverage("86027")));
    }

    private static TreatmentEstimateRequest Request(params (string Code, decimal Fee)[] lines) => new()
    {
        TenantId = "tenant-a",
        TreatmentPlanId = "7",
        PatientId = "101",
        MemberId = "MBR123",
        BenefitPlanId = Guid.Empty,
        RenderingProviderNpi = string.Empty,
        ServiceDate = new DateOnly(2026, 10, 5),
        Lines = lines.Select((l, i) => new TreatmentEstimateRequestLine
        {
            LineId = $"draft-{i + 1}", LineNumber = i + 1, ProcedureCode = l.Code, ChargeAmount = l.Fee
        }).ToList()
    };
}

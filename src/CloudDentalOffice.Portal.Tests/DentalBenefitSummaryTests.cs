using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Portal.Components;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Stedi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// The preventive/basic/major breakdown staff and estimates read from an
/// eligibility answer, and the detailed inquiry that asks the payer for it.
/// Synthetic responses shaped like Stedi's.
/// </summary>
public sealed class DentalBenefitSummaryTests
{
    // A typical 100/80/50 PPO: category coinsurance lines carry the patient's share.
    private const string FullDentalPlan = """
        {
          "planInformation": { "groupDescription": "Dental PPO" },
          "planStatus": [ { "statusCode": "1", "status": "Active Coverage" } ],
          "benefitsInformation": [
            { "code": "1", "name": "Active Coverage", "serviceTypeCodes": ["35"] },
            { "code": "C", "name": "Deductible", "serviceTypeCodes": ["35"], "coverageLevelCode": "IND", "timeQualifier": "Calendar Year", "benefitAmount": "50" },
            { "code": "F", "name": "Limitations", "serviceTypeCodes": ["35"], "coverageLevelCode": "IND", "timeQualifier": "Calendar Year", "benefitAmount": "1500" },
            { "code": "A", "name": "Co-Insurance", "serviceTypeCodes": ["41", "23"], "benefitPercent": "0",
              "additionalInformation": [ { "description": "Prophylaxis 2 per calendar year" } ] },
            { "code": "A", "name": "Co-Insurance", "serviceTypeCodes": ["25", "26", "24"], "benefitPercent": "0.2" },
            { "code": "A", "name": "Co-Insurance", "serviceTypeCodes": ["40"], "benefitPercent": "0.5" },
            { "code": "A", "name": "Co-Insurance", "serviceTypeCodes": ["39", "36", "27"], "benefitPercent": "0.5" },
            { "code": "A", "name": "Co-Insurance", "serviceTypeCodes": ["38"], "benefitPercent": "0.5" },
            { "code": "F", "name": "Limitations", "serviceTypeCodes": ["38"], "timeQualifier": "Lifetime", "benefitAmount": "1000" },
            { "code": "F", "name": "Limitations", "serviceTypeCodes": ["38"], "timeQualifier": "Lifetime Remaining", "benefitAmount": "750" }
          ]
        }
        """;

    [Fact]
    public void Detailed_inquiry_asks_for_every_code_a_category_reads()
    {
        var categories = DentalServiceTypes.Preventive.Concat(DentalServiceTypes.Basic)
            .Concat(DentalServiceTypes.Major).Concat(DentalServiceTypes.Orthodontics).ToList();

        Assert.Equal(DentalServiceTypes.DentalCare, DentalServiceTypes.DetailedInquiry[0]);
        Assert.Equal(categories.Append(DentalServiceTypes.DentalCare).OrderBy(c => c), DentalServiceTypes.DetailedInquiry.OrderBy(c => c));
        Assert.Equal(DentalServiceTypes.DetailedInquiry.Count, DentalServiceTypes.DetailedInquiry.Distinct().Count());
    }

    [Fact]
    public async Task Category_coinsurance_becomes_what_the_plan_pays()
    {
        var summary = (await Check(FullDentalPlan)).BenefitSummary!;

        Assert.Equal(1.00m, summary.Preventive.PlanPaysPercent);
        // Restorative leads basic, so its 80% wins over oral surgery's 50%.
        Assert.Equal(0.80m, summary.Basic.PlanPaysPercent);
        Assert.Equal(0.50m, summary.Major.PlanPaysPercent);
        Assert.Equal(0.50m, summary.Orthodontics.PlanPaysPercent);
        Assert.False(summary.Basic.FromGeneralDentalLine);
        Assert.Equal(1500m, summary.AnnualMaximum);
        Assert.Equal(50m, summary.Deductible);
        Assert.Equal(1000m, summary.OrthodonticLifetimeMaximum);
        Assert.Equal(750m, summary.OrthodonticLifetimeMaximumRemaining);
        Assert.Equal(["Prophylaxis 2 per calendar year"], summary.Preventive.Notes);
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public void Missing_categories_fall_back_to_the_general_dental_line()
    {
        var summary = Map(
            new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "35" },
            new ChoBenefit { BenefitCode = "A", ServiceTypeCode = "35", CoinsurancePercent = 0.2m },
            new ChoBenefit { BenefitCode = "A", ServiceTypeCode = "41", CoinsurancePercent = 0m },
            new ChoBenefit { BenefitCode = "I", ServiceTypeCode = "39" }).BenefitSummary!;

        Assert.Equal(1.00m, summary.Preventive.PlanPaysPercent);
        Assert.False(summary.Preventive.FromGeneralDentalLine);
        Assert.Equal(0.80m, summary.Basic.PlanPaysPercent);
        Assert.True(summary.Basic.FromGeneralDentalLine);
        // An excluded category never borrows the general line.
        Assert.False(summary.Major.Covered);
        Assert.Null(summary.Major.PlanPaysPercent);
        // Nor does orthodontics.
        Assert.Null(summary.Orthodontics.PlanPaysPercent);
    }

    [Fact]
    public void Out_of_network_percent_is_used_only_without_an_in_network_one()
    {
        var summary = Map(
            new ChoBenefit { BenefitCode = "A", ServiceTypeCode = "25", CoinsurancePercent = 0.5m, InNetwork = false },
            new ChoBenefit { BenefitCode = "A", ServiceTypeCode = "25", CoinsurancePercent = 0.2m }).BenefitSummary!;

        Assert.Equal(0.80m, summary.Basic.PlanPaysPercent);
    }

    [Fact]
    public void Answer_without_maximum_deductible_or_percents_lists_what_to_confirm()
    {
        var summary = Map(new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "35" }).BenefitSummary!;

        Assert.False(summary.IsComplete);
        Assert.Equal(["Annual maximum", "Deductible", "Preventive %", "Basic %"], summary.MissingFields);
    }

    [Fact]
    public void Excluded_basic_care_is_not_reported_as_missing()
    {
        var summary = Map(
            new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", TimePeriod = "Calendar Year", Amount = 1000m },
            new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", TimePeriod = "Calendar Year", Amount = 50m },
            new ChoBenefit { BenefitCode = "A", ServiceTypeCode = "41", CoinsurancePercent = 0m },
            new ChoBenefit { BenefitCode = "I", ServiceTypeCode = "25" }).BenefitSummary!;

        Assert.True(summary.IsComplete);
    }

    [Fact]
    public async Task No_summary_for_a_plan_that_does_not_cover_dental()
    {
        var result = await Check("""
            {
              "planStatus": [ { "statusCode": "1" } ],
              "benefitsInformation": [
                { "code": "1", "serviceTypeCodes": ["30"] },
                { "code": "I", "serviceTypeCodes": ["35"] }
              ]
            }
            """);

        Assert.True(result.DentalCareNotCovered);
        Assert.Null(result.BenefitSummary);
    }

    [Fact]
    public async Task Summary_is_kept_with_the_stored_result()
    {
        var result = await Check(FullDentalPlan);

        var json = JsonSerializer.Serialize(result, StoredJson);
        var restored = JsonSerializer.Deserialize<EligibilityResult>(json, StoredJson)!;

        Assert.Equal(0.80m, restored.BenefitSummary!.Basic.PlanPaysPercent);
        Assert.True(restored.BenefitSummary.IsComplete);
    }

    [Fact]
    public void Staff_read_category_coverage_in_plain_words()
    {
        Assert.Equal("Basic 80%", EligibilityResultView.CategoryText("Basic", new CategoryCoverage(0.8m, true, false, [])));
        Assert.Equal("Basic 80%*", EligibilityResultView.CategoryText("Basic", new CategoryCoverage(0.8m, true, true, [])));
        Assert.Equal("Major not covered", EligibilityResultView.CategoryText("Major", new CategoryCoverage(null, false, false, [])));
        Assert.Equal("Major not reported", EligibilityResultView.CategoryText("Major", CategoryCoverage.NotReported));
    }

    [Fact]
    public async Task Plain_inquiry_is_sent_unless_detailed_benefits_are_turned_on()
    {
        var handler = new Recording(_ => (HttpStatusCode.OK, FullDentalPlan));

        await Check(handler, detailed: false);

        Assert.Equal(["35"], Assert.Single(handler.ServiceTypeCodes));
    }

    [Fact]
    public async Task Detailed_inquiry_asks_for_each_category_when_turned_on()
    {
        var handler = new Recording(_ => (HttpStatusCode.OK, FullDentalPlan));

        var result = await Check(handler, detailed: true);

        Assert.Equal(DentalServiceTypes.DetailedInquiry, Assert.Single(handler.ServiceTypeCodes));
        Assert.NotNull(result.BenefitSummary);
    }

    [Fact]
    public async Task Payer_that_rejects_a_detailed_inquiry_is_asked_about_dental_care_alone()
    {
        var handler = new Recording(codes => codes.Count > 1
            ? (HttpStatusCode.BadRequest, """{ "message": "Too many service type codes" }""")
            : (HttpStatusCode.OK, FullDentalPlan));

        var result = await Check(handler, detailed: true);

        Assert.Equal(2, handler.ServiceTypeCodes.Count);
        Assert.Equal(["35"], handler.ServiceTypeCodes[1]);
        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
    }

    [Fact]
    public async Task A_bad_request_still_fails_after_the_single_retry()
    {
        var handler = new Recording(_ => (HttpStatusCode.BadRequest, "{}"));

        await Assert.ThrowsAsync<TreatmentEstimateValidationException>(() => Check(handler, detailed: true));
        Assert.Equal(2, handler.ServiceTypeCodes.Count);
    }

    // Same settings EligibilityVerificationService stores ResultJson with.
    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static EligibilityResult Map(params ChoBenefit[] benefits) =>
        CloudHealthOfficeEligibilityMapper.ToResult(new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits = benefits.ToList(),
            CheckedAtUtc = DateTimeOffset.UtcNow
        }, "corr-1");

    private static Task<EligibilityResult> Check(string stediJson) =>
        Check(new Recording(_ => (HttpStatusCode.OK, stediJson)), detailed: false);

    private static async Task<EligibilityResult> Check(Recording handler, bool detailed)
    {
        var client = new StediEligibilityClient(
            new HttpClient(new StediCredentialHandler(new FixedCredentials()) { InnerHandler = handler }),
            Options.Create(new StediOptions { RequestDetailedDentalBenefits = detailed }),
            NullLogger<StediEligibilityClient>.Instance);
        return await client.CheckAsync(new NormalizedEligibilityRequest
        {
            TenantId = "practice-a",
            PayerId = "60054",
            MemberId = "MBR123",
            SubscriberFirstName = "Quinn",
            SubscriberLastName = "Harlow",
            SubscriberDateOfBirth = new DateOnly(1990, 3, 14),
            ProviderNpi = "1999999984",
            ProviderFirstName = "Dana",
            ProviderLastName = "Dentist",
            ServiceDate = DateOnly.FromDateTime(DateTime.Today)
        });
    }

    private sealed class FixedCredentials : IStediCredentialProvider
    {
        public Task<StediCredential> GetAsync(string tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StediCredential(tenantId, ClearinghouseConnectionMode.Integrated, "test-key"));

        public void Invalidate(string tenantId) { }
    }

    /// <summary>Answers each request from its service type codes and records them.</summary>
    private sealed class Recording(Func<IReadOnlyList<string>, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        public List<IReadOnlyList<string>> ServiceTypeCodes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var codes = body.RootElement.GetProperty("encounter").GetProperty("serviceTypeCodes")
                .EnumerateArray().Select(c => c.GetString()!).ToList();
            ServiceTypeCodes.Add(codes);
            var (status, text) = respond(codes);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }
}

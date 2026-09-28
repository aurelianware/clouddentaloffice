using System.Net;
using System.Text;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Stedi;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// An active medical plan that doesn't cover dental care must not read as plain
/// "Active coverage" to a dental office. Synthetic responses shaped like Stedi's.
/// </summary>
public sealed class EligibilityCoverageScopeTests
{
    // Active medical plan: active health coverage (30), an informational line,
    // and dental care (35) reported as Non-Covered.
    private const string MedicalOnlyStediResponse = """
        {
          "planInformation": { "groupDescription": "HSA Open Access POS II" },
          "planStatus": [ { "statusCode": "1", "status": "Active Coverage" } ],
          "benefitsInformation": [
            { "code": "1", "name": "Active Coverage", "serviceTypeCodes": ["30"], "serviceTypes": ["Health Benefit Plan Coverage"] },
            { "code": "W", "name": "Other Source of Data" },
            { "code": "I", "name": "Non-Covered", "serviceTypeCodes": ["35"], "serviceTypes": ["Dental Care"] }
          ]
        }
        """;

    [Fact]
    public async Task Active_plan_that_does_not_cover_dental_is_flagged()
    {
        var result = await DirectCheck(MedicalOnlyStediResponse);

        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
        Assert.True(result.DentalCareNotCovered);
    }

    [Fact]
    public async Task Benefit_lines_are_labelled_by_what_they_mean()
    {
        var result = await DirectCheck(MedicalOnlyStediResponse);

        var active = result.Benefits.Single(b => b.ServiceTypeCode == "30");
        Assert.Equal(CoverageStatus.Active, active.Status);
        Assert.Equal("Active coverage · Health Benefit Plan Coverage", active.Description);

        var info = result.Benefits.Single(b => b.Description == "Other Source of Data");
        Assert.Equal(CoverageStatus.Unknown, info.Status);

        var dental = result.Benefits.Single(b => b.ServiceTypeCode == "35");
        Assert.Equal(CoverageStatus.Inactive, dental.Status);
        Assert.Equal("Not covered · Dental Care", dental.Description);
    }

    [Fact]
    public async Task Service_label_falls_back_to_the_service_type_code_when_names_are_omitted()
    {
        // serviceTypes omitted; "name" is the benefit line's name, not the service.
        var result = await DirectCheck("""
            {
              "planStatus": [ { "statusCode": "1" } ],
              "benefitsInformation": [
                { "code": "1", "name": "Active Coverage", "serviceTypeCodes": ["35"] },
                { "code": "I", "name": "Non-Covered", "serviceTypeCodes": ["38"] },
                { "code": "1", "name": "Active Coverage", "serviceTypeCodes": ["ZZ"] }
              ]
            }
            """);

        Assert.Equal("Active coverage · Dental Care", result.Benefits.Single(b => b.ServiceTypeCode == "35").Description);
        Assert.Equal("Not covered · Orthodontics", result.Benefits.Single(b => b.ServiceTypeCode == "38").Description);
        // Unknown code and no name: no borrowed or duplicated label.
        Assert.Equal("Active coverage", result.Benefits.Single(b => b.ServiceTypeCode == "ZZ").Description);
        Assert.False(result.DentalCareNotCovered);
    }

    [Fact]
    public void Dental_plan_with_benefits_is_not_flagged()
    {
        var result = Map(
            new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "35" },
            new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", TimePeriod = "Calendar Year", Amount = 50m });

        Assert.False(result.DentalCareNotCovered);
        Assert.All(result.Benefits, b => Assert.Equal(CoverageStatus.Active, b.Status));
    }

    [Fact]
    public void Non_covered_line_without_a_service_type_counts_when_nothing_dental_is_covered()
    {
        var result = Map(
            new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "30" },
            new ChoBenefit { BenefitCode = "I" });

        Assert.True(result.DentalCareNotCovered);
    }

    [Fact]
    public void Non_covered_line_without_a_service_type_is_ignored_when_dental_is_covered()
    {
        var result = Map(
            new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "35" },
            new ChoBenefit { BenefitCode = "I" });

        Assert.False(result.DentalCareNotCovered);
    }

    [Fact]
    public void Non_covered_non_dental_service_does_not_flag_dental()
    {
        var result = Map(
            new ChoBenefit { BenefitCode = "1", ServiceTypeCode = "35" },
            new ChoBenefit { BenefitCode = "I", ServiceTypeCode = "38" });

        Assert.False(result.DentalCareNotCovered);
        Assert.Equal(CoverageStatus.Inactive, result.Benefits.Single(b => b.ServiceTypeCode == "38").Status);
    }

    private static EligibilityResult Map(params ChoBenefit[] benefits) =>
        CloudHealthOfficeEligibilityMapper.ToResult(new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits = benefits.ToList(),
            CheckedAtUtc = DateTimeOffset.UtcNow
        }, "corr-1");

    private static async Task<EligibilityResult> DirectCheck(string stediJson)
    {
        var client = new StediEligibilityClient(
            new HttpClient(new StediCredentialHandler(new FixedCredentials()) { InnerHandler = new Respond(stediJson) }),
            Options.Create(new StediOptions()), NullLogger<StediEligibilityClient>.Instance);
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

    private sealed class Respond(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

using System.Net;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public sealed class InsuranceEstimateServiceTests
{
    [Fact]
    public void MapsTreatmentPlanAndRetainsIdentityForDuplicateCdtCodes()
    {
        var plan = Plan();
        plan.PlannedProcedures =
        [
            Procedure(41, "D2392", 275m),
            Procedure(42, "D2392", 310m)
        ];

        var request = TreatmentEstimateMapper.Map(plan, Patient(), Insurance(), Provider(), new DateOnly(2026, 9, 1), "tenant-a", Mappings());

        Assert.Equal(2, request.Lines.Count);
        Assert.Equal("planned-41", request.Lines[0].LineId);
        Assert.Equal("planned-42", request.Lines[1].LineId);
        Assert.Equal([275m, 310m], request.Lines.Select(x => x.ChargeAmount));
        Assert.All(request.Lines, line => Assert.Equal("D2392", line.ProcedureCode));
    }

    [Fact]
    public void MissingInsuranceProducesActionableValidation() =>
        Assert.Contains("active insurance", Assert.Throws<TreatmentEstimateValidationException>(() =>
            TreatmentEstimateMapper.Map(Plan(), Patient(), null, Provider(), DateOnly.FromDateTime(DateTime.Today), "tenant-a", Mappings())).Message);

    [Fact]
    public void MissingMemberIdProducesActionableValidation()
    {
        var insurance = Insurance();
        insurance.MemberId = "";
        Assert.Contains("member ID", Assert.Throws<TreatmentEstimateValidationException>(() =>
            TreatmentEstimateMapper.Map(Plan(), Patient(), insurance, Provider(), DateOnly.FromDateTime(DateTime.Today), "tenant-a", Mappings())).Message);
    }

    [Fact]
    public void MissingProviderNpiProducesActionableValidation()
    {
        var provider = Provider();
        provider.NPI = "";
        Assert.Contains("NPI", Assert.Throws<TreatmentEstimateValidationException>(() =>
            TreatmentEstimateMapper.Map(Plan(), Patient(), Insurance(), provider, DateOnly.FromDateTime(DateTime.Today), "tenant-a", Mappings())).Message);
    }

    [Fact]
    public void MissingBenefitPlanMappingProducesActionableValidation() =>
        Assert.Contains("benefit plan", Assert.Throws<TreatmentEstimateValidationException>(() =>
            TreatmentEstimateMapper.Map(Plan(), Patient(), Insurance(), Provider(), DateOnly.FromDateTime(DateTime.Today),
                "tenant-a", new Dictionary<string, Guid>())).Message);

    [Fact]
    public async Task ApiSuccessMapsMoneyAndPropagatesTenant()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var service = Service(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessResponseJson(), System.Text.Encoding.UTF8, "application/json")
            });
        });

        var actual = await service.EstimateAsync(Request());

        Assert.Equal(1640m, actual.EstimatedInsurancePayment);
        Assert.Equal(605m, actual.EstimatedPatientResponsibility);
        Assert.Equal(168m, actual.Lines.Single().InsurancePayment);
        Assert.Equal("planned-41", actual.Lines.Single().LineId);
        Assert.Equal("tenant-a", captured!.Headers.GetValues("X-Tenant-Id").Single());
        Assert.Contains("\"benefitPlanId\":\"3f2504e0-4f89-41d3-9a0c-0305e82c3301\"", capturedBody);
        Assert.Contains("\"providerNpi\":\"1234567890\"", capturedBody);
        Assert.Contains("\"claimType\":\"Dental\"", capturedBody);
        Assert.Contains("\"codeType\":\"CDT\"", capturedBody);
        Assert.Contains("\"toothSurface\":\"MO\"", capturedBody);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, typeof(TreatmentEstimateValidationException))]
    [InlineData(HttpStatusCode.UnprocessableEntity, typeof(TreatmentEstimateValidationException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(TreatmentEstimateUnavailableException))]
    public async Task ApiErrorsAreNormalized(HttpStatusCode status, Type exceptionType)
    {
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(status)));
        var exception = await Record.ExceptionAsync(() => service.EstimateAsync(Request()));
        Assert.IsType(exceptionType, exception);
    }

    [Fact]
    public async Task ApiTimeoutIsNormalized()
    {
        var service = Service(_ => throw new TaskCanceledException("timeout"));
        var exception = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => service.EstimateAsync(Request()));
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task CrossTenantRequestIsRejectedBeforeOutboundCall()
    {
        var calls = 0;
        var service = Service(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }, "tenant-b");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.EstimateAsync(Request()));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://evil.example/capture")]
    [InlineData("//evil.example/capture")]
    public async Task AbsoluteEstimatePathIsRejectedAsMisconfigured(string badPath)
    {
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(x => x.TenantId).Returns("tenant-a");
        var options = Options.Create(new CloudHealthOfficeOptions
        {
            Enabled = true,
            BaseUrl = "https://cloudhealthoffice.example",
            EstimatePath = badPath
        });
        var service = new CloudHealthOfficeInsuranceEstimateService(
            new HttpClient(), options, tenantProvider.Object,
            NullLogger<CloudHealthOfficeInsuranceEstimateService>.Instance);
        var ex = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => service.EstimateAsync(Request()));
        Assert.Contains("relative", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(EstimateConfidence.High, "High confidence")]
    [InlineData(EstimateConfidence.Medium, "Medium confidence")]
    [InlineData(EstimateConfidence.Low, "Low confidence")]
    public void ConfidenceDisplayIsProviderFriendly(EstimateConfidence confidence, string expected) =>
        Assert.Equal(expected, TreatmentEstimateDisplay.Confidence(confidence));

    [Fact]
    public void MappingDoesNotChangeClinicalOrClaimState()
    {
        var plan = Plan();
        var procedure = plan.PlannedProcedures.Single();
        procedure.Status = "Planned";
        procedure.ClaimProcedureId = null;
        plan.Status = "Draft";

        _ = TreatmentEstimateMapper.Map(plan, Patient(), Insurance(), Provider(), DateOnly.FromDateTime(DateTime.Today), "tenant-a", Mappings());

        Assert.Equal("Draft", plan.Status);
        Assert.Equal("Planned", procedure.Status);
        Assert.Null(procedure.ClaimProcedureId);
    }

    private static CloudHealthOfficeInsuranceEstimateService Service(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send, string tenant = "tenant-a")
    {
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(x => x.TenantId).Returns(tenant);
        var options = Options.Create(new CloudHealthOfficeOptions
        {
            Enabled = true,
            BaseUrl = "https://cloudhealthoffice.example",
            EstimatePath = "/api/v1/adjudication/estimate"
        });
        return new CloudHealthOfficeInsuranceEstimateService(new HttpClient(new Handler(send)), options,
            tenantProvider.Object, NullLogger<CloudHealthOfficeInsuranceEstimateService>.Instance);
    }

    private static TreatmentEstimateRequest Request() => TreatmentEstimateMapper.Map(
        Plan(), Patient(), Insurance(), Provider(), new DateOnly(2026, 9, 1), "tenant-a", Mappings());

    private static IReadOnlyDictionary<string, Guid> Mappings() =>
        new Dictionary<string, Guid> { ["PAYER1"] = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301") };

    private static TreatmentPlan Plan() => new()
    {
        TreatmentPlanId = 10, TenantId = "tenant-a", PatientId = 7, ProviderId = 3,
        PlannedProcedures = [Procedure(41, "D2392", 275m)]
    };

    private static PlannedProcedure Procedure(int id, string code, decimal fee) => new()
    {
        PlannedProcedureId = id, TenantId = "tenant-a", CDTCode = code,
        Description = "Resin-based composite", EstimatedFee = fee, Status = "Planned",
        ToothNumber = "12", Surface = "MO"
    };

    private static Patient Patient() => new() { PatientId = 7, TenantId = "tenant-a", FirstName = "Sam", LastName = "Patient" };
    private static Provider Provider() => new() { ProviderId = 3, TenantId = "tenant-a", NPI = "1234567890", FirstName = "Dana", LastName = "Dentist" };
    private static PatientInsurance Insurance()
    {
        var plan = new InsurancePlan { InsurancePlanId = 5, TenantId = "tenant-a", PayerId = "PAYER1", PayerName = "Dental Plan" };
        return new PatientInsurance { PatientInsuranceId = 8, TenantId = "tenant-a", PatientId = 7, InsurancePlanId = 5, MemberId = "MEMBER1", InsurancePlan = plan, IsActive = true };
    }

    private static string SuccessResponseJson() => """
        {
          "status":"estimated","authority":"Simulation",
          "totals":{"billedAmount":2800,"allowedAmount":2245,"contractualAdjustment":555,"payerResponsibility":1640,"patientResponsibility":605},
          "lines":[{"lineNumber":1,"procedureCode":"D2392","billedAmount":275,"allowedAmount":210,"contractualAdjustment":65,"payerResponsibility":168,"patientResponsibility":42,"deductibleAmount":0,"copayAmount":0,"coinsuranceAmount":42,"status":"payable","messages":[{"code":"COINSURANCE_APPLIED","severity":"Info","description":"Coinsurance applied."}]}],
          "warnings":[],"confidence":{"level":"High","reasons":["Benefit plan resolved"],"missingData":[]},
          "disclaimer":"Estimate only."
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

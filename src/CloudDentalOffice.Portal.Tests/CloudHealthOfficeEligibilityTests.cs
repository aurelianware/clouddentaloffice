using System.Net;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// CDO → CloudHealthOffice provider eligibility API. Synthetic data only; the
/// JSON fixtures follow the CHO contract (ProviderEligibilityContracts.cs).
/// </summary>
public sealed class CloudHealthOfficeEligibilityTests
{
    private const string Tenant = "third-set-smiles";
    private static readonly DateOnly ServiceDate = DateOnly.FromDateTime(DateTime.Today).AddDays(10);

    // ── Request building ────────────────────────────────────────────────────

    [Fact]
    public void Self_coverage_uses_the_patient_as_the_subscriber()
    {
        var request = EligibilityRequestBuilder.Build(Patient(), Insurance(), Provider(), ServiceDate, Tenant);

        Assert.Equal("PAYER1", request.PayerId);
        Assert.Equal("MBR123", request.MemberId);
        Assert.Equal("Quinn", request.SubscriberFirstName);
        Assert.Equal("Harlow", request.SubscriberLastName);
        Assert.Equal(new DateOnly(1958, 3, 14), request.SubscriberDateOfBirth);
        Assert.Null(request.Dependent);
        Assert.Equal("GRP9", request.GroupNumber);
        Assert.Equal("1999999984", request.ProviderNpi);
        Assert.Equal(ServiceDate, request.ServiceDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Self")]
    [InlineData("self")]
    [InlineData("18")]
    [InlineData("Subscriber")]
    [InlineData("Insured")]
    [InlineData("Employee")]
    [InlineData("Policyholder")]
    public void Self_relationship_values_mean_the_patient_is_the_subscriber(string? relationship)
    {
        var insurance = Insurance();
        insurance.RelationshipToSubscriber = relationship;
        // Stale subscriber fields must not override the patient's own record.
        insurance.SubscriberFirstName = "Someone";
        insurance.SubscriberLastName = "Else";

        var request = EligibilityRequestBuilder.Build(Patient(), insurance, Provider(), ServiceDate, Tenant);

        Assert.Null(request.Dependent);
        Assert.Equal("Quinn", request.SubscriberFirstName);
    }

    [Theory]
    [InlineData("Spouse", EligibilityRelationship.Spouse)]
    [InlineData("CHILD", EligibilityRelationship.Child)]
    [InlineData("19", EligibilityRelationship.Child)]
    [InlineData("01", EligibilityRelationship.Spouse)]
    [InlineData("Wife", EligibilityRelationship.Spouse)]
    [InlineData("Son", EligibilityRelationship.Child)]
    [InlineData("Other", EligibilityRelationship.Other)]
    [InlineData("Domestic partner", EligibilityRelationship.Other)]
    public void Dependent_is_sent_with_the_policyholder_as_subscriber(string stored, string expected)
    {
        var insurance = DependentInsurance(stored);

        var request = EligibilityRequestBuilder.Build(Patient(), insurance, Provider(), ServiceDate, Tenant);

        Assert.Equal("Rowan", request.SubscriberFirstName);
        Assert.Equal("Harlow", request.SubscriberLastName);
        Assert.Equal(new DateOnly(1955, 7, 2), request.SubscriberDateOfBirth);
        Assert.Equal("MBR123", request.MemberId);
        Assert.NotNull(request.Dependent);
        Assert.Equal("Quinn", request.Dependent!.FirstName);
        Assert.Equal(new DateOnly(1958, 3, 14), request.Dependent.DateOfBirth);
        Assert.Equal(expected, request.Dependent.Relationship);
    }

    [Fact]
    public void Dependent_without_policyholder_details_is_rejected_before_any_call()
    {
        var insurance = DependentInsurance("Spouse");
        insurance.SubscriberDateOfBirth = null;

        var error = Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(Patient(), insurance, Provider(), ServiceDate, Tenant));

        Assert.Contains("policyholder", error.Message);
    }

    [Fact]
    public void Missing_patient_date_of_birth_is_rejected()
    {
        var patient = Patient();
        patient.DateOfBirth = default;

        var error = Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(patient, Insurance(), Provider(), ServiceDate, Tenant));

        Assert.Contains("date of birth", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("12345678AB")]
    public void Provider_npi_must_be_ten_digits(string npi)
    {
        var provider = Provider();
        provider.NPI = npi;

        Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(Patient(), Insurance(), provider, ServiceDate, Tenant));
    }

    [Fact]
    public void Missing_member_id_or_payer_id_is_rejected()
    {
        var noMember = Insurance();
        noMember.MemberId = " ";
        Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(Patient(), noMember, Provider(), ServiceDate, Tenant));

        var noPayer = Insurance();
        noPayer.InsurancePlan.PayerId = "";
        Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(Patient(), noPayer, Provider(), ServiceDate, Tenant));
    }

    [Theory]
    [InlineData(-800)]
    [InlineData(400)]
    public void Service_date_outside_the_checkable_window_is_rejected(int daysFromToday)
    {
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(daysFromToday);

        var error = Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(Patient(), Insurance(), Provider(), date, Tenant));

        Assert.Contains("one year ahead", error.Message);
    }

    [Fact]
    public void Names_longer_than_the_payer_limit_are_rejected_before_any_call()
    {
        var patient = Patient();
        patient.LastName = new string('A', 61);

        var error = Assert.Throws<TreatmentEstimateValidationException>(() =>
            EligibilityRequestBuilder.Build(patient, Insurance(), Provider(), ServiceDate, Tenant));

        Assert.Contains("60 characters", error.Message);
    }

    // ── Wire request ────────────────────────────────────────────────────────

    [Fact]
    public async Task Sends_credential_tenant_and_the_CHO_request_shape()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var client = Client(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, ActiveResponseJson);
        });

        await client.CheckAsync(Request(dependent: true) with { CorrelationId = "abc123" });

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("https://provider-eligibility.internal.example/api/v1/eligibility/check", sent.RequestUri!.ToString());
        Assert.Equal("cdo-test-key", Assert.Single(sent.Headers.GetValues("X-Api-Key")));
        Assert.Equal(Tenant, Assert.Single(sent.Headers.GetValues("X-Tenant-ID")));

        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("PAYER1", json.GetProperty("payerId").GetString());
        Assert.Equal("1999999984", json.GetProperty("provider").GetProperty("npi").GetString());
        Assert.Equal("MBR123", json.GetProperty("subscriber").GetProperty("memberId").GetString());
        Assert.Equal("Rowan", json.GetProperty("subscriber").GetProperty("firstName").GetString());
        Assert.Equal("1955-07-02", json.GetProperty("subscriber").GetProperty("dateOfBirth").GetString());
        Assert.Equal("Quinn", json.GetProperty("patient").GetProperty("firstName").GetString());
        Assert.Equal("1958-03-14", json.GetProperty("patient").GetProperty("dateOfBirth").GetString());
        Assert.Equal("spouse", json.GetProperty("patient").GetProperty("relationshipToSubscriber").GetString());
        Assert.Equal("35", json.GetProperty("serviceTypeCode").GetString());
        Assert.Equal(ServiceDate.ToString("yyyy-MM-dd"), json.GetProperty("serviceDate").GetString());
        Assert.Equal("abc123", json.GetProperty("correlationId").GetString());
        Assert.False(json.TryGetProperty("tenantId", out _));
    }

    [Fact]
    public async Task Self_request_omits_the_patient()
    {
        string? body = null;
        var client = Client(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, ActiveResponseJson);
        });

        await client.CheckAsync(Request());

        var patient = JsonDocument.Parse(body!).RootElement.GetProperty("patient");
        Assert.Equal(JsonValueKind.Null, patient.ValueKind);
    }

    [Fact]
    public async Task Refuses_a_request_for_another_tenant()
    {
        var client = Client(_ => throw new InvalidOperationException("must not be called"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.CheckAsync(Request() with { TenantId = "other-practice" }));
    }

    [Fact]
    public async Task Not_configured_fails_without_calling()
    {
        var client = Client(_ => throw new InvalidOperationException("must not be called"), apiKey: null);

        Assert.False(client.IsConfigured);
        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));
        Assert.Contains("not configured", error.Message);
    }

    // ── Response mapping ────────────────────────────────────────────────────

    [Fact]
    public async Task Active_coverage_maps_plan_dates_maximum_and_deductible()
    {
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, ActiveResponseJson)));

        var result = await client.CheckAsync(Request());

        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
        Assert.Equal("Dental PPO Plus", result.PlanName);
        Assert.Equal(new DateOnly(2026, 1, 1), result.EffectiveDate);
        Assert.Null(result.TerminationDate);
        Assert.Equal(1500m, result.AnnualMaximum);
        Assert.Equal(1175m, result.AnnualMaximumRemaining);
        Assert.Equal(50m, result.Deductible);
        Assert.Equal(0m, result.DeductibleRemaining);
        Assert.Equal("CloudHealthOffice", result.Source);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 17, 0, 0, TimeSpan.Zero), result.VerifiedAt);
        Assert.Contains(result.Benefits, b => b.Coinsurance == 0.2m && b.ServiceTypeCode == "35");
        Assert.Contains("Frequency limits apply to cleanings.", result.Messages);
    }

    [Fact]
    public void Year_to_date_and_visit_lines_are_never_read_as_the_annual_amount()
    {
        var response = new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits =
            [
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", TimePeriod = "Year to Date", Amount = 325m },
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", TimePeriod = "Visit", Amount = 100m },
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", TimePeriod = "Calendar Year", Amount = 1500m },
                new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", TimePeriod = "Year to Date", Amount = 50m },
                new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", TimePeriod = "Service Year", Amount = 75m }
            ]
        };

        var result = CloudHealthOfficeEligibilityMapper.ToResult(response, "x");

        Assert.Equal(1500m, result.AnnualMaximum);
        Assert.Equal(75m, result.Deductible);
        Assert.Null(result.AnnualMaximumRemaining);
        Assert.DoesNotContain(result.Benefits, b => b.Limit == 325m);
    }

    [Fact]
    public void Explicit_annual_line_wins_over_a_line_without_a_period()
    {
        var response = new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits =
            [
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", Amount = 1000m },
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "35", TimePeriod = "Calendar Year", Amount = 1500m }
            ]
        };

        Assert.Equal(1500m, CloudHealthOfficeEligibilityMapper.ToResult(response, "x").AnnualMaximum);
    }

    [Fact]
    public async Task Empty_success_body_is_reported_as_temporary()
    {
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, "not json")));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains("incomplete", error.Message);
    }

    [Fact]
    public void Orthodontic_lifetime_maximum_is_not_read_as_the_annual_maximum()
    {
        var response = new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits =
            [
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "38", TimePeriod = "Lifetime", Amount = 1000m },
                new ChoBenefit { BenefitCode = "F", ServiceTypeCode = "38", TimePeriod = "Remaining", Amount = 1000m }
            ]
        };

        var result = CloudHealthOfficeEligibilityMapper.ToResult(response, "x");

        Assert.Null(result.AnnualMaximum);
        Assert.Null(result.AnnualMaximumRemaining);
    }

    [Fact]
    public void Prefers_in_network_individual_amounts()
    {
        var response = new ChoEligibilityResponse
        {
            CoverageStatus = "Active",
            Benefits =
            [
                new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", CoverageLevel = "FAM", TimePeriod = "Calendar Year", Amount = 150m },
                new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", CoverageLevel = "IND", TimePeriod = "Calendar Year", Amount = 100m, InNetwork = false },
                new ChoBenefit { BenefitCode = "C", ServiceTypeCode = "35", CoverageLevel = "IND", TimePeriod = "Calendar Year", Amount = 50m }
            ]
        };

        Assert.Equal(50m, CloudHealthOfficeEligibilityMapper.ToResult(response, "x").Deductible);
    }

    [Fact]
    public async Task Payer_rejection_is_an_answer_not_an_error()
    {
        const string rejected = """
            {"outcome":"Rejected","errorCategory":"None","message":"Subscriber not found.","eligible":false,
             "coverageStatus":"Inactive","benefits":[],"correlationId":"corr-2","checkedAtUtc":"2026-09-26T17:00:00+00:00"}
            """;
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, rejected)));

        var result = await client.CheckAsync(Request());

        Assert.Equal(CoverageStatus.Inactive, result.CoverageStatus);
        Assert.Contains("Subscriber not found.", result.Messages);
    }

    // ── Failures ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bad_request_names_the_fields_without_values()
    {
        const string invalid = """
            {"error":"The eligibility request is invalid.","fields":{"subscriber.dateOfBirth":"Date of birth is out of range.","provider.npi":"Provider NPI must be 10 digits."}}
            """;
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.BadRequest, invalid)));

        var error = await Assert.ThrowsAsync<TreatmentEstimateValidationException>(() => client.CheckAsync(Request()));

        Assert.Contains("policyholder date of birth", error.Message);
        Assert.Contains("provider NPI", error.Message);
        Assert.DoesNotContain("1958", error.Message);
    }

    [Theory]
    [InlineData("PayerNotFound", "payer ID")]
    [InlineData("EnrollmentRequired", "enrollment")]
    [InlineData("NotSupported", "phone")]
    [InlineData("PayerRejected", "The payer could not verify this coverage: x")]
    [InlineData("Validation", "The payer could not verify this coverage: x")]
    public async Task Unprocessable_explains_the_payer_problem(string category, string expected)
    {
        var body = $$"""{"outcome":"Failed","errorCategory":"{{category}}","message":"x","eligible":false,"coverageStatus":"Unknown"}""";
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.UnprocessableEntity, body)));

        var error = await Assert.ThrowsAsync<TreatmentEstimateValidationException>(() => client.CheckAsync(Request()));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Directory_loading_503_asks_staff_to_retry()
    {
        const string loading = """
            {"outcome":"Failed","errorCategory":"ReferenceDataUnavailable","message":"The payer directory is still loading. Retry shortly.","eligible":false,"coverageStatus":"Unknown"}
            """;
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, loading)));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains("Try again", error.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "not authorized")]
    [InlineData(HttpStatusCode.Forbidden, "not authorized")]
    [InlineData(HttpStatusCode.BadGateway, "not a problem with the patient")]
    [InlineData(HttpStatusCode.InternalServerError, "not a problem with the patient")]
    public async Task Server_side_failures_do_not_blame_the_patient_data(HttpStatusCode status, string expected)
    {
        var client = Client(_ => Task.FromResult(new HttpResponseMessage(status)));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Unreachable_service_is_reported_as_temporary()
    {
        var client = Client(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains("temporarily unavailable", error.Message);
    }

    [Fact]
    public async Task Timeout_is_reported_as_temporary()
    {
        var client = Client(_ => throw new TaskCanceledException("timeout"));

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains("timed out", error.Message);
    }

    [Theory]
    [InlineData("http://provider-eligibility.internal.example")]
    [InlineData("https://user:secret@provider-eligibility.internal.example")]
    [InlineData("https://provider-eligibility.internal.example/?route=other")]
    [InlineData("https://provider-eligibility.internal.example/#fragment")]
    public async Task Base_url_that_is_not_a_plain_https_origin_is_refused_before_any_call(string baseUrl)
    {
        var called = false;
        var client = Client(_ => { called = true; return Task.FromResult(Json(HttpStatusCode.OK, ActiveResponseJson)); },
            baseUrl: baseUrl);

        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => client.CheckAsync(Request()));

        Assert.Contains("misconfigured", error.Message);
        Assert.False(called);
    }

    [Fact]
    public async Task Logged_ids_cannot_forge_extra_log_lines()
    {
        var logger = new CapturingLogger();
        var client = Client(_ => Task.FromResult(Json(HttpStatusCode.OK, ActiveResponseJson)), logger: logger);
        var request = Request() with { PayerId = "PAYER1\r\nforged entry", CorrelationId = "corr\n2" };

        await client.CheckAsync(request);

        var entry = Assert.Single(logger.Messages);
        Assert.DoesNotContain('\n', entry);
        Assert.DoesNotContain('\r', entry);
        Assert.Contains("PAYER1forged entry", entry);
        Assert.Contains("corr2", entry);
    }

    // ── Adapter ─────────────────────────────────────────────────────────────

    [Fact]
    public void Adapter_offers_eligibility_only_when_configured()
    {
        var estimates = new Mock<IInsuranceEstimateService>().Object;

        var configured = new CloudHealthOfficeTradingPartnerAdapter(estimates, Client(_ => throw new InvalidOperationException()));
        var unconfigured = new CloudHealthOfficeTradingPartnerAdapter(estimates, Client(_ => throw new InvalidOperationException(), apiKey: null));

        Assert.True(configured.Capabilities.HasFlag(TradingPartnerCapability.Eligibility));
        Assert.True(configured.Capabilities.HasFlag(TradingPartnerCapability.PaymentEstimate));
        Assert.False(unconfigured.Capabilities.HasFlag(TradingPartnerCapability.Eligibility));
        Assert.True(unconfigured.Capabilities.HasFlag(TradingPartnerCapability.PaymentEstimate));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private const string ActiveResponseJson = """
        {
          "outcome":"Completed","errorCategory":"None","message":null,"eligible":true,"coverageStatus":"Active",
          "planName":"Dental PPO Plus","planId":"PPO-1","groupNumber":"GRP9","coverageStart":"2026-01-01","coverageEnd":null,
          "benefits":[
            {"benefitCode":"1","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"messages":[]},
            {"benefitCode":"F","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"timePeriod":"Calendar Year","amount":1500,"messages":[]},
            {"benefitCode":"F","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"timePeriod":"Remaining","amount":1175,"messages":[]},
            {"benefitCode":"C","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"timePeriod":"Calendar Year","amount":50,"messages":[]},
            {"benefitCode":"C","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"timePeriod":"Remaining","amount":0,"messages":[]},
            {"benefitCode":"A","serviceTypeCode":"35","serviceTypeName":"Dental Care","coverageLevel":"IND","inNetwork":true,"percent":0.2,"coinsurancePercent":0.2,"messages":["Frequency limits apply to cleanings."]}
          ],
          "correlationId":"corr-1","checkedAtUtc":"2026-09-26T17:00:00+00:00"
        }
        """;

    private static NormalizedEligibilityRequest Request(bool dependent = false) =>
        EligibilityRequestBuilder.Build(Patient(), dependent ? DependentInsurance("Spouse") : Insurance(), Provider(), ServiceDate, Tenant);

    private static CloudHealthOfficeEligibilityClient Client(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send, string? apiKey = "cdo-test-key",
        string baseUrl = "https://provider-eligibility.internal.example",
        ILogger<CloudHealthOfficeEligibilityClient>? logger = null)
    {
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(x => x.TenantId).Returns(Tenant);
        var options = Options.Create(new CloudHealthOfficeOptions
        {
            Eligibility = new CloudHealthOfficeEligibilityOptions
            {
                BaseUrl = baseUrl,
                ApiKey = apiKey
            }
        });
        return new CloudHealthOfficeEligibilityClient(new HttpClient(new Handler(send)), options,
            tenantProvider.Object, logger ?? NullLogger<CloudHealthOfficeEligibilityClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Patient Patient() => new()
    {
        PatientId = 7, TenantId = Tenant, FirstName = "Quinn", LastName = "Harlow", DateOfBirth = new DateTime(1958, 3, 14)
    };

    private static Provider Provider() => new()
    {
        ProviderId = 3, TenantId = Tenant, NPI = "1999999984", FirstName = "Dana", LastName = "Dentist"
    };

    private static PatientInsurance Insurance()
    {
        var plan = new InsurancePlan { InsurancePlanId = 5, TenantId = Tenant, PayerId = "PAYER1", PayerName = "Dental Plan" };
        return new PatientInsurance
        {
            PatientInsuranceId = 8, TenantId = Tenant, PatientId = 7, InsurancePlanId = 5, MemberId = " MBR123 ",
            GroupNumber = "GRP9", InsurancePlan = plan, IsActive = true, RelationshipToSubscriber = "Self"
        };
    }

    private static PatientInsurance DependentInsurance(string relationship)
    {
        var insurance = Insurance();
        insurance.RelationshipToSubscriber = relationship;
        insurance.SubscriberFirstName = "Rowan";
        insurance.SubscriberLastName = "Harlow";
        insurance.SubscriberDateOfBirth = new DateTime(1955, 7, 2);
        return insurance;
    }

    private sealed class CapturingLogger : ILogger<CloudHealthOfficeEligibilityClient>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}

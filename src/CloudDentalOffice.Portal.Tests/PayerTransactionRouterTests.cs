using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Options;
using Moq;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PayerTransactionRouterTests
{
    [Fact]
    public async Task EligibilityUsesConfiguredCapableAdapterAndRecordsMetadata()
    {
        var audit = new RecordingAudit();
        var adapter = new FakeEligibilityAdapter("Mock");
        var router = Router([adapter], Routes("Mock", ["CloudHealthOffice"]), audit);

        var result = await router.CheckEligibilityAsync(EligibilityRequest());

        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
        Assert.Equal("Mock", result.Source);
        Assert.Equal(1, adapter.CallCount);
        Assert.Single(audit.Records);
        Assert.Equal("Eligibility", audit.Records[0].TransactionType);
        Assert.DoesNotContain("MEMBER", audit.Records[0].CorrelationId);
    }

    [Fact]
    public async Task EligibilityRejectsAdapterWithoutCapability()
    {
        var router = Router([new FakeEstimateAdapter("Mock", Estimate())], Routes("Mock", ["Mock"]));
        var error = await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => router.CheckEligibilityAsync(EligibilityRequest()));
        Assert.Contains("does not support eligibility", error.Message);
    }

    [Fact]
    public async Task EstimateFallsBackFromUnavailablePayerToCloudHealthOffice()
    {
        var payer = new FakeEstimateAdapter("PayerAEOB", null, unavailable: true);
        var cho = new FakeEstimateAdapter("CloudHealthOffice", Estimate());
        var router = Router([payer, cho], Routes("Mock", ["PayerAEOB", "CloudHealthOffice"]));

        var result = await router.GetEstimateAsync(new PayerEstimateRoutingRequest { PayerId = "PAYER1", EstimateRequest = EstimateRequest() });

        Assert.Equal("CloudHealthOffice", result.AdapterType);
        Assert.Equal(2, result.Priority);
        Assert.Equal(1, payer.CallCount);
        Assert.Equal(1, cho.CallCount);
    }

    [Fact]
    public async Task EstimatePreservesAuthoritativeSourceWithoutMixingFallbackAmounts()
    {
        var authoritative = Estimate() with { Authority = EstimateAuthority.PayerAdjudication, EstimatedInsurancePayment = 700m };
        var payer = new FakeEstimateAdapter("DirectPayer", authoritative);
        var cho = new FakeEstimateAdapter("CloudHealthOffice", Estimate() with { EstimatedInsurancePayment = 500m });
        var router = Router([payer, cho], Routes("Mock", ["DirectPayer", "CloudHealthOffice"]));

        var result = await router.GetEstimateAsync(new PayerEstimateRoutingRequest { PayerId = "PAYER1", EstimateRequest = EstimateRequest() });

        Assert.Equal("DirectPayer", result.AdapterType);
        Assert.Equal(700m, result.Result.EstimatedInsurancePayment);
        Assert.Equal(0, cho.CallCount);
    }

    [Fact]
    public async Task CrossTenantRequestDoesNotCallAdapter()
    {
        var adapter = new FakeEligibilityAdapter("Mock");
        var router = Router([adapter], Routes("Mock", []), tenant: "tenant-b");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => router.CheckEligibilityAsync(EligibilityRequest()));
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task MissingRouteMakesNoExternalCall()
    {
        var adapter = new FakeEligibilityAdapter("Mock");
        var router = Router([adapter], new PayerConnectivityOptions());
        await Assert.ThrowsAsync<TreatmentEstimateUnavailableException>(() => router.CheckEligibilityAsync(EligibilityRequest()));
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task MockEligibilityIsNormalizedAndDoesNotTransmitExternally()
    {
        var adapter = new MockEligibilityTradingPartnerAdapter();
        var result = await adapter.CheckEligibilityAsync(EligibilityRequest());
        Assert.Equal(CoverageStatus.Active, result.CoverageStatus);
        Assert.NotNull(result.AnnualMaximumRemaining);
        Assert.Contains(result.Messages, x => x.Contains("No external transaction"));
    }

    private static PayerTransactionRouter Router(IEnumerable<ITradingPartnerAdapter> adapters, PayerConnectivityOptions options,
        RecordingAudit? audit = null, string tenant = "tenant-a")
    {
        var tenantProvider = new Mock<ITenantProvider>();
        tenantProvider.SetupGet(x => x.TenantId).Returns(tenant);
        return new PayerTransactionRouter(adapters, Options.Create(options), tenantProvider.Object, audit ?? new RecordingAudit());
    }

    private static PayerConnectivityOptions Routes(string eligibility, List<string> estimates) => new()
    {
        Payers = new Dictionary<string, PayerRouteOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["PAYER1"] = new() { Eligibility = eligibility, PaymentEstimate = estimates, ClaimSubmission = "Clearinghouse" }
        }
    };

    private static NormalizedEligibilityRequest EligibilityRequest() => new()
    {
        TenantId = "tenant-a", PayerId = "PAYER1", MemberId = "MEMBER1",
        ProviderNpi = "1234567890", ServiceDate = new DateOnly(2026, 9, 1)
    };

    private static TreatmentEstimateRequest EstimateRequest() => new()
    {
        TenantId = "tenant-a", TreatmentPlanId = "10", PatientId = "7", MemberId = "MEMBER1",
        BenefitPlanId = Guid.NewGuid(), RenderingProviderNpi = "1234567890", ServiceDate = new DateOnly(2026, 9, 1),
        Lines = [new TreatmentEstimateRequestLine { LineId = "planned-1", LineNumber = 1, ProcedureCode = "D0120", ChargeAmount = 75m }]
    };

    private static TreatmentEstimateResult Estimate() => new()
    {
        Status = EstimateStatus.Completed, Authority = EstimateAuthority.CloudHealthOfficeEstimate,
        Confidence = EstimateConfidence.High, TotalCharges = 1000m, EstimatedAllowed = 800m,
        EstimatedInsurancePayment = 600m, EstimatedPatientResponsibility = 200m, EstimatedContractAdjustment = 200m
    };

    private sealed class FakeEligibilityAdapter(string type) : IEligibilityTradingPartnerAdapter
    {
        public int CallCount { get; private set; }
        public string AdapterType => type;
        public TradingPartnerCapability Capabilities => TradingPartnerCapability.Eligibility;
        public Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new EligibilityResult { CorrelationId = "external-1", CoverageStatus = CoverageStatus.Active, Source = type, VerifiedAt = DateTimeOffset.UtcNow });
        }
    }

    private sealed class FakeEstimateAdapter(string type, TreatmentEstimateResult? result, bool unavailable = false) : IEstimateTradingPartnerAdapter
    {
        public int CallCount { get; private set; }
        public string AdapterType => type;
        public TradingPartnerCapability Capabilities => TradingPartnerCapability.PaymentEstimate;
        public Task<TreatmentEstimateResult?> GetEstimateAsync(TreatmentEstimateRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return unavailable
                ? throw new TreatmentEstimateUnavailableException("Timed out")
                : Task.FromResult(result);
        }
    }

    private sealed class RecordingAudit : ITransactionAuditSink
    {
        public List<TransactionAuditRecord> Records { get; } = [];
        public Task RecordAsync(TransactionAuditRecord record, CancellationToken cancellationToken = default) { Records.Add(record); return Task.CompletedTask; }
    }
}

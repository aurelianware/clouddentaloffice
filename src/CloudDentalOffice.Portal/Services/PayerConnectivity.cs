using System.Diagnostics;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public interface ITradingPartnerAdapter
{
    string AdapterType { get; }
    TradingPartnerCapability Capabilities { get; }
}

public interface IEligibilityTradingPartnerAdapter : ITradingPartnerAdapter
{
    Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default);
}

public interface IEstimateTradingPartnerAdapter : ITradingPartnerAdapter
{
    Task<TreatmentEstimateResult?> GetEstimateAsync(TreatmentEstimateRequest request, CancellationToken cancellationToken = default);
}

public interface IPayerTransactionRouter
{
    Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default);
    Task<RoutedTreatmentEstimate> GetEstimateAsync(PayerEstimateRoutingRequest request, CancellationToken cancellationToken = default);
}

public sealed class PayerConnectivityOptions
{
    public Dictionary<string, PayerRouteOptions> Payers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PayerRouteOptions
{
    public string? Eligibility { get; set; }
    public List<string> PaymentEstimate { get; set; } = [];
    public string? ClaimSubmission { get; set; }
}

public interface ITransactionAuditSink
{
    Task RecordAsync(TransactionAuditRecord record, CancellationToken cancellationToken = default);
}

public sealed class LoggingTransactionAuditSink(ILogger<LoggingTransactionAuditSink> logger) : ITransactionAuditSink
{
    public Task RecordAsync(TransactionAuditRecord record, CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "Payer transaction {CorrelationId} tenant {TenantId} payer {PayerId} adapter {AdapterType} type {TransactionType} success {Succeeded} status {ResponseStatus} elapsedMs {ElapsedMilliseconds}",
            record.CorrelationId, record.TenantId, record.PayerId, record.AdapterType, record.TransactionType,
            record.Succeeded, record.ResponseStatus, record.ElapsedMilliseconds);
        return Task.CompletedTask;
    }
}

public sealed class PayerTransactionRouter : IPayerTransactionRouter
{
    private readonly IReadOnlyDictionary<string, ITradingPartnerAdapter> _adapters;
    private readonly PayerConnectivityOptions _options;
    private readonly ITenantProvider _tenantProvider;
    private readonly ITransactionAuditSink _audit;

    public PayerTransactionRouter(IEnumerable<ITradingPartnerAdapter> adapters, IOptions<PayerConnectivityOptions> options,
        ITenantProvider tenantProvider, ITransactionAuditSink audit)
    {
        _adapters = adapters.ToDictionary(x => x.AdapterType, StringComparer.OrdinalIgnoreCase);
        _options = options.Value;
        _tenantProvider = tenantProvider;
        _audit = audit;
    }

    public async Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
    {
        ValidateTenant(request.TenantId);
        var routes = RouteFor(request.PayerId);
        if (string.IsNullOrWhiteSpace(routes.Eligibility))
            throw new TreatmentEstimateUnavailableException("Eligibility connectivity is not configured for this payer.");
        if (!_adapters.TryGetValue(routes.Eligibility, out var adapter) || adapter is not IEligibilityTradingPartnerAdapter eligibility ||
            !adapter.Capabilities.HasFlag(TradingPartnerCapability.Eligibility))
            throw new TreatmentEstimateUnavailableException($"The {routes.Eligibility} adapter does not support eligibility.");

        return await AuditAsync(request.TenantId, request.PayerId, adapter.AdapterType, "Eligibility", async () =>
            await eligibility.CheckEligibilityAsync(request, cancellationToken), result => result.CoverageStatus.ToString(), cancellationToken);
    }

    public async Task<RoutedTreatmentEstimate> GetEstimateAsync(PayerEstimateRoutingRequest request, CancellationToken cancellationToken = default)
    {
        ValidateTenant(request.EstimateRequest.TenantId);
        var routes = RouteFor(request.PayerId).PaymentEstimate;
        if (routes.Count == 0) throw new TreatmentEstimateUnavailableException("Payment estimates are not configured for this payer.");

        var failures = new List<string>();
        for (var index = 0; index < routes.Count; index++)
        {
            var adapterType = routes[index];
            if (!_adapters.TryGetValue(adapterType, out var adapter) || adapter is not IEstimateTradingPartnerAdapter estimate ||
                !adapter.Capabilities.HasFlag(TradingPartnerCapability.PaymentEstimate))
            {
                failures.Add($"{adapterType} is unavailable");
                continue;
            }

            try
            {
                var result = await AuditAsync(request.EstimateRequest.TenantId, request.PayerId, adapter.AdapterType, "PaymentEstimate",
                    async () => await estimate.GetEstimateAsync(request.EstimateRequest, cancellationToken),
                    value => value?.Status.ToString() ?? "NoResult", cancellationToken);
                if (result is not null) return new RoutedTreatmentEstimate(result, adapter.AdapterType, index + 1);
            }
            catch (TreatmentEstimateUnavailableException ex)
            {
                failures.Add($"{adapterType}: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{adapterType}: unexpected error - {ex.Message}");
            }
        }

        throw new TreatmentEstimateUnavailableException("No configured estimate source could produce a result. " + string.Join("; ", failures));
    }

    private PayerRouteOptions RouteFor(string payerId) => _options.Payers.TryGetValue(payerId, out var route)
        ? route : throw new TreatmentEstimateUnavailableException("No trading-partner route is configured for this payer.");

    private void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(_tenantProvider.TenantId) || tenantId != _tenantProvider.TenantId)
            throw new UnauthorizedAccessException("The payer transaction is outside the active tenant.");
    }

    private async Task<T> AuditAsync<T>(string tenantId, string payerId, string adapterType, string transactionType,
        Func<Task<T>> action, Func<T, string> status, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            await _audit.RecordAsync(new TransactionAuditRecord
            {
                CorrelationId = correlationId, TenantId = tenantId, PayerId = payerId, AdapterType = adapterType,
                TransactionType = transactionType, RequestedAt = started, RespondedAt = DateTimeOffset.UtcNow,
                Succeeded = true, ResponseStatus = status(result), ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            }, cancellationToken);
            return result;
        }
        catch
        {
            await _audit.RecordAsync(new TransactionAuditRecord
            {
                CorrelationId = correlationId, TenantId = tenantId, PayerId = payerId, AdapterType = adapterType,
                TransactionType = transactionType, RequestedAt = started, RespondedAt = DateTimeOffset.UtcNow,
                Succeeded = false, ResponseStatus = "Failed", ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            }, cancellationToken);
            throw;
        }
    }
}

public sealed class CloudHealthOfficeTradingPartnerAdapter(IInsuranceEstimateService estimates) : IEstimateTradingPartnerAdapter
{
    public string AdapterType => "CloudHealthOffice";
    public TradingPartnerCapability Capabilities => TradingPartnerCapability.PaymentEstimate;
    public Task<TreatmentEstimateResult?> GetEstimateAsync(TreatmentEstimateRequest request, CancellationToken cancellationToken = default) =>
        Wrap(estimates.EstimateAsync(request, cancellationToken));
    private static async Task<TreatmentEstimateResult?> Wrap(Task<TreatmentEstimateResult> task) => await task;
}

public sealed class MockEligibilityTradingPartnerAdapter : IEligibilityTradingPartnerAdapter
{
    public string AdapterType => "Mock";
    public TradingPartnerCapability Capabilities => TradingPartnerCapability.Eligibility;

    public Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new EligibilityResult
        {
            CorrelationId = $"mock-{Guid.NewGuid():N}", CoverageStatus = CoverageStatus.Active,
            PlanName = "Development Dental PPO", PlanType = "PPO", EffectiveDate = new DateOnly(2026, 1, 1),
            Deductible = 50m, DeductibleRemaining = 25m, AnnualMaximum = 2000m, AnnualMaximumRemaining = 1650m,
            Coinsurance = .20m, Source = AdapterType, VerifiedAt = DateTimeOffset.UtcNow,
            Benefits = [new EligibilityBenefit("35", "Dental care", CoverageStatus.Active, Coinsurance: .20m)],
            Messages = ["Development-only normalized eligibility response. No external transaction was sent."]
        });
}

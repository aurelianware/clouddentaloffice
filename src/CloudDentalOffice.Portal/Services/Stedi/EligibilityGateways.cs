using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services.Stedi;

/// <summary>One path an eligibility check can take. Selected per practice by <see cref="ClearinghouseEligibilityAdapter"/>.</summary>
public interface IEligibilityGateway
{
    EligibilityGatewayKind Kind { get; }
    Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default);
}

/// <summary>CDO → CloudHealthOffice provider eligibility API. CHO sends the 270 with its own clearinghouse account.</summary>
public sealed class CloudHealthOfficeEligibilityGateway(ICloudHealthOfficeEligibilityClient client) : IEligibilityGateway
{
    public EligibilityGatewayKind Kind => EligibilityGatewayKind.CloudHealthOffice;

    public Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default) =>
        client.CheckAsync(request, cancellationToken);
}

/// <summary>CDO → Stedi with the practice's own key (or the flagged shared key).</summary>
public sealed class StediEligibilityGateway(IStediEligibilityClient client) : IEligibilityGateway
{
    public EligibilityGatewayKind Kind => EligibilityGatewayKind.Stedi;

    public Task<EligibilityResult> CheckAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default) =>
        client.CheckAsync(request, cancellationToken);
}

/// <summary>
/// Trading-partner adapter "Clearinghouse": routes a payer's eligibility to the
/// path the practice's <see cref="TenantClearinghouseConnection"/> selects.
/// With no active connection the check fails; it never picks a path or key on
/// the practice's behalf. The CloudHealthOffice path uses CHO's clearinghouse
/// account rather than the practice's, so each use is logged.
/// </summary>
public sealed class ClearinghouseEligibilityAdapter(
    IClearinghouseConnectionStore connections,
    IEnumerable<IEligibilityGateway> gateways,
    ILogger<ClearinghouseEligibilityAdapter> logger) : IEligibilityTradingPartnerAdapter
{
    public const string Name = "Clearinghouse";

    public string AdapterType => Name;
    public TradingPartnerCapability Capabilities => TradingPartnerCapability.Eligibility;

    public async Task<EligibilityResult> CheckEligibilityAsync(NormalizedEligibilityRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.TenantId))
            throw new InvalidOperationException("Eligibility request has no tenant.");

        var connection = await connections.GetAsync(request.TenantId, cancellationToken);
        if (connection is null || connection.Status != ClearinghouseConnectionStatus.Active)
            throw new TreatmentEstimateUnavailableException(
                "Eligibility checks aren't set up for this practice yet. Contact support to connect a clearinghouse.");

        var gateway = gateways.FirstOrDefault(g => g.Kind == connection.EligibilityGateway)
            ?? throw new TreatmentEstimateUnavailableException("Eligibility checks are misconfigured. Contact support.");

        if (gateway.Kind == EligibilityGatewayKind.CloudHealthOffice)
            logger.LogWarning("Tenant {TenantId} eligibility is routed through CloudHealthOffice's clearinghouse account", request.TenantId);

        return await gateway.CheckAsync(request, cancellationToken);
    }
}

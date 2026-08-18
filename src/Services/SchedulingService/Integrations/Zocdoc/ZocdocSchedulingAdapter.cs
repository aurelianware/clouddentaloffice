using CloudDentalOffice.Contracts.Scheduling;

namespace SchedulingService.Integrations.Zocdoc;

internal sealed class ZocdocSchedulingAdapter(
    ISchedulingIntegrationConfigurationStore configurations,
    IZocdocApiClient apiClient) : ISchedulingExternalEntitySource
{
    public SchedulingChannel Channel => SchedulingChannel.Zocdoc;

    public async Task ValidateConnectionAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var configuration = await EnabledConfigurationAsync(tenantId, cancellationToken);
        await apiClient.ValidateConnectionAsync(tenantId, configuration, cancellationToken);
    }

    public async Task<IReadOnlyList<ExternalSchedulingEntity>> GetExternalEntitiesAsync(
        string tenantId, CancellationToken cancellationToken = default)
    {
        var configuration = await EnabledConfigurationAsync(tenantId, cancellationToken);
        var schedulableEntities = await apiClient.GetSchedulableEntitiesAsync(
            tenantId, configuration, cancellationToken);
        var visitReasons = await apiClient.GetVisitReasonsAsync(
            tenantId, configuration, cancellationToken);
        return ZocdocMapper.ToCanonical(schedulableEntities, visitReasons);
    }

    private async Task<SchedulingIntegrationConfiguration> EnabledConfigurationAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        var configuration = await configurations.GetAsync(
            tenantId, SchedulingChannel.Zocdoc, cancellationToken);
        if (configuration is not { Enabled: true })
            throw new SchedulingIntegrationDisabledException(SchedulingChannel.Zocdoc);
        _ = ZocdocEndpoints.Parse(configuration.Environment);
        return configuration;
    }
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace SchedulingService.Integrations.Zocdoc;

public sealed record ZocdocReadinessCheck(string Name, bool Ready, string Detail);

public sealed record ZocdocReadinessStatus(
    bool Ready,
    DateTimeOffset CheckedAt,
    IReadOnlyList<ZocdocReadinessCheck> Checks);

public sealed record ZocdocReconciliationStatus(
    DateTimeOffset CheckedAt,
    int MissingLocalAppointments,
    int StaleAvailabilityRecords,
    int FailedOutboundAppointments,
    int PendingOutboundAppointments,
    int FailedInboundEvents,
    int IntegrationConflicts,
    IReadOnlyList<string> Diagnostics);

internal interface IZocdocOperationsService
{
    Task<ZocdocReadinessStatus> GetReadinessAsync(string tenantId, bool probeAuthentication,
        CancellationToken cancellationToken = default);
    Task<ZocdocReconciliationStatus> ReconcileAsync(string tenantId, TimeSpan staleAfter,
        CancellationToken cancellationToken = default);
}

internal sealed class ZocdocOperationsService(
    SchedulingDbContext db,
    ISchedulingEntityMappingService mappings,
    IZocdocCredentialProvider credentials,
    ISchedulingChannelAdapterResolver adapters,
    TimeProvider timeProvider) : IZocdocOperationsService
{
    public async Task<ZocdocReadinessStatus> GetReadinessAsync(string tenantId, bool probeAuthentication,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        var checks = new List<ZocdocReadinessCheck>();
        var configuration = await db.SchedulingIntegrationConfigurations.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc, cancellationToken);

        var configured = configuration is { Enabled: true } &&
            !string.IsNullOrWhiteSpace(configuration.CredentialReference) &&
            configuration.MaximumBookingHorizonDays is > 0 and <= 150;
        checks.Add(new("Configuration valid", configured,
            configured ? "Enabled with a credential reference and supported booking horizon."
                : "Enable Zocdoc, select a credential reference, and use a horizon of 1-150 days."));

        var webhookConfigured = false;
        if (configuration is not null && !string.IsNullOrWhiteSpace(configuration.CredentialReference))
        {
            try
            {
                var secret = await credentials.GetAsync(tenantId, configuration, cancellationToken);
                webhookConfigured = !string.IsNullOrWhiteSpace(secret.WebhookSecret);
            }
            catch (ZocdocIntegrationException) { }
        }
        checks.Add(new("Webhook configured", webhookConfigured,
            webhookConfigured ? "A webhook signing key is present in secret-backed configuration."
                : "No webhook signing key was found for this credential reference."));

        var invalidMappings = await mappings.ListInvalidAsync(
            tenantId, SchedulingChannel.Zocdoc, cancellationToken);
        foreach (var (type, name) in new[]
        {
            (SchedulingResourceType.Provider, "Provider mappings complete"),
            (SchedulingResourceType.Location, "Location mappings complete"),
            (SchedulingResourceType.VisitReason, "Visit reason mappings complete")
        })
        {
            var unmapped = await mappings.ListUnmappedAsync(tenantId, SchedulingChannel.Zocdoc, type, cancellationToken);
            var invalid = invalidMappings.Count(x => x.EntityType == type);
            var complete = unmapped.Count == 0 && invalid == 0;
            checks.Add(new(name, complete, complete ? "Complete."
                : $"{unmapped.Count} unmapped and {invalid} invalid active mapping(s)."));
        }

        var availability = await db.SchedulingAvailabilitySyncStates.AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc && x.LastSuccessAt.HasValue)
            .MaxAsync(x => (DateTime?)x.LastSuccessAt, cancellationToken);
        checks.Add(new("Last availability synchronization successful", availability.HasValue,
            availability.HasValue ? $"Last success: {availability.Value:O}." : "No successful synchronization is recorded."));

        var appointmentSync = await db.ExternalAppointmentReferences.AsNoTracking().Where(x =>
                x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc &&
                x.SyncStatus == ExternalAppointmentSyncStatus.Synced && x.LastSyncedAt.HasValue)
            .MaxAsync(x => (DateTime?)x.LastSyncedAt, cancellationToken);
        checks.Add(new("Last appointment synchronization successful", appointmentSync.HasValue,
            appointmentSync.HasValue ? $"Last success: {appointmentSync.Value:O}." : "No successful synchronization is recorded."));

        if (probeAuthentication)
        {
            var authenticated = false;
            var detail = "Authentication was not attempted because configuration is incomplete.";
            if (configured)
            {
                try
                {
                    var adapter = await adapters.ResolveAsync(tenantId, SchedulingChannel.Zocdoc, cancellationToken);
                    await ((ISchedulingExternalEntitySource)adapter).ValidateConnectionAsync(tenantId, cancellationToken);
                    authenticated = true;
                    detail = "OAuth and API connectivity succeeded.";
                }
                catch (ZocdocIntegrationException ex) { detail = $"Connection failed: {ex.Kind}."; }
            }
            checks.Add(new("Authentication successful", authenticated, detail));
        }
        else
        {
            checks.Add(new("Authentication successful", false,
                "Not probed. Repeat with probeAuthentication=true for an explicit sandbox/API check."));
        }

        return new(checks.All(x => x.Ready), timeProvider.GetUtcNow(), checks);
    }

    public async Task<ZocdocReconciliationStatus> ReconcileAsync(string tenantId, TimeSpan staleAfter,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (staleAfter < TimeSpan.FromMinutes(15) || staleAfter > TimeSpan.FromDays(30))
            throw new ArgumentException("Stale threshold must be between 15 minutes and 30 days.", nameof(staleAfter));
        var cutoff = timeProvider.GetUtcNow().UtcDateTime.Subtract(staleAfter);
        var references = db.ExternalAppointmentReferences.AsNoTracking().Where(x =>
            x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc);
        var missingLocal = await references.CountAsync(reference =>
            !db.Appointments.Any(appointment => appointment.TenantId == tenantId && appointment.Id == reference.AppointmentId),
            cancellationToken);
        var staleAvailability = await db.SchedulingAvailabilitySyncStates.AsNoTracking().CountAsync(x =>
            x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc &&
            (x.Status != AvailabilitySyncStatus.Succeeded || !x.LastSuccessAt.HasValue || x.LastSuccessAt < cutoff),
            cancellationToken);
        var failedOutbound = await references.CountAsync(x => x.SyncStatus == ExternalAppointmentSyncStatus.Failed,
            cancellationToken);
        var pendingOutbound = await references.CountAsync(x =>
            x.SyncStatus == ExternalAppointmentSyncStatus.Pending && x.UpdatedAt < cutoff, cancellationToken);
        var conflicts = await references.CountAsync(x => x.SyncStatus == ExternalAppointmentSyncStatus.Conflict,
            cancellationToken);
        var failedInbound = await db.SchedulingIntegrationEvents.AsNoTracking().CountAsync(x =>
            x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc &&
            x.Status == SchedulingIntegrationEventStatus.Failed, cancellationToken);

        var diagnostics = new List<string>();
        Add(diagnostics, missingLocal, "Zocdoc appointment reference(s) point to a missing CDO appointment.");
        Add(diagnostics, staleAvailability, "availability provider/date record(s) are stale or unsuccessful.");
        Add(diagnostics, failedOutbound, "outbound appointment synchronization(s) failed.");
        Add(diagnostics, pendingOutbound, "outbound appointment synchronization(s) are pending beyond the threshold.");
        Add(diagnostics, failedInbound, "inbound event(s) failed processing.");
        Add(diagnostics, conflicts, "appointment synchronization conflict(s) require review.");
        if (diagnostics.Count == 0) diagnostics.Add("No persisted reconciliation problems were found.");
        diagnostics.Add("CDO appointments without an external reference cannot be inferred unless they carry Zocdoc provenance.");

        return new(timeProvider.GetUtcNow(), missingLocal, staleAvailability, failedOutbound,
            pendingOutbound, failedInbound, conflicts, diagnostics);
    }

    private static void Add(List<string> diagnostics, int count, string text)
    {
        if (count > 0) diagnostics.Add($"{count} {text}");
    }
}

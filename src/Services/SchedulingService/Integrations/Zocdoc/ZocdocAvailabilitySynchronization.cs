using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace SchedulingService.Integrations.Zocdoc;

public sealed record ZocdocAvailabilityReconciliationRequest(
    string TenantId, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int? ProviderId = null);

public sealed record ZocdocAvailabilitySyncResult(
    int Attempted, int Succeeded, int Failed, int SkippedMapping, int Unchanged);

public interface IZocdocAvailabilitySynchronizer
{
    Task<ZocdocAvailabilitySyncResult> ReconcileAsync(
        ZocdocAvailabilityReconciliationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ZocdocAvailabilityMetrics : IDisposable
{
    private readonly Meter _meter = new("CloudDentalOffice.Scheduling.Zocdoc");
    internal Counter<long> Attempts { get; }
    internal Counter<long> Successes { get; }
    internal Counter<long> Failures { get; }
    internal Counter<long> MappingSkips { get; }
    internal Histogram<double> ApiLatency { get; }

    public ZocdocAvailabilityMetrics()
    {
        Attempts = _meter.CreateCounter<long>("scheduling.zocdoc.availability_sync.attempts");
        Successes = _meter.CreateCounter<long>("scheduling.zocdoc.availability_sync.successes");
        Failures = _meter.CreateCounter<long>("scheduling.zocdoc.availability_sync.failures");
        MappingSkips = _meter.CreateCounter<long>("scheduling.zocdoc.availability_sync.mapping_skips");
        ApiLatency = _meter.CreateHistogram<double>("scheduling.zocdoc.api.duration", "ms");
    }

    public void Dispose() => _meter.Dispose();
}

internal sealed class ZocdocAvailabilitySynchronizer(
    SchedulingDbContext db,
    ISchedulingAvailabilityService availability,
    ISchedulingIntegrationConfigurationStore configurations,
    IZocdocApiClient api,
    ZocdocAvailabilityMetrics metrics,
    ILogger<ZocdocAvailabilitySynchronizer> logger) : IZocdocAvailabilitySynchronizer
{
    public async Task<ZocdocAvailabilitySyncResult> ReconcileAsync(
        ZocdocAvailabilityReconciliationRequest request, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(request.TenantId);
        if (request.ToUtc <= request.FromUtc) throw new ArgumentException("Reconciliation end must follow start.");
        if (request.ToUtc - request.FromUtc > TimeSpan.FromDays(31))
            throw new ArgumentException("A reconciliation request may cover at most 31 days.");
        var configuration = await configurations.GetAsync(request.TenantId, SchedulingChannel.Zocdoc, cancellationToken);
        if (configuration is not { Enabled: true }) return new(0, 0, 0, 0, 0);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId);
        var mappings = await db.ExternalSchedulingResourceMappings.AsNoTracking().Where(x =>
            x.TenantId == request.TenantId && x.Channel == SchedulingChannel.Zocdoc && x.IsActive)
            .ToListAsync(cancellationToken);
        List<int> providerIds = request.ProviderId.HasValue
            ? [request.ProviderId.Value]
            : await db.SchedulingProviderWorkingHours.AsNoTracking().Where(x =>
                x.TenantId == request.TenantId && x.IsActive).Select(x => x.ProviderId)
                .Distinct().ToListAsync(cancellationToken);

        var fromDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.FromUtc, zone).DateTime);
        var toDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.ToUtc.AddTicks(-1), zone).DateTime);
        var result = new MutableResult();
        foreach (var providerId in providerIds)
            for (var date = fromDate; date <= toDate; date = date.AddDays(1))
                await ReconcileProviderDateAsync(request.TenantId, providerId, date, zone,
                    configuration, mappings, result, cancellationToken);
        return new(result.Attempted, result.Succeeded, result.Failed, result.Skipped, result.Unchanged);
    }

    private async Task ReconcileProviderDateAsync(string tenantId, int providerId, DateOnly date,
        TimeZoneInfo zone, SchedulingIntegrationConfiguration configuration,
        IReadOnlyList<ExternalSchedulingResourceMapping> mappings, MutableResult result,
        CancellationToken cancellationToken)
    {
        result.Attempted++;
        metrics.Attempts.Add(1, Tags(tenantId));
        var providerMapping = Find(mappings, SchedulingResourceType.Provider, providerId.ToString());
        if (providerMapping is null)
        {
            result.Skipped++;
            metrics.MappingSkips.Add(1, Tags(tenantId));
            await SaveStateAsync(tenantId, providerId, date, AvailabilitySyncStatus.SkippedMapping,
                null, "Missing active provider mapping.", cancellationToken);
            return;
        }

        var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var localEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var from = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero);
        var to = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone), TimeSpan.Zero);
        var canonical = new List<SchedulingAvailabilitySlot>();
        foreach (var relationship in new[] { PatientRelationship.New, PatientRelationship.Existing })
            canonical.AddRange(await availability.GetAvailabilityAsync(new SchedulingAvailabilityQuery
            {
                TenantId = tenantId, Channel = SchedulingChannel.Zocdoc, ProviderId = providerId,
                FromUtc = from, ToUtc = to, PatientRelationship = relationship
            }, cancellationToken));

        var missing = new HashSet<string>(StringComparer.Ordinal);
        var workingLocations = await db.SchedulingProviderWorkingHours.AsNoTracking().Where(x =>
            x.TenantId == tenantId && x.ProviderId == providerId && x.DayOfWeek == date.DayOfWeek && x.IsActive)
            .Select(x => x.LocationId).Distinct().ToListAsync(cancellationToken);
        foreach (var locationId in workingLocations)
            if (Find(mappings, SchedulingResourceType.Location, locationId.ToString()) is null)
                missing.Add($"Location:{locationId}");
        var appointmentTypeIds = await db.SchedulingAppointmentTypes.AsNoTracking().Where(x =>
            x.TenantId == tenantId && x.IsActive && (!x.ProviderId.HasValue || x.ProviderId == providerId) &&
            (!x.LocationId.HasValue || workingLocations.Contains(x.LocationId.Value)))
            .Select(x => x.AppointmentTypeId).ToListAsync(cancellationToken);
        foreach (var appointmentTypeId in appointmentTypeIds)
            if (Find(mappings, SchedulingResourceType.VisitReason, appointmentTypeId) is null)
                missing.Add($"VisitReason:{appointmentTypeId}");
        var slots = canonical.Select(slot => Map(slot, providerMapping.ExternalId, zone, mappings, missing))
            .Where(x => x is not null).Cast<ZocdocTimeslotRequest>()
            .DistinctBy(x => new { x.ProviderId, x.LocationId, x.StartTime, x.PatientType,
                Reasons = string.Join('|', x.AllowedVisitReasonIds.Order()) })
            .OrderBy(x => x.ProviderId, StringComparer.Ordinal)
            .ThenBy(x => x.LocationId, StringComparer.Ordinal)
            .ThenBy(x => x.StartTime, StringComparer.Ordinal)
            .ThenBy(x => x.PatientType, StringComparer.Ordinal)
            .ThenBy(x => string.Join('|', x.AllowedVisitReasonIds.Order()), StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            result.Skipped += missing.Count;
            metrics.MappingSkips.Add(missing.Count, Tags(tenantId));
            logger.LogWarning("Skipped Zocdoc availability mappings for tenant {TenantId}, provider {ProviderId}, date {LocalDate}: {MissingMappings}",
                tenantId, providerId, date, string.Join(",", missing));
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(slots))));
        var state = await db.SchedulingAvailabilitySyncStates.SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            x.Channel == SchedulingChannel.Zocdoc && x.ProviderId == providerId && x.LocalDate == date, cancellationToken);
        if (state is { Status: AvailabilitySyncStatus.Succeeded or AvailabilitySyncStatus.SkippedMapping } &&
            state.ContentHash == hash)
        {
            result.Unchanged++;
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await api.ReplaceTimeslotsAsync(tenantId, configuration, providerMapping.ExternalId, date, slots, cancellationToken);
            metrics.ApiLatency.Record(stopwatch.Elapsed.TotalMilliseconds, Tags(tenantId));
            metrics.Successes.Add(1, Tags(tenantId));
            result.Succeeded++;
            await SaveStateAsync(tenantId, providerId, date,
                missing.Count == 0 ? AvailabilitySyncStatus.Succeeded : AvailabilitySyncStatus.SkippedMapping,
                hash, missing.Count == 0 ? null : string.Join(",", missing), cancellationToken, succeeded: true);
        }
        catch (ZocdocIntegrationException ex)
        {
            metrics.ApiLatency.Record(stopwatch.Elapsed.TotalMilliseconds, Tags(tenantId));
            metrics.Failures.Add(1, Tags(tenantId));
            result.Failed++;
            await SaveStateAsync(tenantId, providerId, date, AvailabilitySyncStatus.Failed,
                null, ex.Kind.ToString(), cancellationToken);
            logger.LogError(ex, "Zocdoc availability reconciliation failed for tenant {TenantId}, provider {ProviderId}, date {LocalDate}",
                tenantId, providerId, date);
        }
    }

    private static ZocdocTimeslotRequest? Map(SchedulingAvailabilitySlot slot, string externalProviderId,
        TimeZoneInfo zone, IReadOnlyList<ExternalSchedulingResourceMapping> mappings, ISet<string> missing)
    {
        var location = Find(mappings, SchedulingResourceType.Location, slot.LocationId.ToString());
        var reason = Find(mappings, SchedulingResourceType.VisitReason, slot.AppointmentTypeId);
        if (location is null) missing.Add($"Location:{slot.LocationId}");
        if (reason is null) missing.Add($"VisitReason:{slot.AppointmentTypeId}");
        if (location is null || reason is null) return null;
        var local = TimeZoneInfo.ConvertTime(slot.StartUtc, zone);
        return new ZocdocTimeslotRequest
        {
            ProviderId = externalProviderId, LocationId = location.ExternalId,
            StartTime = local.ToString("yyyy-MM-dd'T'HH:mm:ss"), TimeZone = zone.Id,
            AllowedVisitReasonIds = [reason.ExternalId],
            PatientType = slot.PatientRelationship == PatientRelationship.New ? "new" : "existing"
        };
    }

    private static ExternalSchedulingResourceMapping? Find(IEnumerable<ExternalSchedulingResourceMapping> mappings,
        SchedulingResourceType type, string internalId) => mappings.FirstOrDefault(x =>
            x.ResourceType == type && string.Equals(x.InternalId, internalId, StringComparison.OrdinalIgnoreCase));

    private async Task SaveStateAsync(string tenantId, int providerId, DateOnly date, AvailabilitySyncStatus status,
        string? hash, string? diagnostic, CancellationToken cancellationToken, bool succeeded = false)
    {
        var state = await db.SchedulingAvailabilitySyncStates.SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            x.Channel == SchedulingChannel.Zocdoc && x.ProviderId == providerId && x.LocalDate == date, cancellationToken);
        if (state is null)
        {
            state = new SchedulingAvailabilitySyncState
                { TenantId = tenantId, Channel = SchedulingChannel.Zocdoc, ProviderId = providerId, LocalDate = date };
            db.SchedulingAvailabilitySyncStates.Add(state);
        }
        state.Status = status; state.ContentHash = hash; state.Diagnostic = diagnostic;
        state.LastAttemptAt = DateTime.UtcNow;
        if (succeeded) state.LastSuccessAt = state.LastAttemptAt;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static KeyValuePair<string, object?>[] Tags(string tenantId) =>
        [new("tenant.id", tenantId), new("scheduling.channel", "Zocdoc")];

    private sealed class MutableResult
    { public int Attempted; public int Succeeded; public int Failed; public int Skipped; public int Unchanged; }
}

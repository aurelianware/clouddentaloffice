using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;
using SchedulingService.Integrations.Zocdoc;

public interface ISchedulingChannelAdapter
{
    SchedulingChannel Channel { get; }
}

public sealed record ExternalSchedulingEntity(
    SchedulingResourceType EntityType,
    string ExternalId,
    string DisplayName);

public interface ISchedulingExternalEntitySource : ISchedulingChannelAdapter
{
    Task ValidateConnectionAsync(string tenantId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExternalSchedulingEntity>> GetExternalEntitiesAsync(
        string tenantId, CancellationToken cancellationToken = default);
}

public interface ISchedulingChannelAdapterResolver
{
    Task<ISchedulingChannelAdapter> ResolveAsync(string tenantId, SchedulingChannel channel, CancellationToken cancellationToken = default);
}

public interface ISchedulingAvailabilityService
{
    Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(
        SchedulingAvailabilityQuery query, CancellationToken cancellationToken = default);
}

public interface ISchedulingBookingService
{
    Task<SchedulingBookingResult> BookAsync(SchedulingBookingCommand command, CancellationToken cancellationToken = default);
}

public interface ISchedulingIntegrationConfigurationStore
{
    Task<SchedulingIntegrationConfiguration?> GetAsync(string tenantId, SchedulingChannel channel, CancellationToken cancellationToken = default);
}

public interface IExternalAppointmentReferenceStore
{
    Task<ExternalAppointmentReference?> FindAsync(string tenantId, SchedulingChannel channel, string externalAppointmentId,
        CancellationToken cancellationToken = default);
    Task AddAsync(ExternalAppointmentReference reference, CancellationToken cancellationToken = default);
}

public interface IExternalSchedulingResourceMappingStore
{
    Task<ExternalSchedulingResourceMapping?> FindByExternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType resourceType, string externalId, CancellationToken cancellationToken = default);
    Task AddAsync(ExternalSchedulingResourceMapping mapping, CancellationToken cancellationToken = default);
}

public sealed record SchedulingIntegrationEventLease(Guid Id, bool Acquired, Guid? AppointmentId);

public interface ISchedulingIntegrationIdempotencyStore
{
    Task<SchedulingIntegrationEventLease> TryBeginAsync(string tenantId, SchedulingChannel channel, string externalEventId,
        CancellationToken cancellationToken = default);
    Task CompleteAsync(string tenantId, SchedulingChannel channel, string externalEventId, Guid appointmentId,
        CancellationToken cancellationToken = default);
}

public sealed class SchedulingIntegrationDisabledException(SchedulingChannel channel)
    : InvalidOperationException($"Scheduling integration '{channel}' is disabled for this tenant.");

public sealed class UnsupportedSchedulingChannelException(SchedulingChannel channel)
    : InvalidOperationException($"No scheduling adapter is registered for channel '{channel}'.");

public sealed class DuplicateSchedulingChannelAdapterException(SchedulingChannel channel, int registrationCount)
    : InvalidOperationException(
        $"Scheduling channel '{channel}' has {registrationCount} registered adapters; exactly one is allowed.")
{
    public SchedulingChannel Channel { get; } = channel;
    public int RegistrationCount { get; } = registrationCount;
}

public sealed class SchedulingIntegrationConfigurationStore(SchedulingDbContext db) : ISchedulingIntegrationConfigurationStore
{
    public Task<SchedulingIntegrationConfiguration?> GetAsync(string tenantId, SchedulingChannel channel,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        return db.SchedulingIntegrationConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Channel == channel, cancellationToken);
    }

    internal static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) throw new ArgumentException("TenantId is required.", nameof(tenantId));
    }
}

public sealed class SchedulingChannelAdapterResolver(
    IEnumerable<ISchedulingChannelAdapter> adapters,
    ISchedulingIntegrationConfigurationStore configurations) : ISchedulingChannelAdapterResolver
{
    private readonly IReadOnlyDictionary<SchedulingChannel, ISchedulingChannelAdapter> _adapters =
        BuildAdapterMap(adapters);

    private static IReadOnlyDictionary<SchedulingChannel, ISchedulingChannelAdapter> BuildAdapterMap(
        IEnumerable<ISchedulingChannelAdapter> adapters)
    {
        var groups = adapters.GroupBy(x => x.Channel).ToList();
        var duplicate = groups.FirstOrDefault(x => x.Count() != 1);
        if (duplicate is not null)
            throw new DuplicateSchedulingChannelAdapterException(duplicate.Key, duplicate.Count());
        return groups.ToDictionary(x => x.Key, x => x.Single());
    }

    public async Task<ISchedulingChannelAdapter> ResolveAsync(string tenantId, SchedulingChannel channel,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (!_adapters.TryGetValue(channel, out var adapter))
            throw new UnsupportedSchedulingChannelException(channel);

        if (channel != SchedulingChannel.Internal)
        {
            var configuration = await configurations.GetAsync(tenantId, channel, cancellationToken);
            if (configuration is not { Enabled: true }) throw new SchedulingIntegrationDisabledException(channel);
        }
        return adapter;
    }
}

public sealed class ExternalAppointmentReferenceStore(SchedulingDbContext db) : IExternalAppointmentReferenceStore
{
    public Task<ExternalAppointmentReference?> FindAsync(string tenantId, SchedulingChannel channel,
        string externalAppointmentId, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        ValidateExternalId(externalAppointmentId);
        return db.ExternalAppointmentReferences.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Channel == channel && x.ExternalAppointmentId == externalAppointmentId,
            cancellationToken);
    }

    public async Task AddAsync(ExternalAppointmentReference reference, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(reference.TenantId);
        ValidateExternalId(reference.ExternalAppointmentId);
        if (reference.AppointmentId == Guid.Empty) throw new ArgumentException("AppointmentId is required.", nameof(reference));
        reference.CreatedAt = reference.UpdatedAt = DateTime.UtcNow;
        db.ExternalAppointmentReferences.Add(reference);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateExternalId(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("External identifier is required.", nameof(externalId));
    }
}

public sealed class ExternalSchedulingResourceMappingStore(SchedulingDbContext db)
    : IExternalSchedulingResourceMappingStore
{
    public Task<ExternalSchedulingResourceMapping?> FindByExternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType resourceType, string externalId, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        ValidateId(externalId, nameof(externalId));
        return db.ExternalSchedulingResourceMappings.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Channel == channel && x.ResourceType == resourceType && x.ExternalId == externalId,
            cancellationToken);
    }

    public async Task AddAsync(ExternalSchedulingResourceMapping mapping, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(mapping.TenantId);
        ValidateId(mapping.InternalId, nameof(mapping.InternalId));
        ValidateId(mapping.ExternalId, nameof(mapping.ExternalId));
        mapping.CreatedAt = mapping.UpdatedAt = DateTime.UtcNow;
        db.ExternalSchedulingResourceMappings.Add(mapping);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Mapping identifier is required.", parameterName);
    }
}

public static class SchedulingBookingRules
{
    /// <summary>
    /// Guards the boundary immediately before a canonical booking can create an
    /// Appointment. PatientRelationship is intentionally not consulted here.
    /// </summary>
    public static void ValidateForAppointmentCreation(SchedulingBookingCommand command)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(command.TenantId);
        if (command.ResolvedPatientId <= 0)
            throw new ArgumentException("A real, internally resolved patient is required.", nameof(command));
        if (command.ProviderId <= 0) throw new ArgumentException("ProviderId is required.", nameof(command));
        if (command.LocationId == Guid.Empty) throw new ArgumentException("LocationId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.ExternalEventId))
            throw new ArgumentException("ExternalEventId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.ExternalAppointmentId))
            throw new ArgumentException("ExternalAppointmentId is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.AppointmentTypeId))
            throw new ArgumentException("AppointmentTypeId is required.", nameof(command));
        if (command.StartUtc.Kind != DateTimeKind.Utc || command.EndUtc.Kind != DateTimeKind.Utc || command.EndUtc <= command.StartUtc)
            throw new ArgumentException("A valid UTC scheduling interval is required.", nameof(command));
    }
}

public sealed class SchedulingIntegrationIdempotencyStore(SchedulingDbContext db) : ISchedulingIntegrationIdempotencyStore
{
    public async Task<SchedulingIntegrationEventLease> TryBeginAsync(string tenantId, SchedulingChannel channel,
        string externalEventId, CancellationToken cancellationToken = default)
    {
        Validate(tenantId, externalEventId);
        var existing = await FindAsync(tenantId, channel, externalEventId, cancellationToken);
        if (existing is not null) return new(existing.Id, false, existing.AppointmentId);

        var record = new SchedulingIntegrationEvent
        {
            TenantId = tenantId, Channel = channel, ExternalEventId = externalEventId.Trim()
        };
        db.SchedulingIntegrationEvents.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(record.Id, true, null);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            existing = await FindAsync(tenantId, channel, externalEventId, cancellationToken);
            if (existing is not null) return new(existing.Id, false, existing.AppointmentId);
            throw;
        }
    }

    public async Task CompleteAsync(string tenantId, SchedulingChannel channel, string externalEventId,
        Guid appointmentId, CancellationToken cancellationToken = default)
    {
        Validate(tenantId, externalEventId);
        if (appointmentId == Guid.Empty) throw new ArgumentException("AppointmentId is required.", nameof(appointmentId));
        var record = await FindAsync(tenantId, channel, externalEventId, cancellationToken)
            ?? throw new KeyNotFoundException("Integration event not found for this tenant and channel.");
        record.Status = SchedulingIntegrationEventStatus.Completed;
        record.AppointmentId = appointmentId;
        record.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<SchedulingIntegrationEvent?> FindAsync(string tenantId, SchedulingChannel channel, string eventId,
        CancellationToken cancellationToken) => db.SchedulingIntegrationEvents.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Channel == channel && x.ExternalEventId == eventId.Trim(), cancellationToken);

    private static void Validate(string tenantId, string eventId)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("ExternalEventId is required.", nameof(eventId));
    }
}

public static class SchedulingIntegrationServiceCollectionExtensions
{
    public static IServiceCollection AddSchedulingIntegrations(this IServiceCollection services)
    {
        services.AddScoped<ISchedulingIntegrationConfigurationStore, SchedulingIntegrationConfigurationStore>()
            .AddScoped<ISchedulingChannelAdapterResolver, SchedulingChannelAdapterResolver>()
            .AddScoped<ISchedulingEntityCatalog, SchedulingEntityCatalog>()
            .AddScoped<ISchedulingEntityMappingService, SchedulingEntityMappingService>()
            .AddSingleton<ISchedulingClock, SchedulingClock>()
            .AddScoped<ISchedulingAvailabilityService, SchedulingAvailabilityService>()
            .AddScoped<IExternalSchedulingResourceMappingStore, ExternalSchedulingResourceMappingStore>()
            .AddScoped<IExternalAppointmentReferenceStore, ExternalAppointmentReferenceStore>()
            .AddScoped<ISchedulingIntegrationIdempotencyStore, SchedulingIntegrationIdempotencyStore>()
            .AddSingleton<IZocdocCredentialProvider, ConfigurationZocdocCredentialProvider>()
            .AddSingleton<IZocdocAccessTokenProvider, ZocdocAccessTokenProvider>()
            .AddScoped<ZocdocSchedulingAdapter>()
            .AddScoped<ISchedulingChannelAdapter>(provider => provider.GetRequiredService<ZocdocSchedulingAdapter>())
            .AddScoped<ISchedulingExternalEntitySource>(provider => provider.GetRequiredService<ZocdocSchedulingAdapter>());

        services.AddHttpClient("ZocdocAuth").AddStandardResilienceHandler();
        services.AddHttpClient<IZocdocApiClient, ZocdocApiClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        }).AddStandardResilienceHandler();
        return services;
    }
}

using System.Text.RegularExpressions;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

public sealed record SchedulingEntityMappingDto(
    Guid Id,
    string TenantId,
    SchedulingChannel Channel,
    SchedulingResourceType EntityType,
    string InternalId,
    string ExternalId,
    string? ExternalDisplayName,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record UpsertSchedulingEntityMapping(
    SchedulingResourceType EntityType,
    string InternalId,
    string ExternalId,
    string? ExternalDisplayName,
    bool IsActive = true);

public sealed record SchedulingInternalEntity(
    SchedulingResourceType EntityType,
    string InternalId,
    string DisplayName,
    bool IsActive);

public interface ISchedulingEntityCatalog
{
    Task<bool> ExistsAsync(string tenantId, SchedulingResourceType entityType, string internalId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingInternalEntity>> ListAsync(string tenantId, SchedulingResourceType entityType,
        CancellationToken cancellationToken = default);
}

public interface ISchedulingEntityMappingService
{
    Task<SchedulingEntityMappingDto?> FindByIdAsync(string tenantId, SchedulingChannel channel,
        Guid mappingId, CancellationToken cancellationToken = default);
    Task<SchedulingEntityMappingDto?> FindByInternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType entityType, string internalId, CancellationToken cancellationToken = default);
    Task<SchedulingEntityMappingDto?> FindByExternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType entityType, string externalId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingEntityMappingDto>> ListAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType? entityType = null, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<SchedulingEntityMappingDto> UpsertAsync(string tenantId, SchedulingChannel channel,
        UpsertSchedulingEntityMapping request, Guid? mappingId = null, CancellationToken cancellationToken = default);
    Task DeactivateAsync(string tenantId, SchedulingChannel channel, Guid mappingId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingInternalEntity>> ListUnmappedAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType entityType, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SchedulingEntityMappingDto>> ListInvalidAsync(string tenantId, SchedulingChannel channel,
        CancellationToken cancellationToken = default);
}

public sealed class SchedulingEntityCatalog(SchedulingDbContext db) : ISchedulingEntityCatalog
{
    public async Task<bool> ExistsAsync(string tenantId, SchedulingResourceType entityType, string internalId,
        CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        return entityType switch
        {
            SchedulingResourceType.Provider when int.TryParse(internalId, out var providerId) =>
                await db.BookingRequests.AnyAsync(x => x.TenantId == tenantId && x.RequestedProviderId == providerId, cancellationToken),
            SchedulingResourceType.Location when Guid.TryParse(internalId, out var locationId) =>
                await db.BookingRequests.AnyAsync(x => x.TenantId == tenantId && x.RequestedLocationId == locationId, cancellationToken),
            SchedulingResourceType.VisitReason =>
                await db.SchedulingAppointmentTypes.AnyAsync(x => x.TenantId == tenantId && x.AppointmentTypeId == internalId, cancellationToken),
            _ => false
        };
    }

    public async Task<IReadOnlyList<SchedulingInternalEntity>> ListAsync(string tenantId,
        SchedulingResourceType entityType, CancellationToken cancellationToken = default)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        return entityType switch
        {
            SchedulingResourceType.Provider => (await db.BookingRequests
                .Where(x => x.TenantId == tenantId && x.RequestedProviderId.HasValue)
                .Select(x => x.RequestedProviderId!.Value).Distinct().OrderBy(x => x).ToListAsync(cancellationToken))
                .Select(x => new SchedulingInternalEntity(entityType, x.ToString(), $"Provider {x}", true)).ToList(),
            SchedulingResourceType.Location => (await db.BookingRequests
                .Where(x => x.TenantId == tenantId && x.RequestedLocationId.HasValue)
                .Select(x => x.RequestedLocationId!.Value).Distinct().OrderBy(x => x).ToListAsync(cancellationToken))
                .Select(x => new SchedulingInternalEntity(entityType, x.ToString(), $"Location {x}", true)).ToList(),
            SchedulingResourceType.VisitReason => await db.SchedulingAppointmentTypes
                .Where(x => x.TenantId == tenantId)
                .OrderBy(x => x.DisplayName)
                .Select(x => new SchedulingInternalEntity(entityType, x.AppointmentTypeId, x.DisplayName, x.IsActive))
                .ToListAsync(cancellationToken),
            _ => []
        };
    }
}

public sealed partial class SchedulingEntityMappingService(
    SchedulingDbContext db,
    ISchedulingEntityCatalog catalog) : ISchedulingEntityMappingService
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExternalIdPattern();

    public async Task<SchedulingEntityMappingDto?> FindByIdAsync(string tenantId, SchedulingChannel channel,
        Guid mappingId, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        if (mappingId == Guid.Empty) throw new ArgumentException("Mapping identifier is required.", nameof(mappingId));
        var mapping = await Query(tenantId, channel).AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == mappingId, cancellationToken);
        return mapping?.ToDto();
    }

    public async Task<SchedulingEntityMappingDto?> FindByInternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType entityType, string internalId, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        ValidateInternalId(entityType, internalId);
        var mapping = await Query(tenantId, channel).AsNoTracking().SingleOrDefaultAsync(x =>
            x.ResourceType == entityType && x.InternalId == internalId && x.IsActive, cancellationToken);
        return mapping?.ToDto();
    }

    public async Task<SchedulingEntityMappingDto?> FindByExternalIdAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType entityType, string externalId, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        ValidateExternalId(externalId);
        var mapping = await Query(tenantId, channel).AsNoTracking().SingleOrDefaultAsync(x =>
            x.ResourceType == entityType && x.ExternalId == externalId && x.IsActive, cancellationToken);
        return mapping?.ToDto();
    }

    public async Task<IReadOnlyList<SchedulingEntityMappingDto>> ListAsync(string tenantId, SchedulingChannel channel,
        SchedulingResourceType? entityType = null, bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        var query = Query(tenantId, channel).AsNoTracking();
        if (entityType.HasValue) query = query.Where(x => x.ResourceType == entityType.Value);
        if (!includeInactive) query = query.Where(x => x.IsActive);
        return (await query.OrderBy(x => x.ResourceType).ThenBy(x => x.InternalId).ToListAsync(cancellationToken))
            .Select(x => x.ToDto()).ToList();
    }

    public async Task<SchedulingEntityMappingDto> UpsertAsync(string tenantId, SchedulingChannel channel,
        UpsertSchedulingEntityMapping request, Guid? mappingId = null, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        ValidateInternalId(request.EntityType, request.InternalId);
        ValidateExternalId(request.ExternalId);
        if (!await catalog.ExistsAsync(tenantId, request.EntityType, request.InternalId, cancellationToken))
            throw new KeyNotFoundException("The internal scheduling entity does not exist for this tenant.");

        ExternalSchedulingResourceMapping? mapping = null;
        if (mappingId.HasValue)
        {
            mapping = await Query(tenantId, channel).SingleOrDefaultAsync(x => x.Id == mappingId.Value, cancellationToken)
                ?? throw new KeyNotFoundException("Mapping not found for this tenant and channel.");
        }
        else
        {
            mapping = await Query(tenantId, channel).SingleOrDefaultAsync(x =>
                x.ResourceType == request.EntityType && x.InternalId == request.InternalId, cancellationToken);
        }

        // The persisted unique indexes apply regardless of lifecycle state, so
        // reject all duplicate identifiers before SaveChanges can surface a
        // provider-specific DbUpdateException.
        var duplicate = await Query(tenantId, channel).AnyAsync(x =>
            x.ResourceType == request.EntityType && x.Id != (mapping == null ? Guid.Empty : mapping.Id) &&
            (x.InternalId == request.InternalId || x.ExternalId == request.ExternalId), cancellationToken);
        if (duplicate)
            throw new InvalidOperationException("A mapping already uses this internal or external identifier.");

        if (mapping is null)
        {
            mapping = new ExternalSchedulingResourceMapping
            {
                TenantId = tenantId, Channel = channel, ResourceType = request.EntityType,
                InternalId = request.InternalId, ExternalId = request.ExternalId, CreatedAt = DateTime.UtcNow
            };
            db.ExternalSchedulingResourceMappings.Add(mapping);
        }
        else
        {
            mapping.ResourceType = request.EntityType;
            mapping.InternalId = request.InternalId;
            mapping.ExternalId = request.ExternalId;
        }
        mapping.ExternalDisplayName = NormalizeDisplayName(request.ExternalDisplayName);
        mapping.IsActive = request.IsActive;
        mapping.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return mapping.ToDto();
    }

    public async Task DeactivateAsync(string tenantId, SchedulingChannel channel, Guid mappingId,
        CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        var mapping = await Query(tenantId, channel).SingleOrDefaultAsync(x => x.Id == mappingId, cancellationToken)
            ?? throw new KeyNotFoundException("Mapping not found for this tenant and channel.");
        mapping.IsActive = false;
        mapping.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SchedulingInternalEntity>> ListUnmappedAsync(string tenantId,
        SchedulingChannel channel, SchedulingResourceType entityType, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        var internalEntities = await catalog.ListAsync(tenantId, entityType, cancellationToken);
        var mappedIds = (await Query(tenantId, channel).Where(x => x.ResourceType == entityType && x.IsActive)
            .Select(x => x.InternalId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        return internalEntities.Where(x => x.IsActive && !mappedIds.Contains(x.InternalId)).ToList();
    }

    public async Task<IReadOnlyList<SchedulingEntityMappingDto>> ListInvalidAsync(string tenantId,
        SchedulingChannel channel, CancellationToken cancellationToken = default)
    {
        ValidateContext(tenantId, channel);
        var mappings = await Query(tenantId, channel).AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        var invalid = new List<SchedulingEntityMappingDto>();
        foreach (var mapping in mappings)
            if (!await catalog.ExistsAsync(tenantId, mapping.ResourceType, mapping.InternalId, cancellationToken))
                invalid.Add(mapping.ToDto());
        return invalid;
    }

    private IQueryable<ExternalSchedulingResourceMapping> Query(string tenantId, SchedulingChannel channel) =>
        db.ExternalSchedulingResourceMappings.Where(x => x.TenantId == tenantId && x.Channel == channel);

    private static void ValidateContext(string tenantId, SchedulingChannel channel)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(tenantId);
        if (channel is not (SchedulingChannel.PublicWebsite or SchedulingChannel.Zocdoc or SchedulingChannel.Google))
            throw new UnsupportedSchedulingChannelException(channel);
    }

    private static void ValidateInternalId(SchedulingResourceType type, string internalId)
    {
        var valid = type switch
        {
            SchedulingResourceType.Provider => int.TryParse(internalId, out var id) && id > 0,
            SchedulingResourceType.Location => Guid.TryParse(internalId, out var id) && id != Guid.Empty,
            SchedulingResourceType.VisitReason => !string.IsNullOrWhiteSpace(internalId) && internalId.Length <= 128,
            _ => false
        };
        if (!valid) throw new ArgumentException("Internal identifier is malformed for this entity type.", nameof(internalId));
    }

    private static void ValidateExternalId(string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId) || !ExternalIdPattern().IsMatch(externalId))
            throw new ArgumentException("External identifier is malformed.", nameof(externalId));
    }

    private static string? NormalizeDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, 300)];
    }
}

internal static class SchedulingEntityMappingConversions
{
    public static SchedulingEntityMappingDto ToDto(this ExternalSchedulingResourceMapping mapping) => new(
        mapping.Id, mapping.TenantId, mapping.Channel, mapping.ResourceType, mapping.InternalId,
        mapping.ExternalId, mapping.ExternalDisplayName, mapping.IsActive, mapping.CreatedAt, mapping.UpdatedAt);
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

public interface ISchedulingClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SchedulingClock : ISchedulingClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Generates channel-neutral offerable slots from CDO-owned schedules, rules,
/// mappings, blocks, and appointments. External adapters consume these slots;
/// vendor models do not participate in the calculation.
/// </summary>
public sealed class SchedulingAvailabilityService(
    SchedulingDbContext db,
    ISchedulingClock clock,
    ILogger<SchedulingAvailabilityService> logger) : ISchedulingAvailabilityService
{
    private static readonly TimeSpan SlotIncrement = TimeSpan.FromMinutes(15);

    public async Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(
        SchedulingAvailabilityQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query);
        var configuration = await db.SchedulingIntegrationConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == query.TenantId && x.Channel == query.Channel, cancellationToken);
        if (configuration is not { Enabled: true })
            return NoAvailability(query, "ChannelDisabled");

        TimeZoneInfo timeZone;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId); }
        catch (TimeZoneNotFoundException) { return NoAvailability(query, "InvalidPracticeTimeZone"); }
        catch (InvalidTimeZoneException) { return NoAvailability(query, "InvalidPracticeTimeZone"); }

        var now = clock.UtcNow.ToUniversalTime();
        if (configuration.MinimumBookingLeadMinutes < 0 || configuration.MaximumBookingHorizonDays < 0)
            return NoAvailability(query, "InvalidBookingWindowConfiguration");
        var effectiveFrom = Later(query.FromUtc.ToUniversalTime(), now.AddMinutes(configuration.MinimumBookingLeadMinutes));
        var effectiveTo = Earlier(query.ToUtc.ToUniversalTime(), now.AddDays(configuration.MaximumBookingHorizonDays));
        if (effectiveTo <= effectiveFrom)
            return NoAvailability(query, "OutsideBookingWindow");

        var mappings = await db.ExternalSchedulingResourceMappings.AsNoTracking()
            .Where(x => x.TenantId == query.TenantId && x.Channel == query.Channel && x.IsActive)
            .ToListAsync(cancellationToken);
        var exposedProviders = MappingIds(mappings, SchedulingResourceType.Provider);
        var exposedLocations = MappingIds(mappings, SchedulingResourceType.Location);
        var exposedTypes = MappingIds(mappings, SchedulingResourceType.VisitReason);

        var appointmentTypesQuery = db.SchedulingAppointmentTypes.AsNoTracking()
            .Where(x => x.TenantId == query.TenantId && x.IsActive && x.DurationMinutes > 0);
        if (!string.IsNullOrWhiteSpace(query.AppointmentTypeId))
            appointmentTypesQuery = appointmentTypesQuery.Where(x => x.AppointmentTypeId == query.AppointmentTypeId);
        var appointmentTypes = (await appointmentTypesQuery.ToListAsync(cancellationToken))
            .Where(x => x.Allows(query.PatientRelationship) && exposedTypes.Contains(x.AppointmentTypeId))
            .ToList();
        if (appointmentTypes.Count == 0)
            return NoAvailability(query, "NoEligibleExposedAppointmentTypes");

        var schedulesQuery = db.SchedulingProviderWorkingHours.AsNoTracking()
            .Where(x => x.TenantId == query.TenantId && x.IsActive);
        if (query.ProviderId.HasValue) schedulesQuery = schedulesQuery.Where(x => x.ProviderId == query.ProviderId.Value);
        if (query.LocationId.HasValue) schedulesQuery = schedulesQuery.Where(x => x.LocationId == query.LocationId.Value);
        var schedules = (await schedulesQuery.ToListAsync(cancellationToken))
            .Where(x => exposedProviders.Contains(x.ProviderId.ToString()) && exposedLocations.Contains(x.LocationId.ToString()))
            .ToList();
        if (schedules.Count == 0)
            return NoAvailability(query, "NoExposedWorkingSchedules");

        var fromUtc = effectiveFrom.UtcDateTime;
        var toUtc = effectiveTo.UtcDateTime;
        var appointments = await db.Appointments.AsNoTracking().Where(x =>
            x.TenantId == query.TenantId && x.Status != AppointmentStatus.Cancelled &&
            x.Status != AppointmentStatus.Requested && x.StartTime < toUtc && x.EndTime > fromUtc)
            .ToListAsync(cancellationToken);
        var blocks = await db.SchedulingBlockedTimes.AsNoTracking().Where(x =>
            x.TenantId == query.TenantId && x.IsActive && x.StartUtc < toUtc && x.EndUtc > fromUtc)
            .ToListAsync(cancellationToken);

        var localFromDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(effectiveFrom, timeZone).DateTime);
        var localToDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(effectiveTo, timeZone).DateTime);
        var slots = new List<SchedulingAvailabilitySlot>();
        var collisionCount = 0;
        var blockedCount = 0;

        for (var date = localFromDate; date <= localToDate; date = date.AddDays(1))
        {
            foreach (var schedule in schedules.Where(x => x.DayOfWeek == date.DayOfWeek))
            {
                if (!TryUtc(date, schedule.StartLocal, timeZone, out var workStart) ||
                    !TryUtc(date, schedule.EndLocal, timeZone, out var workEnd) || workEnd <= workStart)
                    continue;

                foreach (var appointmentType in appointmentTypes.Where(x =>
                    (!x.ProviderId.HasValue || x.ProviderId == schedule.ProviderId) &&
                    (!x.LocationId.HasValue || x.LocationId == schedule.LocationId)))
                {
                    var duration = TimeSpan.FromMinutes(appointmentType.DurationMinutes);
                    for (var start = workStart; start + duration <= workEnd; start += SlotIncrement)
                    {
                        var end = start + duration;
                        if (start < effectiveFrom || end > effectiveTo) continue;
                        if (appointments.Any(x => x.ProviderId == schedule.ProviderId &&
                            x.StartTime < end.UtcDateTime && x.EndTime > start.UtcDateTime))
                        {
                            collisionCount++;
                            continue;
                        }
                        if (blocks.Any(x => (!x.ProviderId.HasValue || x.ProviderId == schedule.ProviderId) &&
                            (!x.LocationId.HasValue || x.LocationId == schedule.LocationId) &&
                            x.StartUtc < end.UtcDateTime && x.EndUtc > start.UtcDateTime))
                        {
                            blockedCount++;
                            continue;
                        }
                        slots.Add(new SchedulingAvailabilitySlot
                        {
                            TenantId = query.TenantId,
                            ProviderId = schedule.ProviderId,
                            LocationId = schedule.LocationId,
                            AppointmentTypeId = appointmentType.AppointmentTypeId,
                            StartUtc = start,
                            EndUtc = end,
                            PatientRelationship = query.PatientRelationship
                        });
                    }
                }
            }
        }

        var result = slots.DistinctBy(x => new
            { x.ProviderId, x.LocationId, x.AppointmentTypeId, x.StartUtc, x.EndUtc })
            .OrderBy(x => x.StartUtc).ThenBy(x => x.ProviderId).ThenBy(x => x.AppointmentTypeId).ToList();
        logger.LogInformation(
            "Calculated external scheduling availability for tenant {TenantId}, channel {Channel}: {SlotCount} slots; " +
            "{ScheduleCount} schedules, {AppointmentTypeCount} appointment types, {CollisionCount} appointment collisions, " +
            "{BlockedCount} blocked-time collisions",
            query.TenantId, query.Channel, result.Count, schedules.Count, appointmentTypes.Count, collisionCount, blockedCount);
        return result;
    }

    private IReadOnlyList<SchedulingAvailabilitySlot> NoAvailability(
        SchedulingAvailabilityQuery query, string reason)
    {
        logger.LogInformation(
            "No external scheduling availability for tenant {TenantId}, channel {Channel}. Reason: {Reason}",
            query.TenantId, query.Channel, reason);
        return [];
    }

    private static HashSet<string> MappingIds(
        IEnumerable<ExternalSchedulingResourceMapping> mappings, SchedulingResourceType type) =>
        mappings.Where(x => x.ResourceType == type).Select(x => x.InternalId).ToHashSet(StringComparer.Ordinal);

    private static bool TryUtc(DateOnly date, TimeOnly time, TimeZoneInfo timeZone, out DateTimeOffset utc)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }
        utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero);
        return true;
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;
    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    private static void Validate(SchedulingAvailabilityQuery query)
    {
        SchedulingIntegrationConfigurationStore.ValidateTenant(query.TenantId);
        if (query.Channel is not (SchedulingChannel.PublicWebsite or SchedulingChannel.Zocdoc or SchedulingChannel.Google))
            throw new UnsupportedSchedulingChannelException(query.Channel);
        if (query.ToUtc <= query.FromUtc) throw new ArgumentException("Availability end must be after start.", nameof(query));
        if (query.PatientRelationship == PatientRelationship.Unknown)
            throw new ArgumentException("Patient relationship must be New or Existing.", nameof(query));
        if (query.ProviderId <= 0) throw new ArgumentException("ProviderId must be positive.", nameof(query));
        if (query.LocationId == Guid.Empty) throw new ArgumentException("LocationId must not be empty.", nameof(query));
    }
}

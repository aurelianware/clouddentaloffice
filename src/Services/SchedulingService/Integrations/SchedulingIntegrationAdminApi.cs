using System.Security.Claims;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;
using SchedulingService.Integrations.Zocdoc;

public static class SchedulingIntegrationAdminApi
{
    public sealed record SchedulingIntegrationOverview(
        SchedulingChannel Channel, bool Enabled, string Environment, string ConnectionStatus,
        bool CredentialReferenceConfigured, string? CredentialReference, string TimeZoneId,
        int MinimumBookingLeadMinutes, int MaximumBookingHorizonDays,
        DateTime? LastSuccessfulSynchronization, string? LastError,
        int MappedProviders, int MappedLocations, int MappedVisitReasons);
    public sealed record UpdateSchedulingIntegrationConfiguration(
        bool Enabled, string Environment, string? CredentialReference, string TimeZoneId,
        int MinimumBookingLeadMinutes, int MaximumBookingHorizonDays);

    public static IEndpointRouteBuilder MapSchedulingIntegrationAdminApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/scheduling-integrations/zocdoc/overview", async (
            ClaimsPrincipal user, SchedulingDbContext db, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, async tenantId =>
            {
                var configuration = await db.SchedulingIntegrationConfigurations.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc, cancellationToken);
                var mappings = await db.ExternalSchedulingResourceMappings.AsNoTracking().Where(x =>
                    x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc && x.IsActive)
                    .GroupBy(x => x.ResourceType).Select(x => new { Type = x.Key, Count = x.Count() })
                    .ToDictionaryAsync(x => x.Type, x => x.Count, cancellationToken);
                var availability = await db.SchedulingAvailabilitySyncStates.AsNoTracking().Where(x =>
                    x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc)
                    .OrderByDescending(x => x.LastAttemptAt).FirstOrDefaultAsync(cancellationToken);
                var lifecycle = await db.ExternalAppointmentReferences.AsNoTracking().Where(x =>
                    x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc &&
                    x.SyncStatus != ExternalAppointmentSyncStatus.Synced)
                    .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
                var lastSuccess = await db.SchedulingAvailabilitySyncStates.AsNoTracking().Where(x =>
                    x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc && x.LastSuccessAt.HasValue)
                    .MaxAsync(x => (DateTime?)x.LastSuccessAt, cancellationToken);
                var lastError = lifecycle?.LastSyncError ??
                    (availability?.Status is AvailabilitySyncStatus.Failed or AvailabilitySyncStatus.SkippedMapping
                        ? availability.Diagnostic : null);
                return new SchedulingIntegrationOverview(SchedulingChannel.Zocdoc,
                    configuration?.Enabled == true, configuration?.Environment ?? "Sandbox",
                    configuration is null ? "Not configured" : configuration.Enabled ? "Configured" : "Disabled",
                    !string.IsNullOrWhiteSpace(configuration?.CredentialReference), configuration?.CredentialReference,
                    configuration?.TimeZoneId ?? "UTC", configuration?.MinimumBookingLeadMinutes ?? 0,
                    configuration?.MaximumBookingHorizonDays ?? 90, lastSuccess, lastError,
                    mappings.GetValueOrDefault(SchedulingResourceType.Provider),
                    mappings.GetValueOrDefault(SchedulingResourceType.Location),
                    mappings.GetValueOrDefault(SchedulingResourceType.VisitReason));
            })).RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapPut("/api/scheduling-integrations/{channel}/configuration", async (
            SchedulingChannel channel, UpdateSchedulingIntegrationConfiguration request, ClaimsPrincipal user, SchedulingDbContext db,
            CancellationToken cancellationToken) => await ExecuteAsync(user, async tenantId =>
            {
                if (channel is not (SchedulingChannel.PublicWebsite or SchedulingChannel.Zocdoc or SchedulingChannel.Google))
                    throw new UnsupportedSchedulingChannelException(channel);
                if (request.Environment is not ("Sandbox" or "Production"))
                    throw new ArgumentException("Environment must be Sandbox or Production.");
                if (request.MinimumBookingLeadMinutes < 0 || request.MaximumBookingHorizonDays is < 1 or > 365)
                    throw new ArgumentException("Booking limits are invalid.");
                try { _ = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                { throw new ArgumentException("Time zone is invalid."); }
                var configuration = await db.SchedulingIntegrationConfigurations.SingleOrDefaultAsync(x =>
                    x.TenantId == tenantId && x.Channel == channel, cancellationToken);
                if (configuration is null)
                {
                    configuration = new() { TenantId = tenantId, Channel = channel };
                    db.SchedulingIntegrationConfigurations.Add(configuration);
                }
                configuration.Enabled = request.Enabled;
                configuration.Environment = request.Environment;
                configuration.CredentialReference = string.IsNullOrWhiteSpace(request.CredentialReference)
                    ? null : request.CredentialReference.Trim();
                configuration.TimeZoneId = request.TimeZoneId;
                configuration.MinimumBookingLeadMinutes = request.MinimumBookingLeadMinutes;
                configuration.MaximumBookingHorizonDays = request.MaximumBookingHorizonDays;
                configuration.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return new { saved = true };
            })).RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapPost("/api/scheduling-integrations/zocdoc/test-connection", async (
            ClaimsPrincipal user, ISchedulingChannelAdapterResolver resolver, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, async tenantId =>
            {
                var adapter = await resolver.ResolveAsync(tenantId, SchedulingChannel.Zocdoc, cancellationToken);
                if (adapter is not ISchedulingExternalEntitySource source)
                    throw new InvalidOperationException("Zocdoc connection testing is unavailable.");
                await source.ValidateConnectionAsync(tenantId, cancellationToken);
                return new { status = "Connected" };
            })).RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapGet("/api/scheduling-integrations/zocdoc/readiness", async (
            bool probeAuthentication, ClaimsPrincipal user, IZocdocOperationsService operations,
            CancellationToken cancellationToken) => await ExecuteAsync(user, tenantId =>
                operations.GetReadinessAsync(tenantId, probeAuthentication, cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapGet("/api/scheduling-integrations/zocdoc/reconciliation", async (
            int staleAfterMinutes, ClaimsPrincipal user, IZocdocOperationsService operations,
            CancellationToken cancellationToken) => await ExecuteAsync(user, tenantId =>
                operations.ReconcileAsync(tenantId,
                    TimeSpan.FromMinutes(staleAfterMinutes == 0 ? 1440 : staleAfterMinutes), cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapPost("/api/scheduling-integrations/zocdoc/external-entities/refresh", async (
            ClaimsPrincipal user, ISchedulingChannelAdapterResolver resolver, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, async tenantId =>
            {
                var adapter = await resolver.ResolveAsync(tenantId, SchedulingChannel.Zocdoc, cancellationToken);
                if (adapter is not ISchedulingExternalEntitySource source)
                    throw new InvalidOperationException("Zocdoc external entities are unavailable.");
                return await source.GetExternalEntitiesAsync(tenantId, cancellationToken);
            })).RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrations");

        endpoints.MapGet("/api/scheduling-integrations/zocdoc/appointments/status", async (
            ClaimsPrincipal user, SchedulingDbContext db, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, tenantId => db.ExternalAppointmentReferences.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc)
                .OrderByDescending(x => x.UpdatedAt).Take(500).ToListAsync(cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrationAppointments");

        endpoints.MapPost("/api/scheduling-integrations/zocdoc/availability/reconcile", async (
            DateTimeOffset from, DateTimeOffset to, int? providerId, ClaimsPrincipal user,
            IZocdocAvailabilitySynchronizer synchronizer, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, tenantId => synchronizer.ReconcileAsync(
                new(tenantId, from, to, providerId), cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrationAvailability");

        endpoints.MapGet("/api/scheduling-integrations/zocdoc/availability/status", async (
            ClaimsPrincipal user, SchedulingDbContext db, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, tenantId => db.SchedulingAvailabilitySyncStates.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Channel == SchedulingChannel.Zocdoc)
                .OrderByDescending(x => x.LastAttemptAt).Take(500).ToListAsync(cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin").WithTags("SchedulingIntegrationAvailability");

        endpoints.MapGet("/api/scheduling-integrations/{channel}/availability", async (
            SchedulingChannel channel, DateTimeOffset from, DateTimeOffset to, int? providerId,
            Guid? locationId, string? appointmentTypeId, PatientRelationship patientRelationship,
            ClaimsPrincipal user, ISchedulingAvailabilityService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, tenantId => service.GetAvailabilityAsync(new SchedulingAvailabilityQuery
            {
                TenantId = tenantId,
                Channel = channel,
                ProviderId = providerId,
                LocationId = locationId,
                AppointmentTypeId = appointmentTypeId,
                PatientRelationship = patientRelationship,
                FromUtc = from,
                ToUtc = to
            }, cancellationToken)))
            .RequireAuthorization("SchedulingIntegrationAdmin")
            .WithTags("SchedulingIntegrationAvailability");

        var group = endpoints.MapGroup("/api/scheduling-integrations/{channel}/mappings")
            .RequireAuthorization("SchedulingIntegrationAdmin")
            .WithTags("SchedulingIntegrationMappings");

        group.MapGet("/", async (SchedulingChannel channel, SchedulingResourceType? entityType,
            bool includeInactive, ClaimsPrincipal user, ISchedulingEntityMappingService service,
            CancellationToken cancellationToken) => await ExecuteAsync(user, tenantId =>
                service.ListAsync(tenantId, channel, entityType, includeInactive, cancellationToken)));

        group.MapGet("/{id:guid}", async (SchedulingChannel channel, Guid id, ClaimsPrincipal user,
            ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteNullableAsync(user, tenantId =>
                service.FindByIdAsync(tenantId, channel, id, cancellationToken)));

        group.MapGet("/by-internal/{entityType}/{internalId}", async (SchedulingChannel channel,
            SchedulingResourceType entityType, string internalId, ClaimsPrincipal user,
            ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteNullableAsync(user, tenantId => service.FindByInternalIdAsync(
                tenantId, channel, entityType, internalId, cancellationToken)));

        group.MapGet("/by-external/{entityType}/{externalId}", async (SchedulingChannel channel,
            SchedulingResourceType entityType, string externalId, ClaimsPrincipal user,
            ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteNullableAsync(user, tenantId => service.FindByExternalIdAsync(
                tenantId, channel, entityType, externalId, cancellationToken)));

        group.MapGet("/unmapped/{entityType}", async (SchedulingChannel channel,
            SchedulingResourceType entityType, ClaimsPrincipal user, ISchedulingEntityMappingService service,
            CancellationToken cancellationToken) => await ExecuteAsync(user, tenantId =>
                service.ListUnmappedAsync(tenantId, channel, entityType, cancellationToken)));

        group.MapGet("/invalid", async (SchedulingChannel channel, ClaimsPrincipal user,
            ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, tenantId => service.ListInvalidAsync(tenantId, channel, cancellationToken)));

        group.MapPost("/", async (SchedulingChannel channel, UpsertSchedulingEntityMapping request,
            ClaimsPrincipal user, ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, async tenantId =>
            {
                var mapping = await service.UpsertAsync(tenantId, channel, request, cancellationToken: cancellationToken);
                return Results.Created($"/api/scheduling-integrations/{channel}/mappings/{mapping.Id}", mapping);
            }));

        group.MapPut("/{id:guid}", async (SchedulingChannel channel, Guid id,
            UpsertSchedulingEntityMapping request, ClaimsPrincipal user, ISchedulingEntityMappingService service,
            CancellationToken cancellationToken) => await ExecuteAsync(user, tenantId =>
                service.UpsertAsync(tenantId, channel, request, id, cancellationToken)));

        group.MapDelete("/{id:guid}", async (SchedulingChannel channel, Guid id, ClaimsPrincipal user,
            ISchedulingEntityMappingService service, CancellationToken cancellationToken) =>
            await ExecuteAsync(user, async tenantId =>
            {
                await service.DeactivateAsync(tenantId, channel, id, cancellationToken);
                return Results.NoContent();
            }));

        return endpoints;
    }

    internal static string? TenantId(ClaimsPrincipal user) =>
        user.FindFirstValue("tenant_id") ?? user.FindFirstValue("tenantId") ?? user.FindFirstValue("TenantId");

    private static async Task<IResult> ExecuteNullableAsync<T>(ClaimsPrincipal user, Func<string, Task<T?>> action)
        where T : class
    {
        var result = await ExecuteCoreAsync(user, action);
        return result.Result ?? (result.Value is null ? Results.NotFound() : Results.Ok(result.Value));
    }

    private static async Task<IResult> ExecuteAsync<T>(ClaimsPrincipal user, Func<string, Task<T>> action)
    {
        var result = await ExecuteCoreAsync(user, action);
        return result.Result ?? Results.Ok(result.Value);
    }

    private static async Task<(T? Value, IResult? Result)> ExecuteCoreAsync<T>(
        ClaimsPrincipal user, Func<string, Task<T>> action)
    {
        var tenantId = TenantId(user);
        if (string.IsNullOrWhiteSpace(tenantId)) return (default, Results.Unauthorized());
        try { return (await action(tenantId), null); }
        catch (ArgumentException ex)
        {
            return (default, Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] }));
        }
        catch (KeyNotFoundException) { return (default, Results.NotFound()); }
        catch (UnsupportedSchedulingChannelException ex) { return (default, Results.BadRequest(new { message = ex.Message })); }
        catch (ZocdocIntegrationException ex)
        {
            return (default, Results.Problem(
                title: "Zocdoc operation failed.",
                detail: ex.Kind.ToString(),
                statusCode: StatusCodes.Status502BadGateway));
        }
        catch (InvalidOperationException ex) { return (default, Results.Conflict(new { message = ex.Message })); }
    }
}

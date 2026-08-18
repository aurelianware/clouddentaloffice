using System.Security.Claims;
using CloudDentalOffice.Contracts.Scheduling;

public static class SchedulingIntegrationAdminApi
{
    public static IEndpointRouteBuilder MapSchedulingIntegrationAdminApi(this IEndpointRouteBuilder endpoints)
    {
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
            .RequireAuthorization()
            .WithTags("SchedulingIntegrationAvailability");

        var group = endpoints.MapGroup("/api/scheduling-integrations/{channel}/mappings")
            .RequireAuthorization()
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
        catch (InvalidOperationException ex) { return (default, Results.Conflict(new { message = ex.Message })); }
    }
}

using System.Net.Http.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace SchedulingService.Integrations.Zocdoc;

internal interface IExternalPatientResolver
{
    Task<MatchOrCreateExternalPatientResult> ResolveAsync(string tenantId, ZocdocPatientDto patient,
        CancellationToken cancellationToken);
}

internal sealed class ExternalPatientResolver(HttpClient client, IConfiguration configuration) : IExternalPatientResolver
{
    public async Task<MatchOrCreateExternalPatientResult> ResolveAsync(string tenantId, ZocdocPatientDto patient,
        CancellationToken cancellationToken)
    {
        var apiKey = configuration.GetSection("Services:PatientServiceClients").GetChildren()
            .FirstOrDefault(x => string.Equals(x["TenantId"], tenantId, StringComparison.Ordinal))?["ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("PatientService authorization is not configured for this tenant.");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/internal/patients/match-or-create?tenantId={Uri.EscapeDataString(tenantId)}")
        {
            Content = JsonContent.Create(new MatchOrCreateExternalPatientRequest
            {
                DeveloperPatientId = patient.DeveloperPatientId, FirstName = patient.FirstName,
                LastName = patient.LastName, DateOfBirth = patient.DateOfBirth,
                Gender = patient.SexAtBirth, Email = patient.EmailAddress, Phone = patient.PhoneNumber
            })
        };
        request.Headers.Add("X-CDO-Service-Key", apiKey);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MatchOrCreateExternalPatientResult>(cancellationToken)
            ?? throw new InvalidOperationException("Patient service returned an empty result.");
    }
}

internal interface IZocdocAppointmentWebhookProcessor
{
    Task ProcessAsync(ZocdocAppointmentWebhookEvent webhook, CancellationToken cancellationToken = default);
}

internal sealed class ZocdocAppointmentWebhookProcessor(
    SchedulingDbContext db, ISchedulingIntegrationConfigurationStore configurations,
    ISchedulingIntegrationIdempotencyStore idempotency, IZocdocApiClient api,
    IExternalPatientResolver patients, ISchedulingAvailabilityService availability,
    ILogger<ZocdocAppointmentWebhookProcessor> logger)
    : IZocdocAppointmentWebhookProcessor
{
    public async Task ProcessAsync(ZocdocAppointmentWebhookEvent webhook,
        CancellationToken cancellationToken = default)
    {
        var configuration = await configurations.GetAsync(webhook.TenantId, SchedulingChannel.Zocdoc, cancellationToken);
        if (configuration is not { Enabled: true }) throw new SchedulingIntegrationDisabledException(SchedulingChannel.Zocdoc);
        var lease = await idempotency.TryBeginAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
            webhook.ExternalEventId, cancellationToken);
        if (!lease.Acquired) return;

        try
        {
            var existingReference = await db.ExternalAppointmentReferences.SingleOrDefaultAsync(x =>
                x.TenantId == webhook.TenantId && x.Channel == SchedulingChannel.Zocdoc &&
                x.ExternalAppointmentId == webhook.AppointmentId, cancellationToken);
            if (string.Equals(webhook.UpdateType, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                if (existingReference is null) throw new InvalidOperationException("External appointment reference not found.");
                if (existingReference.SyncStatus == ExternalAppointmentSyncStatus.Pending &&
                    existingReference.PendingOperation != "cancel")
                {
                    existingReference.SyncStatus = ExternalAppointmentSyncStatus.Conflict;
                    existingReference.LastSyncError = "Zocdoc cancelled while a different local change was pending.";
                    existingReference.LastExternalUpdatedAt = webhook.ExternalUpdatedAt;
                    existingReference.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await idempotency.CompleteAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
                        webhook.ExternalEventId, existingReference.AppointmentId, cancellationToken);
                    return;
                }
                var existing = await db.Appointments.SingleAsync(x => x.Id == existingReference.AppointmentId &&
                    x.TenantId == webhook.TenantId, cancellationToken);
                existing.Status = AppointmentStatus.Cancelled;
                existingReference.SyncStatus = ExternalAppointmentSyncStatus.Synced;
                existingReference.PendingOperation = null;
                existingReference.PendingStartUtc = null;
                existingReference.LastSyncError = null;
                existingReference.LastSyncedAt = DateTime.UtcNow;
                existingReference.LastExternalUpdatedAt = webhook.ExternalUpdatedAt;
                existingReference.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await idempotency.CompleteAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
                    webhook.ExternalEventId, existing.Id, cancellationToken);
                return;
            }

            var remote = await api.GetAppointmentAsync(webhook.TenantId, configuration,
                webhook.AppointmentId, cancellationToken);

            var (externalProviderId, externalLocationId) = SplitProviderLocation(remote.ProviderLocationId);
            var provider = await MappingAsync(webhook.TenantId, SchedulingResourceType.Provider,
                externalProviderId, cancellationToken);
            var location = await MappingAsync(webhook.TenantId, SchedulingResourceType.Location,
                externalLocationId, cancellationToken);
            var visitReason = await MappingAsync(webhook.TenantId, SchedulingResourceType.VisitReason,
                remote.VisitReasonId, cancellationToken);
            if (!int.TryParse(provider.InternalId, out var providerId) ||
                !Guid.TryParse(location.InternalId, out var locationId))
                throw new InvalidOperationException("Zocdoc mapping is invalid.");
            var appointmentType = await db.SchedulingAppointmentTypes.AsNoTracking().SingleAsync(x =>
                x.TenantId == webhook.TenantId && x.AppointmentTypeId == visitReason.InternalId && x.IsActive,
                cancellationToken);
            var startUtc = remote.StartTime.UtcDateTime;
            var endUtc = startUtc.AddMinutes(appointmentType.DurationMinutes);

            if (existingReference is { SyncStatus: ExternalAppointmentSyncStatus.Pending })
            {
                var remoteOperation = RemoteOperation(remote.Status, existingReference, startUtc);
                if (!string.Equals(existingReference.PendingOperation, remoteOperation, StringComparison.Ordinal))
                {
                    existingReference.SyncStatus = ExternalAppointmentSyncStatus.Conflict;
                    existingReference.LastSyncError = "Zocdoc changed the appointment while a different local change was pending.";
                    existingReference.LastExternalUpdatedAt = webhook.ExternalUpdatedAt;
                    existingReference.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await idempotency.CompleteAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
                        webhook.ExternalEventId, existingReference.AppointmentId, cancellationToken);
                    return;
                }
            }

            var targetId = existingReference?.AppointmentId;
            if (existingReference is null)
            {
                var relationship = string.Equals(remote.PatientType, "new", StringComparison.OrdinalIgnoreCase)
                    ? PatientRelationship.New : string.Equals(remote.PatientType, "existing", StringComparison.OrdinalIgnoreCase)
                        ? PatientRelationship.Existing : PatientRelationship.Unknown;
                var offered = await availability.GetAvailabilityAsync(new SchedulingAvailabilityQuery
                {
                    TenantId = webhook.TenantId, Channel = SchedulingChannel.Zocdoc,
                    ProviderId = providerId, LocationId = locationId,
                    AppointmentTypeId = visitReason.InternalId, PatientRelationship = relationship,
                    FromUtc = new DateTimeOffset(startUtc), ToUtc = new DateTimeOffset(endUtc)
                }, cancellationToken);
                if (!offered.Any(x => x.StartUtc.UtcDateTime == startUtc && x.EndUtc.UtcDateTime == endUtc))
                    throw new InvalidOperationException("The requested appointment slot is no longer available.");
            }
            var collision = await db.Appointments.AsNoTracking().AnyAsync(x => x.TenantId == webhook.TenantId &&
                x.ProviderId == providerId && x.Status != AppointmentStatus.Cancelled && x.Status != AppointmentStatus.Requested &&
                x.Id != targetId && x.StartTime < endUtc && x.EndTime > startUtc, cancellationToken);
            if (collision) throw new InvalidOperationException("The requested appointment slot is no longer available.");

            Appointment appointment;
            if (existingReference is not null)
            {
                appointment = await db.Appointments.SingleAsync(x => x.Id == existingReference.AppointmentId, cancellationToken);
                appointment.ProviderId = providerId; appointment.LocationId = locationId;
                appointment.AppointmentTypeId = visitReason.InternalId;
                appointment.StartTime = startUtc; appointment.EndTime = endUtc;
                appointment.Status = RemoteStatus(remote.Status, appointment.Status);
            }
            else
            {
                if (remote.Patient is null) throw new InvalidOperationException("Zocdoc appointment has no patient.");
                var patient = await patients.ResolveAsync(webhook.TenantId, remote.Patient, cancellationToken);
                appointment = new Appointment
                {
                    Id = Guid.NewGuid(), TenantId = webhook.TenantId, PatientId = patient.PatientId,
                    ProviderId = providerId, LocationId = locationId, AppointmentTypeId = visitReason.InternalId,
                    StartTime = startUtc, EndTime = endUtc, Status = AppointmentStatus.Scheduled,
                    CreatedAt = DateTime.UtcNow
                };
                db.Appointments.Add(appointment);
                db.ExternalAppointmentReferences.Add(new ExternalAppointmentReference
                {
                    TenantId = webhook.TenantId, AppointmentId = appointment.Id, Channel = SchedulingChannel.Zocdoc,
                    ExternalAppointmentId = webhook.AppointmentId, ExternalProviderId = externalProviderId,
                    ExternalLocationId = externalLocationId, ExternalVisitReasonId = remote.VisitReasonId,
                    SyncStatus = ExternalAppointmentSyncStatus.Synced, LastSyncedAt = DateTime.UtcNow,
                    LastExternalUpdatedAt = webhook.ExternalUpdatedAt
                });
            }
            if (existingReference is not null)
            {
                existingReference.ExternalProviderId = externalProviderId;
                existingReference.ExternalLocationId = externalLocationId;
                existingReference.ExternalVisitReasonId = remote.VisitReasonId;
                existingReference.SyncStatus = ExternalAppointmentSyncStatus.Synced;
                existingReference.PendingOperation = null;
                existingReference.PendingStartUtc = null;
                existingReference.LastSyncError = null;
                existingReference.LastSyncedAt = existingReference.UpdatedAt = DateTime.UtcNow;
                existingReference.LastExternalUpdatedAt = webhook.ExternalUpdatedAt;
            }
            await db.SaveChangesAsync(cancellationToken);
            if (string.Equals(remote.Status, "pending_booking", StringComparison.OrdinalIgnoreCase))
                await api.ConfirmAppointmentAsync(webhook.TenantId, configuration, webhook.AppointmentId, cancellationToken);
            await idempotency.CompleteAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
                webhook.ExternalEventId, appointment.Id, cancellationToken);
            logger.LogInformation("Processed Zocdoc appointment event {ExternalEventId} for tenant {TenantId} with result {Result}",
                webhook.ExternalEventId, webhook.TenantId, existingReference is null ? "Created" : "Updated");
        }
        catch (Exception ex)
        {
            await idempotency.FailAsync(webhook.TenantId, SchedulingChannel.Zocdoc,
                webhook.ExternalEventId, ex.GetType().Name, cancellationToken);
            throw;
        }
    }

    private async Task<ExternalSchedulingResourceMapping> MappingAsync(string tenantId,
        SchedulingResourceType type, string externalId, CancellationToken cancellationToken)
    {
        var mapping = await db.ExternalSchedulingResourceMappings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId &&
            x.Channel == SchedulingChannel.Zocdoc && x.ResourceType == type && x.ExternalId == externalId && x.IsActive,
            cancellationToken);
        return mapping ?? throw new InvalidOperationException($"Required {type} mapping is missing.");
    }

    private static (string ProviderId, string LocationId) SplitProviderLocation(string value)
    {
        var parts = value.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Zocdoc provider-location identifier is malformed.");
        return (parts[0], parts[1]);
    }

    private static string? RemoteOperation(string status, ExternalAppointmentReference reference, DateTime startUtc) =>
        status switch
        {
            "cancelled" => "cancel",
            "arrived" => "arrived",
            "no_show" => "no_show",
            "rescheduled" when reference.PendingStartUtc == startUtc => "reschedule",
            _ => null
        };

    private static AppointmentStatus RemoteStatus(string status, AppointmentStatus current) => status switch
    {
        "confirmed" => AppointmentStatus.Confirmed,
        "cancelled" => AppointmentStatus.Cancelled,
        "arrived" => AppointmentStatus.CheckedIn,
        "no_show" => AppointmentStatus.NoShow,
        "rescheduled" => AppointmentStatus.Rescheduled,
        _ => current
    };
}

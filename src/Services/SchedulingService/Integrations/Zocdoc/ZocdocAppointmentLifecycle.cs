using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;
using Microsoft.EntityFrameworkCore;

namespace SchedulingService.Integrations.Zocdoc;

public sealed record AppointmentLifecycleCommand(
    AppointmentStatus Status, DateTime? StartUtc = null, DateTime? EndUtc = null);

public interface IAppointmentLifecycleService
{
    Task<Appointment> ApplyLocalAsync(string tenantId, Guid appointmentId, AppointmentLifecycleCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class AppointmentLifecycleService(
    SchedulingDbContext db, IEventPublisher events, ILogger<AppointmentLifecycleService> logger)
    : IAppointmentLifecycleService
{
    public async Task<Appointment> ApplyLocalAsync(string tenantId, Guid appointmentId,
        AppointmentLifecycleCommand command, CancellationToken cancellationToken = default)
    {
        var appointment = await db.Appointments.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.Id == appointmentId, cancellationToken)
            ?? throw new KeyNotFoundException("Appointment not found.");
        var operation = command.Status switch
        {
            AppointmentStatus.Cancelled => "cancel",
            AppointmentStatus.Rescheduled => "reschedule",
            AppointmentStatus.CheckedIn => "arrived",
            AppointmentStatus.NoShow => "no_show",
            _ => throw new ArgumentException("Only cancel, reschedule, arrived, and no-show lifecycle changes are supported.")
        };
        if (operation == "reschedule")
        {
            if (command.StartUtc is null || command.EndUtc is null || command.EndUtc <= command.StartUtc)
                throw new ArgumentException("Reschedule requires a valid start and end time.");
            appointment.StartTime = SchedulingTime.NormalizeUtc(command.StartUtc.Value);
            appointment.EndTime = SchedulingTime.NormalizeUtc(command.EndUtc.Value);
        }
        if (operation is "arrived" or "no_show")
        {
            var now = DateTime.UtcNow;
            if (appointment.StartTime > now)
                throw new ArgumentException("Arrived and no-show may only be set after the appointment starts.");
            if (operation == "no_show" && appointment.StartTime < now.AddDays(-2))
                throw new ArgumentException("Zocdoc only accepts no-show updates within two days of the appointment.");
        }
        appointment.Status = command.Status;

        var reference = await db.ExternalAppointmentReferences.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.AppointmentId == appointmentId && x.Channel == SchedulingChannel.Zocdoc,
            cancellationToken);
        if (reference is not null)
        {
            reference.SyncStatus = ExternalAppointmentSyncStatus.Pending;
            reference.PendingOperation = operation;
            reference.PendingStartUtc = operation == "reschedule" ? appointment.StartTime : null;
            reference.LastSyncError = null;
            reference.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);

        if (reference is not null)
        {
            try
            {
                await events.PublishAsync(new AppointmentLifecycleChangedEvent(
                    tenantId, appointmentId, operation, "CloudDentalOffice", reference.PendingStartUtc), cancellationToken);
            }
            catch (Exception ex)
            {
                reference.SyncStatus = ExternalAppointmentSyncStatus.Failed;
                reference.LastSyncError = "EventPublishFailed";
                reference.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                logger.LogError(ex, "Could not enqueue Zocdoc lifecycle sync for tenant {TenantId}, appointment {AppointmentId}",
                    tenantId, appointmentId);
            }
        }
        return appointment;
    }
}

internal interface IZocdocAppointmentLifecycleSynchronizer
{
    Task SynchronizeAsync(AppointmentLifecycleChangedEvent evt, CancellationToken cancellationToken = default);
}

internal sealed class PermanentLifecycleSyncException(string message) : InvalidOperationException(message);

internal sealed class ZocdocAppointmentLifecycleSynchronizer(
    SchedulingDbContext db, ISchedulingIntegrationConfigurationStore configurations, IZocdocApiClient api)
    : IZocdocAppointmentLifecycleSynchronizer
{
    public async Task SynchronizeAsync(AppointmentLifecycleChangedEvent evt,
        CancellationToken cancellationToken = default)
    {
        if (evt.Source != "CloudDentalOffice") return; // explicit loop prevention
        var reference = await db.ExternalAppointmentReferences.SingleOrDefaultAsync(x =>
            x.TenantId == evt.TenantId && x.AppointmentId == evt.AppointmentId && x.Channel == SchedulingChannel.Zocdoc,
            cancellationToken) ?? throw new PermanentLifecycleSyncException("External appointment mapping is missing.");
        if (reference.SyncStatus == ExternalAppointmentSyncStatus.Synced && reference.PendingOperation is null) return;
        if (evt.Operation is not ("cancel" or "reschedule" or "arrived" or "no_show"))
        {
            reference.SyncStatus = ExternalAppointmentSyncStatus.Failed;
            reference.LastSyncError = "UnsupportedOperation";
            reference.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new PermanentLifecycleSyncException("Unsupported lifecycle operation.");
        }
        var configuration = await configurations.GetAsync(evt.TenantId, SchedulingChannel.Zocdoc, cancellationToken);
        if (configuration is not { Enabled: true })
        {
            reference.SyncStatus = ExternalAppointmentSyncStatus.Failed;
            reference.LastSyncError = "DisabledIntegration";
            reference.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new PermanentLifecycleSyncException("Zocdoc integration is disabled.");
        }
        var appointment = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == evt.TenantId && x.Id == evt.AppointmentId, cancellationToken)
            ?? throw new PermanentLifecycleSyncException("Local appointment is missing.");
        if (string.IsNullOrWhiteSpace(reference.ExternalAppointmentId) ||
            string.IsNullOrWhiteSpace(reference.ExternalProviderId) ||
            string.IsNullOrWhiteSpace(reference.ExternalLocationId) ||
            string.IsNullOrWhiteSpace(reference.ExternalVisitReasonId))
        {
            await ConflictAsync(reference, "External appointment mapping is incomplete.", cancellationToken);
            throw new PermanentLifecycleSyncException("External appointment mapping is incomplete.");
        }
        if (!string.Equals(reference.PendingOperation, evt.Operation, StringComparison.Ordinal) ||
            evt.Operation == "reschedule" && reference.PendingStartUtc != evt.StartUtc)
        {
            await ConflictAsync(reference, "A newer local lifecycle change superseded this event.", cancellationToken);
            throw new PermanentLifecycleSyncException("Lifecycle synchronization conflict.");
        }
        var expectedStatus = evt.Operation switch
        {
            "cancel" => AppointmentStatus.Cancelled,
            "reschedule" => AppointmentStatus.Rescheduled,
            "arrived" => AppointmentStatus.CheckedIn,
            "no_show" => AppointmentStatus.NoShow,
            _ => appointment.Status
        };
        if (appointment.Status != expectedStatus ||
            evt.Operation == "reschedule" && appointment.StartTime != evt.StartUtc)
        {
            await ConflictAsync(reference, "The local appointment no longer matches the queued lifecycle change.", cancellationToken);
            throw new PermanentLifecycleSyncException("Lifecycle synchronization conflict.");
        }

        try
        {
            switch (evt.Operation)
            {
                case "cancel":
                    await api.CancelAppointmentAsync(evt.TenantId, configuration, reference.ExternalAppointmentId, cancellationToken);
                    break;
                case "reschedule":
                    var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(appointment.StartTime, TimeSpan.Zero),
                        TimeZoneInfo.FindSystemTimeZoneById(configuration.TimeZoneId));
                    await api.RescheduleAppointmentAsync(evt.TenantId, configuration, reference.ExternalAppointmentId,
                        local, cancellationToken);
                    break;
                case "arrived":
                case "no_show":
                    await api.UpdateAppointmentStatusAsync(evt.TenantId, configuration,
                        reference.ExternalAppointmentId, evt.Operation, cancellationToken);
                    break;
            }
            reference.SyncStatus = ExternalAppointmentSyncStatus.Synced;
            reference.PendingOperation = null;
            reference.PendingStartUtc = null;
            reference.LastSyncError = null;
            reference.LastSyncedAt = reference.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (ZocdocIntegrationException ex)
        {
            reference.SyncStatus = ExternalAppointmentSyncStatus.Failed;
            reference.LastSyncError = ex.Kind.ToString();
            reference.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (ex.Kind is ZocdocFailureKind.Authentication or ZocdocFailureKind.Authorization or
                ZocdocFailureKind.RemoteValidation or ZocdocFailureKind.Misconfiguration)
                throw new PermanentLifecycleSyncException($"Permanent Zocdoc failure: {ex.Kind}.");
            throw;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            reference.SyncStatus = ExternalAppointmentSyncStatus.Failed;
            reference.LastSyncError = "InvalidTimeZone";
            reference.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            throw new PermanentLifecycleSyncException("The practice timezone configuration is invalid.");
        }
    }

    private async Task ConflictAsync(ExternalAppointmentReference reference, string diagnostic,
        CancellationToken cancellationToken)
    {
        reference.SyncStatus = ExternalAppointmentSyncStatus.Conflict;
        reference.LastSyncError = diagnostic;
        reference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

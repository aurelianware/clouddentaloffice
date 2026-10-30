using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentLifecycleConsumer(
    IServiceProvider services, ServiceBusOptions options, IMessageSubscriber subscriber, ILogger<ZocdocAppointmentLifecycleConsumer> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        await subscriber.RunAsync(options.AppointmentLifecycleTopic, options.AppointmentLifecycleSubscription, ProcessAsync, stoppingToken);
    }
    private async Task ProcessAsync(IReceivedMessage message)
    {
        AppointmentLifecycleChangedEvent? evt;
        try { evt = JsonSerializer.Deserialize<AppointmentLifecycleChangedEvent>(message.Body); }
        catch (JsonException) { await message.DeadLetterAsync("DeserializationError"); return; }
        if (message.Subject != nameof(AppointmentLifecycleChangedEvent) || evt is null ||
            string.IsNullOrWhiteSpace(evt.TenantId) || evt.AppointmentId == Guid.Empty)
        { await message.DeadLetterAsync("InvalidEvent"); return; }
        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IZocdocAppointmentLifecycleSynchronizer>()
                .SynchronizeAsync(evt, message.CancellationToken);
            await message.CompleteAsync();
        }
        catch (PermanentLifecycleSyncException ex)
        { await message.DeadLetterAsync("PermanentFailure", ex.Message); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transient Zocdoc lifecycle failure for tenant {TenantId}, appointment {AppointmentId}",
                evt.TenantId, evt.AppointmentId);
            await message.RetryAsync();
        }
    }
}

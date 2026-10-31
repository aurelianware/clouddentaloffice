using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentWebhookConsumer(
    IServiceProvider services, ServiceBusOptions options, IMessageSubscriber subscriber,
    ILogger<ZocdocAppointmentWebhookConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        await subscriber.RunAsync(options.ZocdocWebhookTopic, options.ZocdocWebhookSubscription, ProcessAsync, stoppingToken);
    }

    private async Task ProcessAsync(IReceivedMessage message)
    {
        if (!string.Equals(message.Subject, nameof(ZocdocAppointmentWebhookEvent), StringComparison.Ordinal))
        {
            await message.DeadLetterAsync("UnexpectedSubject");
            return;
        }

        ZocdocAppointmentWebhookEvent? evt;
        try { evt = JsonSerializer.Deserialize<ZocdocAppointmentWebhookEvent>(message.Body); }
        catch (JsonException)
        {
            await message.DeadLetterAsync("DeserializationError");
            return;
        }
        if (evt is null || string.IsNullOrWhiteSpace(evt.TenantId) ||
            string.IsNullOrWhiteSpace(evt.ExternalEventId) || string.IsNullOrWhiteSpace(evt.AppointmentId) ||
            evt.UpdateType is not ("created" or "updated" or "cancelled"))
        {
            await message.DeadLetterAsync("InvalidEvent");
            return;
        }

        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IZocdocAppointmentWebhookProcessor>()
                .ProcessAsync(evt, message.CancellationToken);
            await message.CompleteAsync();
        }
        catch (SchedulingIntegrationDisabledException)
        {
            await message.DeadLetterAsync("DisabledIntegration");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Zocdoc appointment event processing failed for tenant {TenantId}, event {ExternalEventId}",
                evt.TenantId, evt.ExternalEventId);
            await message.RetryAsync();
        }
    }
}

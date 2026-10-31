using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAvailabilityConsumer(
    IServiceProvider services, ServiceBusOptions options, IMessageSubscriber subscriber) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        await subscriber.RunAsync(options.SchedulingAvailabilityTopic, options.SchedulingAvailabilitySubscription, ProcessAsync, stoppingToken);
    }

    private async Task ProcessAsync(IReceivedMessage message)
    {
        if (!string.Equals(message.Subject, nameof(SchedulingAvailabilityChangedEvent), StringComparison.Ordinal))
        {
            await message.DeadLetterAsync("UnexpectedSubject");
            return;
        }
        SchedulingAvailabilityChangedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SchedulingAvailabilityChangedEvent>(message.Body);
        }
        catch (JsonException ex)
        {
            await message.DeadLetterAsync("DeserializationError", ex.Message);
            return;
        }
        if (evt is null || string.IsNullOrWhiteSpace(evt.TenantId) || evt.ToUtc <= evt.FromUtc)
        {
            await message.DeadLetterAsync("InvalidEvent");
            return;
        }
        await using var scope = services.CreateAsyncScope();
        var synchronizer = scope.ServiceProvider.GetRequiredService<IZocdocAvailabilitySynchronizer>();
        var result = await synchronizer.ReconcileAsync(new(evt.TenantId,
            new DateTimeOffset(evt.FromUtc), new DateTimeOffset(evt.ToUtc), evt.ProviderId), message.CancellationToken);
        if (result.Failed > 0)
            await message.RetryAsync();
        else
            await message.CompleteAsync();
    }
}

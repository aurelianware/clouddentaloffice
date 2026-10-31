using CloudDentalOffice.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Used when no message broker is configured. It logs a warning
/// and drops the event, so services still start and run locally without a
/// broker. Booking requests are NOT delivered in this mode.
/// </summary>
public sealed class NullEventPublisher(ILogger<NullEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "Messaging is not configured; dropping {EventType} {EventId}. Set ServiceBus:ConnectionString, or ServiceBus:Provider=PubSub with ServiceBus:PubSubProjectId, to enable publishing.",
            @event.GetType().Name, @event.EventId);
        return Task.CompletedTask;
    }
}

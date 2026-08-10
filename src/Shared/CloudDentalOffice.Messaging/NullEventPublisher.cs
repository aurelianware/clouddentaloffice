using CloudDentalOffice.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Used when no Service Bus connection string is configured. It logs a warning
/// and drops the event, so services still start and run locally without a
/// broker. Booking requests are NOT delivered in this mode.
/// </summary>
public sealed class NullEventPublisher(ILogger<NullEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "Service Bus is not configured; dropping {EventType} {EventId}. Set ServiceBus:ConnectionString to enable publishing.",
            @event.GetType().Name, @event.EventId);
        return Task.CompletedTask;
    }
}

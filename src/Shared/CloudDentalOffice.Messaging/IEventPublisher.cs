using CloudDentalOffice.Contracts.Events;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Publishes integration events to the message broker. Implementations serialize
/// the event to JSON and set the message subject to the event type name so
/// subscribers can filter/route by type.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default);
}

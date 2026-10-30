using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Publishes events to an Azure Service Bus topic. The message Subject is the
/// runtime event type name (e.g. "BookingRequestedEvent"), which subscriptions
/// can filter on with a correlation/subject rule.
/// </summary>
public sealed class ServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusOptions _options;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private readonly ILogger<ServiceBusEventPublisher> _logger;

    public ServiceBusEventPublisher(ServiceBusOptions options, ILogger<ServiceBusEventPublisher> logger)
    {
        // Only constructed when a connection string is configured (see
        // AddEventPublishing), so the null-forgiving operator is safe here.
        _client = new ServiceBusClient(options.ConnectionString!);
        _options = options;
        _logger = logger;
    }

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        var typeName = @event.GetType().Name;
        var body = JsonSerializer.Serialize(@event, @event.GetType());
        var message = new ServiceBusMessage(body)
        {
            Subject = typeName,
            ContentType = "application/json",
            MessageId = @event.EventId.ToString(),
            CorrelationId = @event.CorrelationId
        };

        var sender = _senders.GetOrAdd(_options.TopicFor(@event), _client.CreateSender);
        await sender.SendMessageAsync(message, cancellationToken);
        _logger.LogInformation("Published {EventType} {EventId} to Service Bus.", typeName, @event.EventId);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
            await sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

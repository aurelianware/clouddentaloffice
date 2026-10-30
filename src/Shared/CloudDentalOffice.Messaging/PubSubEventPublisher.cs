using System.Collections.Concurrent;
using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Publishes events to a Google Cloud Pub/Sub topic. Pub/Sub messages have no
/// subject, so the event type name goes in the "subject" attribute, which
/// subscriptions filter on (attributes.subject = "BookingRequestedEvent") and
/// <see cref="PubSubMessageSubscriber"/> reads back as the subject. Honors
/// PUBSUB_EMULATOR_HOST for local runs and tests.
/// </summary>
public sealed class PubSubEventPublisher(ServiceBusOptions options, ILogger<PubSubEventPublisher> logger)
    : IEventPublisher, IAsyncDisposable
{
    public const string SubjectAttribute = "subject";
    public const string MessageIdAttribute = "messageId";
    public const string CorrelationIdAttribute = "correlationId";

    private readonly ConcurrentDictionary<string, Lazy<Task<PublisherClient>>> _publishers = new();

    public async Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
    {
        var typeName = @event.GetType().Name;
        var message = new PubsubMessage
        {
            Data = ByteString.CopyFromUtf8(JsonSerializer.Serialize(@event, @event.GetType())),
            Attributes =
            {
                [SubjectAttribute] = typeName,
                [MessageIdAttribute] = @event.EventId.ToString(),
                ["contentType"] = "application/json"
            }
        };
        if (!string.IsNullOrEmpty(@event.CorrelationId))
            message.Attributes[CorrelationIdAttribute] = @event.CorrelationId;

        var publisher = await PublisherFor(options.TopicFor(@event));
        await publisher.PublishAsync(message).WaitAsync(cancellationToken);
        logger.LogInformation("Published {EventType} {EventId} to Pub/Sub.", typeName, @event.EventId);
    }

    internal Task<PublisherClient> PublisherFor(string topic) =>
        _publishers.GetOrAdd(topic, name => new Lazy<Task<PublisherClient>>(() => new PublisherClientBuilder
        {
            TopicName = TopicName.FromProjectTopic(options.PubSubProjectId!, name),
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction
        }.BuildAsync())).Value;

    public async ValueTask DisposeAsync()
    {
        foreach (var publisher in _publishers.Values.Where(p => p.IsValueCreated))
            await (await publisher.Value).DisposeAsync();
    }
}

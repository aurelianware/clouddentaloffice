namespace CloudDentalOffice.Messaging;

/// <summary>
/// One message received from a topic subscription. The handler must settle it
/// exactly once: complete it, return it for redelivery, or dead-letter it.
/// </summary>
public interface IReceivedMessage
{
    /// <summary>The event type name the publisher set (see <see cref="IEventPublisher"/>).</summary>
    string? Subject { get; }

    /// <summary>The JSON body.</summary>
    string Body { get; }

    CancellationToken CancellationToken { get; }

    /// <summary>The message was handled; remove it from the subscription.</summary>
    Task CompleteAsync();

    /// <summary>A transient failure; deliver the message again later.</summary>
    Task RetryAsync();

    /// <summary>The message can never succeed; move it aside for a person to inspect.</summary>
    Task DeadLetterAsync(string reason, string? description = null);
}

/// <summary>
/// Receives messages from a topic subscription on the configured broker
/// (Azure Service Bus or Google Cloud Pub/Sub), one at a time.
/// </summary>
public interface IMessageSubscriber
{
    /// <summary>
    /// Delivers messages to <paramref name="handler"/> until
    /// <paramref name="stoppingToken"/> is cancelled. A message the handler
    /// leaves unsettled, or that throws, is returned for redelivery.
    /// </summary>
    Task RunAsync(string topic, string subscription, Func<IReceivedMessage, Task> handler,
        CancellationToken stoppingToken);
}

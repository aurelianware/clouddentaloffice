using Google.Api.Gax;
using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>
/// Receives from a Google Cloud Pub/Sub subscription, one message at a time
/// like the Service Bus processors. Complete acks, retry nacks (the
/// subscription's retry and dead-letter policy apply), and dead-letter
/// publishes the message to "{topic}-dead-letter" with the reason, then acks.
/// </summary>
public sealed class PubSubMessageSubscriber(
    ServiceBusOptions options, PubSubEventPublisher publisher, ILogger<PubSubMessageSubscriber> logger)
    : IMessageSubscriber
{
    public const string DeadLetterReasonAttribute = "deadLetterReason";
    public const string DeadLetterDescriptionAttribute = "deadLetterDescription";

    public async Task RunAsync(string topic, string subscription, Func<IReceivedMessage, Task> handler,
        CancellationToken stoppingToken)
    {
        var subscriber = await new SubscriberClientBuilder
        {
            SubscriptionName = SubscriptionName.FromProjectSubscription(options.PubSubProjectId!, subscription),
            Settings = new SubscriberClient.Settings
            {
                FlowControlSettings = new FlowControlSettings(maxOutstandingElementCount: 1, maxOutstandingByteCount: null)
            },
            EmulatorDetection = EmulatorDetection.EmulatorOrProduction
        }.BuildAsync(stoppingToken);

        var deadLetterTopic = topic + options.PubSubDeadLetterTopicSuffix;
        // Like the Service Bus processor: finish the message in hand, then stop.
        using var stop = stoppingToken.Register(() => _ = subscriber.StopAsync(new SubscriberClient.ShutdownOptions
        {
            Mode = SubscriberClient.ShutdownMode.WaitForProcessing,
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None));
        logger.LogInformation("Listening on Pub/Sub {Topic}/{Subscription}.", topic, subscription);

        await subscriber.StartAsync(async (message, cancellationToken) =>
        {
            var received = new PubSubReceivedMessage(message, cancellationToken);
            try
            {
                await handler(received);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Handler for {Topic}/{Subscription} failed ({FailureKind}); the message will retry.",
                    topic, subscription, ex.GetType().Name);
                return SubscriberClient.Reply.Nack;
            }

            if (received.Outcome is not { } outcome)
                return SubscriberClient.Reply.Nack;
            if (outcome.DeadLetterReason is null)
                return outcome.Ack ? SubscriberClient.Reply.Ack : SubscriberClient.Reply.Nack;

            var deadLetter = message.Clone();
            deadLetter.MessageId = "";
            deadLetter.PublishTime = null;
            deadLetter.OrderingKey = "";
            deadLetter.Attributes[DeadLetterReasonAttribute] = outcome.DeadLetterReason;
            if (outcome.DeadLetterDescription is not null)
                deadLetter.Attributes[DeadLetterDescriptionAttribute] = outcome.DeadLetterDescription;
            try
            {
                await (await publisher.PublisherFor(deadLetterTopic)).PublishAsync(deadLetter);
            }
            catch (Exception ex)
            {
                // Not acked: the subscription's own dead-letter policy is the backstop.
                logger.LogError("Could not dead-letter a message to {Topic} ({FailureKind}); it will retry.",
                    deadLetterTopic, ex.GetType().Name);
                return SubscriberClient.Reply.Nack;
            }
            return SubscriberClient.Reply.Ack;
        });
    }

    private sealed record Outcome(bool Ack, string? DeadLetterReason = null, string? DeadLetterDescription = null);

    private sealed class PubSubReceivedMessage(PubsubMessage message, CancellationToken cancellationToken) : IReceivedMessage
    {
        public Outcome? Outcome { get; private set; }
        public string? Subject => message.Attributes.TryGetValue(PubSubEventPublisher.SubjectAttribute, out var s) ? s : null;
        public string Body => message.Data.ToStringUtf8();
        public CancellationToken CancellationToken => cancellationToken;

        public Task CompleteAsync() => Settle(new Outcome(Ack: true));
        public Task RetryAsync() => Settle(new Outcome(Ack: false));
        public Task DeadLetterAsync(string reason, string? description = null) =>
            Settle(new Outcome(Ack: true, reason, description));

        private Task Settle(Outcome outcome)
        {
            Outcome ??= outcome;
            return Task.CompletedTask;
        }
    }
}

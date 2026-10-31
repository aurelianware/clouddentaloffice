using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;

namespace CloudDentalOffice.Messaging;

/// <summary>Receives from an Azure Service Bus topic subscription.</summary>
public sealed class ServiceBusMessageSubscriber(string connectionString, ILogger<ServiceBusMessageSubscriber> logger)
    : IMessageSubscriber
{
    public async Task RunAsync(string topic, string subscription, Func<IReceivedMessage, Task> handler,
        CancellationToken stoppingToken)
    {
        await using var client = new ServiceBusClient(connectionString);
        await using var processor = client.CreateProcessor(topic, subscription, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false
        });
        processor.ProcessMessageAsync += async args =>
        {
            var message = new ServiceBusReceivedMessage(args);
            try
            {
                await handler(message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Handler for {Topic}/{Subscription} failed ({FailureKind}); the message will retry.",
                    topic, subscription, ex.GetType().Name);
            }
            if (!message.Settled)
                await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        };
        processor.ProcessErrorAsync += args =>
        {
            logger.LogError("Service Bus processing error on {Topic}/{Subscription} ({Source}, {FailureKind}).",
                topic, subscription, args.ErrorSource, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };

        await processor.StartProcessingAsync(stoppingToken);
        logger.LogInformation("Listening on Service Bus {Topic}/{Subscription}.", topic, subscription);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
        await processor.StopProcessingAsync(CancellationToken.None);
    }

    private sealed class ServiceBusReceivedMessage(ProcessMessageEventArgs args) : IReceivedMessage
    {
        public bool Settled { get; private set; }
        public string? Subject => args.Message.Subject;
        public string Body => args.Message.Body.ToString();
        public CancellationToken CancellationToken => args.CancellationToken;

        public Task CompleteAsync() => Settle(args.CompleteMessageAsync(args.Message, args.CancellationToken));

        public Task RetryAsync() =>
            Settle(args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken));

        public Task DeadLetterAsync(string reason, string? description = null) =>
            Settle(args.DeadLetterMessageAsync(args.Message, reason, description, args.CancellationToken));

        private Task Settle(Task settle)
        {
            Settled = true;
            return settle;
        }
    }
}

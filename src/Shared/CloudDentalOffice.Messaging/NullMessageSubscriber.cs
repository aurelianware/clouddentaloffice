namespace CloudDentalOffice.Messaging;

/// <summary>
/// Used when no message broker is configured. Consumers check
/// <see cref="ServiceBusOptions.IsConfigured"/> and stay idle, so this only
/// keeps them constructible.
/// </summary>
public sealed class NullMessageSubscriber : IMessageSubscriber
{
    public Task RunAsync(string topic, string subscription, Func<IReceivedMessage, Task> handler,
        CancellationToken stoppingToken) => Task.CompletedTask;
}

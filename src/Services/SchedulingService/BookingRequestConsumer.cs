using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Subscribes to the booking-requests topic and turns each BookingRequestedEvent
/// into a durable BookingRequest for explicit staff review. It never creates an
/// Appointment and requires no placeholder patient or provider identifiers.
/// </summary>
public sealed class BookingRequestConsumer(
    IServiceProvider services,
    ServiceBusOptions options,
    ILogger<BookingRequestConsumer> logger) : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "ServiceBus not configured; BookingRequestConsumer is idle. Set ServiceBus:ConnectionString to enable.");
            return;
        }

        // Guarded by the IsConfigured check above, so ConnectionString is set.
        _client = new ServiceBusClient(options.ConnectionString!);
        _processor = _client.CreateProcessor(options.BookingTopic, options.BookingSubscription, new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = 1,
            AutoCompleteMessages = false
        });
        _processor.ProcessMessageAsync += OnMessageAsync;
        _processor.ProcessErrorAsync += OnErrorAsync;

        await _processor.StartProcessingAsync(stoppingToken);
        logger.LogInformation("BookingRequestConsumer listening on {Topic}/{Subscription}.",
            options.BookingTopic, options.BookingSubscription);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task OnMessageAsync(ProcessMessageEventArgs args)
    {
        if (!string.Equals(args.Message.Subject, nameof(BookingRequestedEvent), StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(args.Message, "UnexpectedSubject",
                $"Subject '{args.Message.Subject}' is not handled.");
            return;
        }

        BookingRequestedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BookingRequestedEvent>(args.Message.Body.ToString());
        }
        catch (Exception ex)
        {
            await args.DeadLetterMessageAsync(args.Message, "DeserializationError", ex.Message);
            return;
        }

        if (evt is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "EmptyBody", "Message body did not deserialize.");
            return;
        }

        if (evt.EventId == Guid.Empty || string.IsNullOrWhiteSpace(evt.TenantId) ||
            string.IsNullOrWhiteSpace(evt.Name) || string.IsNullOrWhiteSpace(evt.Phone) || evt.PreferredStartUtc == default)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidEvent", "Required booking event fields are missing.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var created = await new BookingRequestWorkflow(db).PersistEventAsync(evt, args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);

        logger.LogInformation("{Action} BookingRequest for event {EventId} and tenant {TenantId}.",
            created ? "Created" : "Ignored duplicate", evt.EventId, evt.TenantId);
    }

    private Task OnErrorAsync(ProcessErrorEventArgs args)
    {
        logger.LogError(args.Exception, "Service Bus processing error ({Source}).", args.ErrorSource);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
        }
        if (_client is not null)
            await _client.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}

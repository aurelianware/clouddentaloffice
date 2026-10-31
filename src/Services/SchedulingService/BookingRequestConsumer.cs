using System.Text.Json;
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
    IMessageSubscriber subscriber,
    ILogger<BookingRequestConsumer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "Messaging is not configured; BookingRequestConsumer is idle. Configure the ServiceBus section to enable.");
            return;
        }
        await subscriber.RunAsync(options.BookingTopic, options.BookingSubscription, ProcessAsync, stoppingToken);
    }

    private async Task ProcessAsync(IReceivedMessage message)
    {
        if (!string.Equals(message.Subject, nameof(BookingRequestedEvent), StringComparison.Ordinal))
        {
            await message.DeadLetterAsync("UnexpectedSubject",
                $"Subject '{message.Subject}' is not handled.");
            return;
        }

        BookingRequestedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<BookingRequestedEvent>(message.Body);
        }
        catch (Exception ex)
        {
            await message.DeadLetterAsync("DeserializationError", ex.Message);
            return;
        }

        if (evt is null)
        {
            await message.DeadLetterAsync("EmptyBody", "Message body did not deserialize.");
            return;
        }

        if (evt.EventId == Guid.Empty || string.IsNullOrWhiteSpace(evt.TenantId) ||
            string.IsNullOrWhiteSpace(evt.Name) || string.IsNullOrWhiteSpace(evt.Phone) || evt.PreferredStartUtc == default)
        {
            await message.DeadLetterAsync("InvalidEvent", "Required booking event fields are missing.");
            return;
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var created = await new BookingRequestWorkflow(db).PersistEventAsync(evt, message.CancellationToken);
        await message.CompleteAsync();

        logger.LogInformation("{Action} BookingRequest for event {EventId} and tenant {TenantId}.",
            created ? "Created" : "Ignored duplicate", evt.EventId, evt.TenantId);
    }
}

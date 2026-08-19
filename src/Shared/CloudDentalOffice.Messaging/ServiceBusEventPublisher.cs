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
    private readonly ServiceBusSender _bookingSender;
    private readonly ServiceBusSender _availabilitySender;
    private readonly ServiceBusSender _zocdocWebhookSender;
    private readonly ServiceBusSender _appointmentLifecycleSender;
    private readonly ServiceBusSender _stripeWebhookSender;
    private readonly ILogger<ServiceBusEventPublisher> _logger;

    public ServiceBusEventPublisher(ServiceBusOptions options, ILogger<ServiceBusEventPublisher> logger)
    {
        // Only constructed when a connection string is configured (see
        // AddEventPublishing), so the null-forgiving operator is safe here.
        _client = new ServiceBusClient(options.ConnectionString!);
        _bookingSender = _client.CreateSender(options.BookingTopic);
        _availabilitySender = _client.CreateSender(options.SchedulingAvailabilityTopic);
        _zocdocWebhookSender = _client.CreateSender(options.ZocdocWebhookTopic);
        _appointmentLifecycleSender = _client.CreateSender(options.AppointmentLifecycleTopic);
        _stripeWebhookSender = _client.CreateSender(options.StripeWebhookTopic);
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

        var sender = @event switch
        {
            SchedulingAvailabilityChangedEvent => _availabilitySender,
            ZocdocAppointmentWebhookEvent => _zocdocWebhookSender,
            AppointmentLifecycleChangedEvent => _appointmentLifecycleSender,
            StripePaymentWebhookEvent or StripeRefundWebhookEvent => _stripeWebhookSender,
            _ => _bookingSender
        };
        await sender.SendMessageAsync(message, cancellationToken);
        _logger.LogInformation("Published {EventType} {EventId} to Service Bus.", typeName, @event.EventId);
    }

    public async ValueTask DisposeAsync()
    {
        await _bookingSender.DisposeAsync();
        await _availabilitySender.DisposeAsync();
        await _zocdocWebhookSender.DisposeAsync();
        await _appointmentLifecycleSender.DisposeAsync();
        await _stripeWebhookSender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

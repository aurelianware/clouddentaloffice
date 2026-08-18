using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentWebhookConsumer(
    IServiceProvider services, ServiceBusOptions options,
    ILogger<ZocdocAppointmentWebhookConsumer> logger) : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        _client = new ServiceBusClient(options.ConnectionString!);
        _processor = _client.CreateProcessor(options.ZocdocWebhookTopic,
            options.ZocdocWebhookSubscription, new ServiceBusProcessorOptions
            { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        _processor.ProcessMessageAsync += ProcessAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Zocdoc webhook Service Bus processing error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(ProcessMessageEventArgs args)
    {
        if (!string.Equals(args.Message.Subject, nameof(ZocdocAppointmentWebhookEvent), StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(args.Message, "UnexpectedSubject");
            return;
        }

        ZocdocAppointmentWebhookEvent? evt;
        try { evt = JsonSerializer.Deserialize<ZocdocAppointmentWebhookEvent>(args.Message.Body.ToString()); }
        catch (JsonException)
        {
            await args.DeadLetterMessageAsync(args.Message, "DeserializationError");
            return;
        }
        if (evt is null || string.IsNullOrWhiteSpace(evt.TenantId) ||
            string.IsNullOrWhiteSpace(evt.ExternalEventId) || string.IsNullOrWhiteSpace(evt.AppointmentId) ||
            evt.UpdateType is not ("created" or "updated" or "cancelled"))
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidEvent");
            return;
        }

        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IZocdocAppointmentWebhookProcessor>()
                .ProcessAsync(evt, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (SchedulingIntegrationDisabledException)
        {
            await args.DeadLetterMessageAsync(args.Message, "DisabledIntegration", cancellationToken: args.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Zocdoc appointment event processing failed for tenant {TenantId}, event {ExternalEventId}",
                evt.TenantId, evt.ExternalEventId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) { await _processor.StopProcessingAsync(cancellationToken); await _processor.DisposeAsync(); }
        if (_client is not null) await _client.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}

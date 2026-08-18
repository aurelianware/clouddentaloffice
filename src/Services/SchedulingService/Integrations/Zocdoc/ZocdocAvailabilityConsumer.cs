using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAvailabilityConsumer(
    IServiceProvider services, ServiceBusOptions options,
    ILogger<ZocdocAvailabilityConsumer> logger) : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        _client = new ServiceBusClient(options.ConnectionString!);
        _processor = _client.CreateProcessor(options.SchedulingAvailabilityTopic,
            options.SchedulingAvailabilitySubscription, new ServiceBusProcessorOptions
            { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        _processor.ProcessMessageAsync += ProcessAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError(args.Exception, "Zocdoc availability Service Bus processing error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(ProcessMessageEventArgs args)
    {
        if (!string.Equals(args.Message.Subject, nameof(SchedulingAvailabilityChangedEvent), StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(args.Message, "UnexpectedSubject");
            return;
        }
        SchedulingAvailabilityChangedEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<SchedulingAvailabilityChangedEvent>(args.Message.Body.ToString());
        }
        catch (JsonException ex)
        {
            await args.DeadLetterMessageAsync(args.Message, "DeserializationError", ex.Message);
            return;
        }
        if (evt is null || string.IsNullOrWhiteSpace(evt.TenantId) || evt.ToUtc <= evt.FromUtc)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidEvent");
            return;
        }
        await using var scope = services.CreateAsyncScope();
        var synchronizer = scope.ServiceProvider.GetRequiredService<IZocdocAvailabilitySynchronizer>();
        var result = await synchronizer.ReconcileAsync(new(evt.TenantId,
            new DateTimeOffset(evt.FromUtc), new DateTimeOffset(evt.ToUtc), evt.ProviderId), args.CancellationToken);
        if (result.Failed > 0)
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        else
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) { await _processor.StopProcessingAsync(cancellationToken); await _processor.DisposeAsync(); }
        if (_client is not null) await _client.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;

namespace SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentLifecycleConsumer(
    IServiceProvider services, ServiceBusOptions options, ILogger<ZocdocAppointmentLifecycleConsumer> logger)
    : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        _client = new(options.ConnectionString!);
        _processor = _client.CreateProcessor(options.AppointmentLifecycleTopic,
            options.AppointmentLifecycleSubscription, new() { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        _processor.ProcessMessageAsync += ProcessAsync;
        _processor.ProcessErrorAsync += args => { logger.LogError(args.Exception,
            "Zocdoc lifecycle Service Bus error ({Source})", args.ErrorSource); return Task.CompletedTask; };
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
    }
    private async Task ProcessAsync(ProcessMessageEventArgs args)
    {
        AppointmentLifecycleChangedEvent? evt;
        try { evt = JsonSerializer.Deserialize<AppointmentLifecycleChangedEvent>(args.Message.Body.ToString()); }
        catch (JsonException) { await args.DeadLetterMessageAsync(args.Message, "DeserializationError"); return; }
        if (args.Message.Subject != nameof(AppointmentLifecycleChangedEvent) || evt is null ||
            string.IsNullOrWhiteSpace(evt.TenantId) || evt.AppointmentId == Guid.Empty)
        { await args.DeadLetterMessageAsync(args.Message, "InvalidEvent"); return; }
        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IZocdocAppointmentLifecycleSynchronizer>()
                .SynchronizeAsync(evt, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (PermanentLifecycleSyncException ex)
        { await args.DeadLetterMessageAsync(args.Message, "PermanentFailure", ex.Message, args.CancellationToken); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transient Zocdoc lifecycle failure for tenant {TenantId}, appointment {AppointmentId}",
                evt.TenantId, evt.AppointmentId);
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

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;

/// <summary>
/// Subscribes to the booking-requests topic and turns each BookingRequestedEvent
/// into an unconfirmed (Requested) appointment. Provider/location/patient are
/// resolved here from PublicBooking configuration — the internet-facing
/// IntakeService never sees them. Runs only when ServiceBus is configured.
/// </summary>
public sealed class BookingRequestConsumer(
    IServiceProvider services,
    IConfiguration config,
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

        var section = config.GetSection("PublicBooking");
        var providerId = section.GetValue<Guid>("ProviderId");
        var patientId = section.GetValue<Guid>("PatientId");
        var locationId = section.GetValue<Guid>("LocationId");
        var defaultDurationMinutes = section.GetValue("DefaultDurationMinutes", 60);
        if (providerId == Guid.Empty || patientId == Guid.Empty || defaultDurationMinutes <= 0)
        {
            logger.LogError("PublicBooking is misconfigured; dead-lettering booking event {EventId}.", evt.EventId);
            await args.DeadLetterMessageAsync(args.Message, "Misconfigured",
                "ProviderId/PatientId/DefaultDurationMinutes are not set on the SchedulingService.");
            return;
        }

        var durationMinutes = evt.DurationMinutes is int d && d > 0 ? d : defaultDurationMinutes;
        var startUtc = evt.PreferredStartUtc.Kind == DateTimeKind.Utc
            ? evt.PreferredStartUtc
            : evt.PreferredStartUtc.ToUniversalTime();

        var notes = string.Join("\n", new[]
        {
            "WEB BOOKING REQUEST — confirm with patient before finalizing.",
            $"Name: {evt.Name}",
            $"Phone: {evt.Phone}",
            string.IsNullOrWhiteSpace(evt.Email) ? null : $"Email: {evt.Email}",
            string.IsNullOrWhiteSpace(evt.Reason) ? null : $"Reason: {evt.Reason}",
            string.IsNullOrWhiteSpace(evt.Message) ? null : $"Message: {evt.Message}"
        }.Where(line => line is not null));

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        db.Appointments.Add(new Appointment
        {
            Id = Guid.NewGuid(),
            PatientId = patientId,
            ProviderId = providerId,
            StartTime = startUtc,
            EndTime = startUtc.AddMinutes(durationMinutes),
            Status = AppointmentStatus.Requested,
            ProcedureCodes = null,
            Notes = notes,
            Operatory = null,
            LocationId = locationId == Guid.Empty ? null : locationId,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(args.CancellationToken);
        await args.CompleteMessageAsync(args.Message, args.CancellationToken);

        logger.LogInformation("Created Requested appointment from booking event {EventId}.", evt.EventId);
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

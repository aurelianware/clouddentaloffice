using System.Runtime.CompilerServices;
using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The broker provider switch (Service Bus or Pub/Sub) and the consumer
/// contract every subscription handler settles through.
/// </summary>
public sealed class MessagingProviderTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddDbContext<SchedulingDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Theory]
    [InlineData(typeof(BookingRequestedEvent), "booking-requests")]
    [InlineData(typeof(SchedulingAvailabilityChangedEvent), "scheduling-availability")]
    [InlineData(typeof(ZocdocAppointmentWebhookEvent), "zocdoc-webhooks")]
    [InlineData(typeof(AppointmentLifecycleChangedEvent), "appointment-lifecycle")]
    [InlineData(typeof(StripePaymentWebhookEvent), "stripe-webhooks")]
    [InlineData(typeof(StripeRefundWebhookEvent), "stripe-webhooks")]
    [InlineData(typeof(StripeDisputeWebhookEvent), "stripe-webhooks")]
    [InlineData(typeof(CoverageIntakeSubmittedEvent), "coverage-intake")]
    [InlineData(typeof(WebsiteInsuranceCheckEvent), "coverage-intake")]
    public void EachEventGoesToTheSameTopicOnEitherBroker(Type eventType, string topic)
    {
        var @event = (IntegrationEvent)RuntimeHelpers.GetUninitializedObject(eventType);
        Assert.Equal(topic, new ServiceBusOptions().TopicFor(@event));
    }

    [Fact]
    public void ServiceBusIsTheDefaultAndNeedsAConnectionString()
    {
        Assert.False(Options().IsConfigured);
        Assert.False(Options(("ServiceBus:PubSubProjectId", "cdo-prod")).IsConfigured);
        var options = Options(("ServiceBus:ConnectionString", "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s"));
        Assert.True(options.IsConfigured);
        Assert.False(options.IsPubSub);
    }

    [Fact]
    public void PubSubNeedsAProjectAndIgnoresTheServiceBusConnectionString()
    {
        Assert.False(Options(("ServiceBus:Provider", "PubSub"),
            ("ServiceBus:ConnectionString", "Endpoint=sb://example.servicebus.windows.net/")).IsConfigured);
        var options = Options(("ServiceBus:Provider", "pubsub"), ("ServiceBus:PubSubProjectId", "cdo-prod"));
        Assert.True(options.IsConfigured);
        Assert.True(options.IsPubSub);
    }

    [Fact]
    public async Task PubSubRegistersThePubSubPublisherAndSubscriber()
    {
        await using var provider = Register(("ServiceBus:Provider", "PubSub"), ("ServiceBus:PubSubProjectId", "cdo-prod"));
        Assert.IsType<PubSubEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.IsType<PubSubMessageSubscriber>(provider.GetRequiredService<IMessageSubscriber>());
    }

    [Fact]
    public async Task ServiceBusRegistersTheServiceBusPublisherAndSubscriber()
    {
        await using var provider = Register(("ServiceBus:ConnectionString",
            "Endpoint=sb://example.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=s"));
        Assert.IsType<ServiceBusEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.IsType<ServiceBusMessageSubscriber>(provider.GetRequiredService<IMessageSubscriber>());
    }

    [Fact]
    public void WithoutABrokerEventsAreDroppedAndConsumersStayIdle()
    {
        using var provider = Register();
        Assert.IsType<NullEventPublisher>(provider.GetRequiredService<IEventPublisher>());
        Assert.IsType<NullMessageSubscriber>(provider.GetRequiredService<IMessageSubscriber>());
    }

    [Fact]
    public async Task ConsumerListensOnTheBookingSubscription()
    {
        var subscriber = new FakeSubscriber();
        await RunBookingConsumer(subscriber);
        Assert.Equal(("booking-requests", "scheduling"), subscriber.ListenedOn);
    }

    [Fact]
    public async Task ValidBookingIsStoredAndCompleted()
    {
        var message = new FakeMessage(nameof(BookingRequestedEvent), JsonSerializer.Serialize(NewBooking()));
        await RunBookingConsumer(new FakeSubscriber(message));

        Assert.Equal("complete", message.Outcome);
        await using var scope = _services.CreateAsyncScope();
        Assert.Single(await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().BookingRequests.ToListAsync());
    }

    [Theory]
    [InlineData("SomethingElse", "{}", "UnexpectedSubject")]
    [InlineData(nameof(BookingRequestedEvent), "not json", "DeserializationError")]
    [InlineData(nameof(BookingRequestedEvent), "null", "EmptyBody")]
    public async Task UnusableMessagesAreDeadLetteredWithAReason(string subject, string body, string reason)
    {
        var message = new FakeMessage(subject, body);
        await RunBookingConsumer(new FakeSubscriber(message));

        Assert.Equal("dead-letter:" + reason, message.Outcome);
        await using var scope = _services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<SchedulingDbContext>().BookingRequests.ToListAsync());
    }

    [Fact]
    public async Task BookingMissingRequiredFieldsIsDeadLettered()
    {
        var message = new FakeMessage(nameof(BookingRequestedEvent),
            JsonSerializer.Serialize(NewBooking() with { Phone = "" }));
        await RunBookingConsumer(new FakeSubscriber(message));
        Assert.Equal("dead-letter:InvalidEvent", message.Outcome);
    }

    private static BookingRequestedEvent NewBooking() => new("Sam Patient", "555-0100", null,
        new DateTime(2030, 1, 1, 17, 0, 0, DateTimeKind.Utc), 60, "Cleaning", null, PatientRelationship.New, "tenant-a");

    private async Task RunBookingConsumer(FakeSubscriber subscriber)
    {
        var options = new ServiceBusOptions { Provider = ServiceBusOptions.PubSubProvider, PubSubProjectId = "test" };
        var consumer = new BookingRequestConsumer(_services, options, subscriber, NullLogger<BookingRequestConsumer>.Instance);
        await consumer.StartAsync(CancellationToken.None);
        await consumer.ExecuteTask!;
    }

    private static ServiceBusOptions Options(params (string Key, string Value)[] settings)
    {
        var options = new ServiceBusOptions();
        Configuration(settings).GetSection(ServiceBusOptions.SectionName).Bind(options);
        return options;
    }

    private static ServiceProvider Register(params (string Key, string Value)[] settings) =>
        new ServiceCollection().AddLogging().AddEventPublishing(Configuration(settings)).BuildServiceProvider();

    private static IConfiguration Configuration((string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    /// <summary>Hands its messages to the handler once, then returns as if stopped.</summary>
    private sealed class FakeSubscriber(params FakeMessage[] messages) : IMessageSubscriber
    {
        public (string Topic, string Subscription)? ListenedOn { get; private set; }

        public async Task RunAsync(string topic, string subscription, Func<IReceivedMessage, Task> handler,
            CancellationToken stoppingToken)
        {
            ListenedOn = (topic, subscription);
            foreach (var message in messages)
                await handler(message);
        }
    }

    private sealed class FakeMessage(string? subject, string body) : IReceivedMessage
    {
        public string? Outcome { get; private set; }
        public string? Subject => subject;
        public string Body => body;
        public CancellationToken CancellationToken => CancellationToken.None;

        public Task CompleteAsync() => Settle("complete");
        public Task RetryAsync() => Settle("retry");
        public Task DeadLetterAsync(string reason, string? description = null) => Settle("dead-letter:" + reason);

        private Task Settle(string outcome)
        {
            Assert.Null(Outcome);
            Outcome = outcome;
            return Task.CompletedTask;
        }
    }
}

using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

public sealed class IntegrationInboxTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    public IntegrationInboxTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Persist_is_durable_and_duplicate_is_idempotent()
    {
        await using var db = CreateDb();
        var inbox = new IntegrationInbox(db, _clock);
        var first = await inbox.PersistAsync("tenant-a", "Zocdoc", "event-1",
            nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "event-1"));
        var replay = await inbox.PersistAsync("tenant-a", "Zocdoc", "event-1",
            nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "event-1"));

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(1, await db.IntegrationInboxMessages.CountAsync());
        var payload = await db.IntegrationInboxMessages.Select(x => x.Payload).SingleAsync();
        Assert.DoesNotContain("patient", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stripe_event_uses_same_durable_duplicate_and_dispatch_path()
    {
        await using var db = CreateDb();
        var inbox = new IntegrationInbox(db, _clock);
        var stripe = new StripePaymentWebhookEvent("tenant-a", "evt_stripe", "checkout.session.completed",
            "acct_practice", "cs_test", "pi_test", "pay_opaque", 5000, "USD", "paid", false);

        var first = await inbox.PersistAsync("tenant-a", "Stripe", "evt_stripe",
            nameof(StripePaymentWebhookEvent), stripe);
        var replay = await inbox.PersistAsync("tenant-a", "Stripe", "evt_stripe",
            nameof(StripePaymentWebhookEvent), stripe);
        var publisher = new RecordingPublisher();

        Assert.True(first.Created);
        Assert.False(replay.Created);
        Assert.Equal(1, await Dispatcher(db, publisher,
            new ServiceBusOptions { ConnectionString = "configured" }).DispatchBatchAsync());
        Assert.IsType<StripePaymentWebhookEvent>(Assert.Single(publisher.Events));
    }

    [Fact]
    public async Task Stripe_refund_event_uses_the_durable_dispatch_path()
    {
        await using var db = CreateDb();
        var inbox = new IntegrationInbox(db, _clock);
        var stripe = new StripeRefundWebhookEvent("tenant-a", "evt_refund", "refund.updated",
            "acct_practice", "re_test", "pi_test", "refund_opaque", 2500, "USD", "succeeded", false);
        await inbox.PersistAsync("tenant-a", "Stripe", "evt_refund", nameof(StripeRefundWebhookEvent), stripe);
        var publisher = new RecordingPublisher();
        Assert.Equal(1, await Dispatcher(db, publisher,
            new ServiceBusOptions { ConnectionString = "configured" }).DispatchBatchAsync());
        Assert.IsType<StripeRefundWebhookEvent>(Assert.Single(publisher.Events));
    }

    [Fact]
    public async Task Database_constraint_prevents_duplicate_logical_event()
    {
        await using var db = CreateDb();
        db.AddRange(Row("same"), Row("same"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Broker_outage_keeps_acknowledged_message_then_later_delivers_it()
    {
        await using var db = CreateDb();
        var inbox = new IntegrationInbox(db, _clock);
        await inbox.PersistAsync("tenant-a", "Zocdoc", "event-outage",
            nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "event-outage"));
        var broker = new ServiceBusOptions();
        var publisher = new RecordingPublisher();
        var dispatcher = Dispatcher(db, publisher, broker);

        Assert.Equal(0, await dispatcher.DispatchBatchAsync());
        Assert.Equal(IntegrationInboxStatus.Received,
            await db.IntegrationInboxMessages.Select(x => x.Status).SingleAsync());

        broker.ConnectionString = "Endpoint=sb://restored/;SharedAccessKeyName=test;SharedAccessKey=test";
        _clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        Assert.Single(publisher.Events);
        Assert.Equal(IntegrationInboxStatus.Published,
            await db.IntegrationInboxMessages.Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Transient_failure_retries_and_poison_message_can_be_requeued_tenant_safely()
    {
        await using var db = CreateDb();
        var inbox = new IntegrationInbox(db, _clock);
        var persisted = await inbox.PersistAsync("tenant-a", "Zocdoc", "poison",
            nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "poison"));
        var publisher = new RecordingPublisher { Failure = new HttpRequestException("patient name must not leak") };
        var dispatcher = Dispatcher(db, publisher, new ServiceBusOptions { ConnectionString = "configured" }, maxAttempts: 2);

        await dispatcher.DispatchBatchAsync();
        _clock.Advance(TimeSpan.FromSeconds(6));
        await dispatcher.DispatchBatchAsync();
        var row = await db.IntegrationInboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(IntegrationInboxStatus.Failed, row.Status);
        Assert.Equal(nameof(HttpRequestException), row.LastError);
        Assert.DoesNotContain("patient name", row.LastError);
        Assert.False(await inbox.RequeueAsync("tenant-b", persisted.Id));
        Assert.True(await inbox.RequeueAsync("tenant-a", persisted.Id));
    }

    [Fact]
    public async Task Restart_dispatches_pending_record_and_status_is_tenant_scoped()
    {
        await using (var first = CreateDb())
            await new IntegrationInbox(first, _clock).PersistAsync("tenant-a", "Zocdoc", "restart",
                nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "restart"));
        await using var restarted = CreateDb();
        var publisher = new RecordingPublisher();
        Assert.Equal(1, await Dispatcher(restarted, publisher,
            new ServiceBusOptions { ConnectionString = "configured" }).DispatchBatchAsync());
        var statusA = await new IntegrationInbox(restarted, _clock).GetStatusAsync("tenant-a");
        var statusB = await new IntegrationInbox(restarted, _clock).GetStatusAsync("tenant-b");
        Assert.Equal(1, statusA.Published);
        Assert.Equal(0, statusB.Published);
    }

    [Fact]
    public async Task Concurrent_dispatchers_claim_a_message_only_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdo-inbox-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<IntakeDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var setup = new IntakeDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                await new IntegrationInbox(setup, _clock).PersistAsync("tenant-a", "Zocdoc", "concurrent",
                    nameof(ZocdocAppointmentWebhookEvent), Event("tenant-a", "concurrent"));
            }
            await using var firstDb = new IntakeDbContext(options);
            await using var secondDb = new IntakeDbContext(options);
            var publisher = new RecordingPublisher();
            var broker = new ServiceBusOptions { ConnectionString = "configured" };
            await Task.WhenAll(Dispatcher(firstDb, publisher, broker).DispatchBatchAsync(),
                Dispatcher(secondDb, publisher, broker).DispatchBatchAsync());
            Assert.Single(publisher.Events);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Expired_lease_on_final_attempt_is_recovered_instead_of_stuck()
    {
        await using var db = CreateDb();
        var row = Row("expired-final-attempt");
        row.Status = IntegrationInboxStatus.Publishing;
        row.AttemptCount = 2;
        row.LockId = Guid.NewGuid();
        row.LockedUntil = _clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
        row.Payload = System.Text.Json.JsonSerializer.Serialize(Event("tenant-a", row.ExternalEventId));
        db.Add(row);
        await db.SaveChangesAsync();
        var publisher = new RecordingPublisher();

        Assert.Equal(1, await Dispatcher(db, publisher,
            new ServiceBusOptions { ConnectionString = "configured" }, maxAttempts: 2).DispatchBatchAsync());
        Assert.Single(publisher.Events);
        Assert.Equal(IntegrationInboxStatus.Published,
            await db.IntegrationInboxMessages.AsNoTracking().Select(x => x.Status).SingleAsync());
    }

    private IntakeDbContext CreateDb() => new(new DbContextOptionsBuilder<IntakeDbContext>()
        .UseSqlite(_connection).Options);

    private IntegrationInboxDispatcher Dispatcher(IntakeDbContext db, IEventPublisher publisher,
        ServiceBusOptions broker, int maxAttempts = 8) => new(db, publisher, broker,
        Options.Create(new IntegrationInboxOptions { MaxAttempts = maxAttempts }), _clock,
        new ZocdocWebhookMetrics(), NullLogger<IntegrationInboxDispatcher>.Instance);

    private IntegrationInboxMessage Row(string eventId) => new()
    {
        TenantId = "tenant-a", Channel = "Zocdoc", ExternalEventId = eventId,
        EventType = nameof(ZocdocAppointmentWebhookEvent), ReceivedAt = _clock.GetUtcNow().UtcDateTime,
        Payload = "{}"
    };

    private static ZocdocAppointmentWebhookEvent Event(string tenant, string id) =>
        new(tenant, id, "appointment-1", "created");

    public void Dispose() => _connection.Dispose();

    private sealed class RecordingPublisher : IEventPublisher
    {
        public ConcurrentBag<IntegrationEvent> Events { get; } = [];
        public Exception? Failure { get; set; }
        public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Events.Add(@event);
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}

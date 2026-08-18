using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Scheduling;
using CloudDentalOffice.Messaging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentLifecycleTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private readonly FakePublisher _publisher = new();
    private readonly FakeApi _api = new();
    private readonly Guid _appointmentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.SchedulingIntegrationConfigurations.Add(new()
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true,
            Environment = "Sandbox", TimeZoneId = "America/Phoenix"
        });
        _db.Appointments.Add(new()
        {
            Id = _appointmentId, TenantId = "practice-a", PatientId = 42, ProviderId = 12,
            LocationId = Guid.NewGuid(), AppointmentTypeId = "exam", Status = AppointmentStatus.Confirmed,
            StartTime = DateTime.UtcNow.AddHours(-12), EndTime = DateTime.UtcNow.AddHours(-11.5)
        });
        _db.ExternalAppointmentReferences.Add(Reference());
        await _db.SaveChangesAsync();
    }
    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task LocalCancellationIsQueuedThenSentAndMarkedSynced()
    {
        await Local().ApplyLocalAsync("practice-a", _appointmentId,
            new(AppointmentStatus.Cancelled));
        var evt = Assert.IsType<AppointmentLifecycleChangedEvent>(Assert.Single(_publisher.Events));
        Assert.Equal("CloudDentalOffice", evt.Source);
        Assert.Equal(ExternalAppointmentSyncStatus.Pending, (await ReferenceAsync()).SyncStatus);

        await Sync().SynchronizeAsync(evt);

        Assert.Equal("cancel", Assert.Single(_api.Operations).Operation);
        Assert.Equal(ExternalAppointmentSyncStatus.Synced, (await ReferenceAsync()).SyncStatus);

        await Sync().SynchronizeAsync(evt);
        Assert.Single(_api.Operations);
    }

    [Fact]
    public async Task InvalidFutureNoShowIsRejectedBeforePublishing()
    {
        var appointment = await _db.Appointments.SingleAsync();
        appointment.StartTime = DateTime.UtcNow.AddHours(1);
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => Local().ApplyLocalAsync("practice-a", _appointmentId,
            new(AppointmentStatus.NoShow)));
        Assert.Empty(_publisher.Events);
    }

    [Theory]
    [InlineData(AppointmentStatus.CheckedIn, "arrived")]
    [InlineData(AppointmentStatus.NoShow, "no_show")]
    public async Task ArrivedAndNoShowUseDocumentedStatusValues(AppointmentStatus status, string operation)
    {
        await Local().ApplyLocalAsync("practice-a", _appointmentId, new(status));
        await Sync().SynchronizeAsync((AppointmentLifecycleChangedEvent)Assert.Single(_publisher.Events));
        Assert.Equal(operation, Assert.Single(_api.Operations).Operation);
    }

    [Fact]
    public async Task RescheduleUsesPracticeLocalOffset()
    {
        await Local().ApplyLocalAsync("practice-a", _appointmentId,
            new(AppointmentStatus.Rescheduled, Utc(2026, 9, 9, 16), Utc(2026, 9, 9, 16, 30)));
        await Sync().SynchronizeAsync((AppointmentLifecycleChangedEvent)Assert.Single(_publisher.Events));
        var operation = Assert.Single(_api.Operations);
        Assert.Equal("reschedule", operation.Operation);
        Assert.Equal(TimeSpan.FromHours(-7), operation.Start!.Value.Offset);
    }

    [Fact]
    public async Task ExternalSourceDoesNotLoopBackToZocdoc()
    {
        await Sync().SynchronizeAsync(new("practice-a", _appointmentId, "cancel", "Zocdoc"));
        Assert.Empty(_api.Operations);
    }

    [Fact]
    public async Task ApiOutagePersistsFailureAndRemainsRetryable()
    {
        await Local().ApplyLocalAsync("practice-a", _appointmentId, new(AppointmentStatus.Cancelled));
        _api.Failure = new(ZocdocFailureKind.TemporaryRemoteFailure, "outage");
        await Assert.ThrowsAsync<ZocdocIntegrationException>(() =>
            Sync().SynchronizeAsync((AppointmentLifecycleChangedEvent)Assert.Single(_publisher.Events)));
        Assert.Equal(ExternalAppointmentSyncStatus.Failed, (await ReferenceAsync()).SyncStatus);
        Assert.Equal("TemporaryRemoteFailure", (await ReferenceAsync()).LastSyncError);
    }

    [Fact]
    public async Task MissingMappingAndTenantIsolationArePermanentFailures()
    {
        var reference = await ReferenceAsync();
        reference.ExternalProviderId = null;
        reference.SyncStatus = ExternalAppointmentSyncStatus.Pending;
        reference.PendingOperation = "cancel";
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<PermanentLifecycleSyncException>(() => Sync().SynchronizeAsync(
            new("practice-a", _appointmentId, "cancel", "CloudDentalOffice")));
        Assert.Equal(ExternalAppointmentSyncStatus.Conflict, reference.SyncStatus);
        await Assert.ThrowsAsync<PermanentLifecycleSyncException>(() => Sync().SynchronizeAsync(
            new("practice-b", _appointmentId, "cancel", "CloudDentalOffice")));
    }

    [Fact]
    public async Task SupersededEventIsPersistedAsConflict()
    {
        var reference = await ReferenceAsync();
        reference.SyncStatus = ExternalAppointmentSyncStatus.Pending;
        reference.PendingOperation = "arrived";
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<PermanentLifecycleSyncException>(() => Sync().SynchronizeAsync(
            new("practice-a", _appointmentId, "cancel", "CloudDentalOffice")));
        Assert.Equal(ExternalAppointmentSyncStatus.Conflict, reference.SyncStatus);
        Assert.Empty(_api.Operations);
    }

    private AppointmentLifecycleService Local() => new(_db, _publisher,
        NullLogger<AppointmentLifecycleService>.Instance);
    private ZocdocAppointmentLifecycleSynchronizer Sync() => new(_db,
        new SchedulingIntegrationConfigurationStore(_db), _api);
    private Task<ExternalAppointmentReference> ReferenceAsync() =>
        _db.ExternalAppointmentReferences.SingleAsync(x => x.TenantId == "practice-a");
    private ExternalAppointmentReference Reference() => new()
    {
        TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, AppointmentId = _appointmentId,
        ExternalAppointmentId = "za-1", ExternalProviderId = "zp", ExternalLocationId = "zl",
        ExternalVisitReasonId = "zv", SyncStatus = ExternalAppointmentSyncStatus.Synced
    };
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private sealed class FakePublisher : IEventPublisher
    {
        public List<IntegrationEvent> Events { get; } = [];
        public Task PublishAsync(IntegrationEvent @event, CancellationToken cancellationToken = default)
        { Events.Add(@event); return Task.CompletedTask; }
    }
    private sealed class FakeApi : IZocdocApiClient
    {
        public List<(string Operation, DateTimeOffset? Start)> Operations { get; } = [];
        public ZocdocIntegrationException? Failure { get; set; }
        private Task Record(string operation, DateTimeOffset? start = null)
        { if (Failure is not null) throw Failure; Operations.Add((operation, start)); return Task.CompletedTask; }
        public Task CancelAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default) => Record("cancel");
        public Task RescheduleAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, DateTimeOffset startTime, CancellationToken cancellationToken = default) => Record("reschedule", startTime);
        public Task UpdateAppointmentStatusAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, string status, CancellationToken cancellationToken = default) => Record(status);
        public Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration, string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration, string appointmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ConfirmAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration, string appointmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

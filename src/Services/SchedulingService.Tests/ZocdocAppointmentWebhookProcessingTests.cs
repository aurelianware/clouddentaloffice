using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Contracts.Patients;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAppointmentWebhookProcessingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private readonly Guid _location = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private readonly FakeApi _api = new();
    private readonly FakePatients _patients = new();
    private readonly FakeAvailability _availability = new();

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.SchedulingIntegrationConfigurations.Add(new()
            { TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true, Environment = "Sandbox" });
        _db.ExternalSchedulingResourceMappings.AddRange(
            Map(SchedulingResourceType.Provider, "12", "zp"),
            Map(SchedulingResourceType.Location, _location.ToString(), "zl"),
            Map(SchedulingResourceType.VisitReason, "exam", "zv"));
        _db.SchedulingAppointmentTypes.Add(new()
        {
            TenantId = "practice-a", AppointmentTypeId = "exam", DisplayName = "Exam",
            DurationMinutes = 30, NewPatientAllowed = true, ExistingPatientAllowed = true, IsActive = true
        });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task CreatesMappedConfirmedAppointmentAndDeduplicatesRetry()
    {
        var evt = Event("event-1");
        await Service().ProcessAsync(evt);
        await Service().ProcessAsync(evt);

        var appointment = Assert.Single(await _db.Appointments.ToListAsync());
        Assert.Equal(12, appointment.ProviderId);
        Assert.Equal(_location, appointment.LocationId);
        Assert.Equal("exam", appointment.AppointmentTypeId);
        Assert.Equal(42, appointment.PatientId);
        Assert.Single(await _db.ExternalAppointmentReferences.ToListAsync());
        Assert.Equal(1, _patients.Calls);
        Assert.Equal(1, _api.ConfirmCalls);
        Assert.Equal(SchedulingIntegrationEventStatus.Completed,
            (await _db.SchedulingIntegrationEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task CollisionFailsWithoutPatientOrAppointmentCreation()
    {
        _availability.Offered = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ProcessAsync(Event("event-2")));
        Assert.Empty(await _db.Appointments.ToListAsync());
        Assert.Equal(0, _patients.Calls);
        Assert.Equal(SchedulingIntegrationEventStatus.Failed,
            (await _db.SchedulingIntegrationEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task MissingMappingFailsBeforePatientResolution()
    {
        (await _db.ExternalSchedulingResourceMappings.SingleAsync(x =>
            x.ResourceType == SchedulingResourceType.Provider)).IsActive = false;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ProcessAsync(Event("event-3")));
        Assert.Equal(0, _patients.Calls);
    }

    [Fact]
    public async Task DisabledTenantIsRejectedBeforeDeduplication()
    {
        (await _db.SchedulingIntegrationConfigurations.SingleAsync()).Enabled = false;
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<SchedulingIntegrationDisabledException>(() =>
            Service().ProcessAsync(Event("event-4")));
        Assert.Empty(await _db.SchedulingIntegrationEvents.ToListAsync());
    }

    [Fact]
    public async Task StructuredLogsDoNotContainPatientDemographics()
    {
        var logger = new RecordingLogger<ZocdocAppointmentWebhookProcessor>();
        await Service(logger).ProcessAsync(Event("event-5"));
        var output = string.Join('\n', logger.Messages);
        Assert.DoesNotContain("Private", output);
        Assert.DoesNotContain("private@example.test", output);
        Assert.DoesNotContain("1990", output);
    }

    [Fact]
    public async Task CancellationDoesNotDependOnFetchingRemoteAppointment()
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), TenantId = "practice-a", PatientId = 42, ProviderId = 12,
            LocationId = _location, AppointmentTypeId = "exam", StartTime = DateTime.UtcNow.AddDays(1),
            EndTime = DateTime.UtcNow.AddDays(1).AddMinutes(30), Status = AppointmentStatus.Scheduled
        };
        _db.Appointments.Add(appointment);
        _db.ExternalAppointmentReferences.Add(new()
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc,
            AppointmentId = appointment.Id, ExternalAppointmentId = "za-1"
        });
        await _db.SaveChangesAsync();

        await Service().ProcessAsync(new("practice-a", "cancel-event", "za-1", "cancelled"));

        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(0, _api.GetCalls);
    }

    [Fact]
    public async Task IncomingRescheduleUpdatesExistingAppointmentWithoutOutgoingLoop()
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), TenantId = "practice-a", PatientId = 42, ProviderId = 12,
            LocationId = _location, AppointmentTypeId = "exam", StartTime = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Utc),
            EndTime = new DateTime(2026, 9, 7, 9, 30, 0, DateTimeKind.Utc), Status = AppointmentStatus.Confirmed
        };
        _db.Appointments.Add(appointment);
        _db.ExternalAppointmentReferences.Add(new()
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, AppointmentId = appointment.Id,
            ExternalAppointmentId = "za-1", ExternalProviderId = "zp", ExternalLocationId = "zl",
            ExternalVisitReasonId = "zv"
        });
        _api.StartTime = new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        _api.Status = "rescheduled";
        await _db.SaveChangesAsync();

        await Service().ProcessAsync(new("practice-a", "reschedule-event", "za-1", "updated"));

        Assert.Equal(_api.StartTime.UtcDateTime, appointment.StartTime);
        Assert.Equal(AppointmentStatus.Rescheduled, appointment.Status);
        Assert.Equal(0, _api.ConfirmCalls);
    }

    [Fact]
    public async Task UnchangedRemoteStatusDoesNotConflictWithPendingLocalOperation()
    {
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(), TenantId = "practice-a", PatientId = 42, ProviderId = 12,
            LocationId = _location, AppointmentTypeId = "exam", StartTime = _api.StartTime.UtcDateTime,
            EndTime = _api.StartTime.AddMinutes(30).UtcDateTime, Status = AppointmentStatus.Cancelled
        };
        var reference = new ExternalAppointmentReference
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, AppointmentId = appointment.Id,
            ExternalAppointmentId = "za-1", ExternalProviderId = "zp", ExternalLocationId = "zl",
            ExternalVisitReasonId = "zv", SyncStatus = ExternalAppointmentSyncStatus.Pending,
            PendingOperation = "cancel"
        };
        _db.Appointments.Add(appointment);
        _db.ExternalAppointmentReferences.Add(reference);
        _api.Status = "confirmed";
        await _db.SaveChangesAsync();

        await Service().ProcessAsync(new("practice-a", "unchanged-event", "za-1", "updated")
            { ExternalUpdatedAt = DateTime.UtcNow });

        Assert.Equal(ExternalAppointmentSyncStatus.Pending, reference.SyncStatus);
        Assert.Equal("cancel", reference.PendingOperation);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.NotNull(reference.LastExternalUpdatedAt);
    }

    private ZocdocAppointmentWebhookProcessor Service(
        ILogger<ZocdocAppointmentWebhookProcessor>? logger = null) => new(_db,
        new SchedulingIntegrationConfigurationStore(_db), new SchedulingIntegrationIdempotencyStore(_db),
        _api, _patients, _availability, logger ?? NullLogger<ZocdocAppointmentWebhookProcessor>.Instance);
    private static ZocdocAppointmentWebhookEvent Event(string id) =>
        new("practice-a", id, "za-1", "created");
    private static ExternalSchedulingResourceMapping Map(SchedulingResourceType type, string internalId, string externalId) =>
        new() { TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, ResourceType = type,
            InternalId = internalId, ExternalId = externalId, IsActive = true };

    private sealed class FakeApi : IZocdocApiClient
    {
        public int GetCalls { get; private set; }
        public int ConfirmCalls { get; private set; }
        public DateTimeOffset StartTime { get; set; } = new(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
        public string Status { get; set; } = "pending_booking";
        public Task<ZocdocAppointmentDto> GetAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(new ZocdocAppointmentDto
            { AppointmentId = appointmentId, Status = Status, StartTime = StartTime,
                ProviderLocationId = "zp|zl", VisitReasonId = "zv", PatientType = "new",
                Patient = new() { FirstName = "Private", LastName = "Patient", DateOfBirth = new(1990, 1, 1), EmailAddress = "private@example.test" } });
        }
        public Task ConfirmAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default) { ConfirmCalls++; return Task.CompletedTask; }
        public Task CancelAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RescheduleAppointmentAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, DateTimeOffset startTime, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAppointmentStatusAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string appointmentId, string status, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(string tenantId, SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration, string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePatients : IExternalPatientResolver
    {
        public int Calls { get; private set; }
        public Task<MatchOrCreateExternalPatientResult> ResolveAsync(string tenantId, ZocdocPatientDto patient,
            CancellationToken cancellationToken) { Calls++; return Task.FromResult(new MatchOrCreateExternalPatientResult(42, true)); }
    }

    private sealed class FakeAvailability : ISchedulingAvailabilityService
    {
        public bool Offered { get; set; } = true;
        public Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(SchedulingAvailabilityQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SchedulingAvailabilitySlot>>(
            Offered ? [new() { TenantId = query.TenantId, ProviderId = query.ProviderId!.Value,
                LocationId = query.LocationId!.Value, AppointmentTypeId = query.AppointmentTypeId!,
                StartUtc = query.FromUtc, EndUtc = query.ToUtc, PatientRelationship = query.PatientRelationship }] : []);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocAvailabilitySynchronizationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private FakeAvailability _availability = null!;
    private FakeZocdocApi _api = null!;
    private readonly Guid _location = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        SeedTenant("practice-a", 12);
        await _db.SaveChangesAsync();
        _availability = new FakeAvailability(_db, _location);
        _api = new FakeZocdocApi();
    }

    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task InitialPublicationUsesReplacementAndDuplicateIsSkipped()
    {
        var first = await Service().ReconcileAsync(Request());
        var duplicate = await Service().ReconcileAsync(Request());

        Assert.Equal(1, first.Succeeded);
        Assert.Equal(1, duplicate.Unchanged);
        var call = Assert.Single(_api.Calls);
        Assert.Equal("z-provider-12", call.ProviderId);
        Assert.Equal(new DateOnly(2026, 1, 5), call.Date);
        Assert.NotEmpty(call.Timeslots);
        Assert.All(call.Timeslots, x => Assert.Equal("z-location", x.LocationId));
    }

    [Fact]
    public async Task AppointmentCancellationAndRescheduleReconcileAffectedDates()
    {
        await Service().ReconcileAsync(Request(to: Offset(2026, 1, 7)));
        _api.Calls.Clear();
        var appointment = Appointment(Offset(2026, 1, 5, 9));
        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync();

        Assert.Empty(await _availability.GetAvailabilityAsync(new SchedulingAvailabilityQuery
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, ProviderId = 12,
            FromUtc = Offset(2026, 1, 5), ToUtc = Offset(2026, 1, 6),
            PatientRelationship = PatientRelationship.New
        }));

        await Service().ReconcileAsync(Request());
        Assert.Empty(Assert.Single(_api.Calls).Timeslots);

        appointment.Status = AppointmentStatus.Cancelled;
        await _db.SaveChangesAsync();
        _api.Calls.Clear();
        await Service().ReconcileAsync(Request());
        Assert.NotEmpty(Assert.Single(_api.Calls).Timeslots);

        appointment.Status = AppointmentStatus.Scheduled;
        await _db.SaveChangesAsync();
        await Service().ReconcileAsync(Request());
        appointment.StartTime = Utc(2026, 1, 6, 9);
        appointment.EndTime = Utc(2026, 1, 6, 9, 30);
        await _db.SaveChangesAsync();
        _api.Calls.Clear();
        await Service().ReconcileAsync(Request(to: Offset(2026, 1, 7)));
        Assert.Equal(new[] { new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 6) },
            _api.Calls.Select(x => x.Date).Order().ToArray());
    }

    [Fact]
    public async Task MissingMappingIsDiagnosedWithoutPublishingMalformedSlot()
    {
        (await _db.ExternalSchedulingResourceMappings.SingleAsync(x =>
            x.ResourceType == SchedulingResourceType.VisitReason)).IsActive = false;
        await _db.SaveChangesAsync();

        var result = await Service().ReconcileAsync(Request());

        Assert.True(result.SkippedMapping > 0);
        Assert.Empty(Assert.Single(_api.Calls).Timeslots);
        var state = await _db.SchedulingAvailabilitySyncStates.SingleAsync();
        Assert.Equal(AvailabilitySyncStatus.SkippedMapping, state.Status);
        Assert.Contains("VisitReason", state.Diagnostic);
    }

    [Fact]
    public async Task TemporaryFailureIsIsolatedAndRetriedLater()
    {
        _api.FailDates.Add(new DateOnly(2026, 1, 5));
        var partial = await Service().ReconcileAsync(Request(to: Offset(2026, 1, 7)));

        Assert.Equal(1, partial.Failed);
        Assert.Equal(1, partial.Succeeded);
        _api.FailDates.Clear();
        _api.Calls.Clear();
        var retry = await Service().ReconcileAsync(Request(to: Offset(2026, 1, 7)));

        Assert.Equal(1, retry.Succeeded);
        Assert.Equal(1, retry.Unchanged);
        Assert.Equal(new DateOnly(2026, 1, 5), Assert.Single(_api.Calls).Date);
    }

    [Fact]
    public async Task DisabledIntegrationCannotPublish()
    {
        (await _db.SchedulingIntegrationConfigurations.SingleAsync()).Enabled = false;
        await _db.SaveChangesAsync();
        Assert.Equal(0, (await Service().ReconcileAsync(Request())).Attempted);

        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task TenantReconciliationUsesOnlyThatTenantsMappingsAndState()
    {
        SeedTenant("practice-b", 12);
        await _db.SaveChangesAsync();

        var result = await Service().ReconcileAsync(Request() with { TenantId = "practice-b" });

        Assert.Equal(1, result.Succeeded);
        Assert.All(_api.Calls, x => Assert.Equal("practice-b", x.TenantId));
        Assert.DoesNotContain(await _db.SchedulingAvailabilitySyncStates.ToListAsync(),
            x => x.TenantId == "practice-a");
    }

    private ZocdocAvailabilitySynchronizer Service() => new(_db, _availability,
        new SchedulingIntegrationConfigurationStore(_db), _api, new ZocdocAvailabilityMetrics(),
        NullLogger<ZocdocAvailabilitySynchronizer>.Instance);

    private ZocdocAvailabilityReconciliationRequest Request(DateTimeOffset? to = null) =>
        new("practice-a", Offset(2026, 1, 5), to ?? Offset(2026, 1, 6), 12);

    private void SeedTenant(string tenant, int provider)
    {
        _db.SchedulingIntegrationConfigurations.Add(new()
        {
            TenantId = tenant, Channel = SchedulingChannel.Zocdoc, Enabled = true,
            Environment = "Sandbox", CredentialReference = "test", TimeZoneId = "UTC",
            MaximumBookingHorizonDays = 365
        });
        _db.ExternalSchedulingResourceMappings.AddRange(
            Mapping(tenant, SchedulingResourceType.Provider, provider.ToString(), $"z-provider-{provider}"),
            Mapping(tenant, SchedulingResourceType.Location, _location.ToString(), "z-location"),
            Mapping(tenant, SchedulingResourceType.VisitReason, "exam", "z-exam"));
        _db.SchedulingProviderWorkingHours.AddRange(
            WorkingHours(tenant, provider, DayOfWeek.Monday),
            WorkingHours(tenant, provider, DayOfWeek.Tuesday));
        _db.SchedulingAppointmentTypes.Add(new()
        {
            TenantId = tenant, AppointmentTypeId = "exam", DisplayName = "Exam", DurationMinutes = 30,
            NewPatientAllowed = true, ExistingPatientAllowed = true, IsActive = true
        });
    }

    private static ExternalSchedulingResourceMapping Mapping(string tenant, SchedulingResourceType type,
        string internalId, string externalId) => new()
        { TenantId = tenant, Channel = SchedulingChannel.Zocdoc, ResourceType = type,
            InternalId = internalId, ExternalId = externalId, IsActive = true };

    private SchedulingProviderWorkingHours WorkingHours(string tenant, int provider, DayOfWeek day) => new()
    {
        TenantId = tenant, ProviderId = provider, LocationId = _location, DayOfWeek = day,
        StartLocal = new TimeOnly(8, 0), EndLocal = new TimeOnly(17, 0), IsActive = true
    };

    private Appointment Appointment(DateTimeOffset start) => new()
    {
        Id = Guid.NewGuid(), TenantId = "practice-a", PatientId = 1, ProviderId = 12,
        LocationId = _location, AppointmentTypeId = "exam", StartTime = start.UtcDateTime,
        EndTime = start.AddMinutes(30).UtcDateTime, Status = AppointmentStatus.Scheduled
    };

    private static DateTimeOffset Offset(int y, int m, int d, int h = 0, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.Zero);
    private static DateTime Utc(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, DateTimeKind.Utc);

    private sealed class FakeAvailability(SchedulingDbContext db, Guid location) : ISchedulingAvailabilityService
    {
        public async Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(
            SchedulingAvailabilityQuery query, CancellationToken cancellationToken = default)
        {
            var date = DateOnly.FromDateTime(query.FromUtc.UtcDateTime);
            var start = new DateTimeOffset(date.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
            var appointments = await db.Appointments.AsNoTracking()
                .Where(x => x.TenantId == query.TenantId).ToListAsync(cancellationToken);
            var blocked = appointments.Any(x => x.ProviderId == query.ProviderId &&
                x.Status != AppointmentStatus.Cancelled && x.StartTime < start.AddMinutes(30).UtcDateTime &&
                x.EndTime > start.UtcDateTime);
            return blocked ? [] : [new SchedulingAvailabilitySlot
            {
                TenantId = query.TenantId, ProviderId = query.ProviderId!.Value, LocationId = location,
                AppointmentTypeId = "exam", StartUtc = start, EndUtc = start.AddMinutes(30),
                PatientRelationship = query.PatientRelationship
            }];
        }
    }

    private sealed class FakeZocdocApi : IZocdocApiClient
    {
        public List<Call> Calls { get; } = [];
        public HashSet<DateOnly> FailDates { get; } = [];
        public Task ReplaceTimeslotsAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            string externalProviderId, DateOnly localDate, IReadOnlyList<ZocdocTimeslotRequest> timeslots,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new(tenantId, externalProviderId, localDate, timeslots));
            if (FailDates.Contains(localDate)) throw new ZocdocIntegrationException(
                ZocdocFailureKind.TemporaryRemoteFailure, "outage");
            return Task.CompletedTask;
        }
        public Task ValidateConnectionAsync(string tenantId, SchedulingIntegrationConfiguration configuration,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ZocdocSchedulableEntityDto>> GetSchedulableEntitiesAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ZocdocSchedulableEntityDto>>([]);
        public Task<IReadOnlyList<ZocdocVisitReasonDto>> GetVisitReasonsAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ZocdocVisitReasonDto>>([]);
    }
    private sealed record Call(string TenantId, string ProviderId, DateOnly Date,
        IReadOnlyList<ZocdocTimeslotRequest> Timeslots);
}

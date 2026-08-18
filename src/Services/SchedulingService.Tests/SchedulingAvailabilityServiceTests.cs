using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

public sealed class SchedulingAvailabilityServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _location = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        await SeedOfferableSchedule();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task GeneratesOpenSlotsUsingAppointmentTypeDuration()
    {
        var slots = await Service().GetAvailabilityAsync(Query());

        Assert.Equal(11, slots.Count);
        Assert.All(slots, slot => Assert.Equal(TimeSpan.FromMinutes(30), slot.EndUtc - slot.StartUtc));
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), slots[0].StartUtc);
    }

    [Fact]
    public async Task ExistingAppointmentRemovesOverlappingSlots()
    {
        _db.Appointments.Add(Appointment("practice-a", 12, Utc(2026, 1, 5, 10), Utc(2026, 1, 5, 10, 30)));
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query());

        Assert.DoesNotContain(slots, x => x.StartUtc < Offset(2026, 1, 5, 10, 30) && x.EndUtc > Offset(2026, 1, 5, 10));
    }

    [Fact]
    public async Task BlockedTimeRemovesOverlappingSlots()
    {
        _db.SchedulingBlockedTimes.Add(new SchedulingBlockedTime
        {
            TenantId = "practice-a", ProviderId = 12, LocationId = _location,
            StartUtc = Utc(2026, 1, 5, 10), EndUtc = Utc(2026, 1, 5, 10, 30), Reason = "Team meeting"
        });
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query());

        Assert.DoesNotContain(slots, x => x.StartUtc < Offset(2026, 1, 5, 10, 30) && x.EndUtc > Offset(2026, 1, 5, 10));
    }

    [Fact]
    public async Task UnapprovedBookingRequestDoesNotBlockAvailability()
    {
        _db.BookingRequests.Add(new BookingRequest
        {
            TenantId = "practice-a", EventId = Guid.NewGuid(), Name = "Not logged", Phone = "4805550100",
            PreferredStartUtc = Utc(2026, 1, 5, 10), PreferredDurationMinutes = 30,
            RequestedProviderId = 12, RequestedLocationId = _location, Status = BookingRequestStatus.InReview
        });
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query());

        Assert.Contains(slots, x => x.StartUtc == Offset(2026, 1, 5, 10));
    }

    [Fact]
    public async Task ProviderAndLocationFiltersSelectOnlyMatchingSchedules()
    {
        var otherLocation = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        _db.SchedulingProviderWorkingHours.Add(WorkingHours("practice-a", 13, otherLocation, DayOfWeek.Monday));
        AddMapping(SchedulingResourceType.Provider, "13", "provider-13");
        AddMapping(SchedulingResourceType.Location, otherLocation.ToString(), "location-b");
        await _db.SaveChangesAsync();

        var providerSlots = await Service().GetAvailabilityAsync(Query() with { ProviderId = 13 });
        var locationSlots = await Service().GetAvailabilityAsync(Query() with { LocationId = _location });

        Assert.NotEmpty(providerSlots);
        Assert.All(providerSlots, x => Assert.Equal(13, x.ProviderId));
        Assert.NotEmpty(locationSlots);
        Assert.All(locationSlots, x => Assert.Equal(_location, x.LocationId));
    }

    [Fact]
    public async Task NewAndExistingPatientEligibilityAreApplied()
    {
        _db.SchedulingAppointmentTypes.Add(new SchedulingAppointmentTypeDefinition
        {
            TenantId = "practice-a", AppointmentTypeId = "follow-up", DisplayName = "Follow-up",
            DurationMinutes = 30, NewPatientAllowed = false, ExistingPatientAllowed = true, IsActive = true
        });
        AddMapping(SchedulingResourceType.VisitReason, "follow-up", "reason-follow-up");
        await _db.SaveChangesAsync();

        var newPatientSlots = await Service().GetAvailabilityAsync(Query(PatientRelationship.New));
        var existingPatientSlots = await Service().GetAvailabilityAsync(Query(PatientRelationship.Existing));
        var followUpSlots = await Service().GetAvailabilityAsync(Query(PatientRelationship.Existing) with
            { AppointmentTypeId = "follow-up" });

        Assert.NotEmpty(newPatientSlots);
        Assert.Empty(existingPatientSlots);
        Assert.NotEmpty(followUpSlots);
        Assert.All(followUpSlots, x => Assert.Equal("follow-up", x.AppointmentTypeId));
    }

    [Fact]
    public async Task DisabledAppointmentTypeReturnsNoSlots()
    {
        (await _db.SchedulingAppointmentTypes.SingleAsync()).IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Empty(await Service().GetAvailabilityAsync(Query()));
    }

    [Fact]
    public async Task DisabledChannelReturnsNoSlots()
    {
        (await _db.SchedulingIntegrationConfigurations.SingleAsync()).Enabled = false;
        await _db.SaveChangesAsync();

        Assert.Empty(await Service().GetAvailabilityAsync(Query()));
    }

    [Fact]
    public async Task MinimumLeadTimeRemovesSlotsBeforeThreshold()
    {
        var configuration = await _db.SchedulingIntegrationConfigurations.SingleAsync();
        configuration.MinimumBookingLeadMinutes = 24 * 60 + 60; // Monday 13:00 UTC
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query() with { ToUtc = Offset(2026, 1, 5, 17) });

        Assert.All(slots, x => Assert.True(x.StartUtc >= Offset(2026, 1, 5, 13)));
    }

    [Fact]
    public async Task MaximumBookingHorizonRemovesLaterSlots()
    {
        var configuration = await _db.SchedulingIntegrationConfigurations.SingleAsync();
        configuration.MaximumBookingHorizonDays = 0;
        await _db.SaveChangesAsync();

        Assert.Empty(await Service().GetAvailabilityAsync(Query()));
    }

    [Fact]
    public async Task AppointmentsFromAnotherTenantDoNotBlockSlots()
    {
        _db.Appointments.Add(Appointment("practice-b", 12, Utc(2026, 1, 5, 10), Utc(2026, 1, 5, 10, 30)));
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query());

        Assert.Contains(slots, x => x.StartUtc == Offset(2026, 1, 5, 10));
    }

    [Fact]
    public async Task MissingActiveChannelMappingPreventsExposure()
    {
        (await _db.ExternalSchedulingResourceMappings.SingleAsync(x =>
            x.ResourceType == SchedulingResourceType.Provider)).IsActive = false;
        await _db.SaveChangesAsync();

        Assert.Empty(await Service().GetAvailabilityAsync(Query()));
    }

    [Fact]
    public async Task DaylightSavingGapNeverProducesNonexistentLocalSlots()
    {
        var configuration = await _db.SchedulingIntegrationConfigurations.SingleAsync();
        configuration.TimeZoneId = "America/New_York";
        _db.SchedulingProviderWorkingHours.RemoveRange(_db.SchedulingProviderWorkingHours);
        _db.SchedulingProviderWorkingHours.Add(new SchedulingProviderWorkingHours
        {
            TenantId = "practice-a", ProviderId = 12, LocationId = _location, DayOfWeek = DayOfWeek.Sunday,
            StartLocal = new TimeOnly(1, 0), EndLocal = new TimeOnly(4, 0)
        });
        _clock.UtcNow = Offset(2026, 3, 7, 0);
        await _db.SaveChangesAsync();

        var slots = await Service().GetAvailabilityAsync(Query() with
        {
            FromUtc = Offset(2026, 3, 8, 5), ToUtc = Offset(2026, 3, 8, 9)
        });
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.NotEmpty(slots);
        Assert.DoesNotContain(slots, x => TimeZoneInfo.ConvertTime(x.StartUtc, zone).Hour == 2);
        Assert.All(slots, x => Assert.Equal(TimeSpan.Zero, x.StartUtc.Offset));
    }

    [Fact]
    public async Task SchemaBootstrapUpgradesLegacyAppointmentsAndConfiguration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE SchedulingIntegrationConfigurations (
                  Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, Channel INTEGER NOT NULL,
                  Enabled INTEGER NOT NULL, Environment TEXT NOT NULL, CredentialReference TEXT NULL,
                  CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
                CREATE TABLE Appointments (
                  Id TEXT NOT NULL PRIMARY KEY, PatientId INTEGER NOT NULL, ProviderId INTEGER NOT NULL,
                  StartTime TEXT NOT NULL, EndTime TEXT NOT NULL, Status INTEGER NOT NULL,
                  ProcedureCodes TEXT NULL, Notes TEXT NULL, Operatory TEXT NULL, LocationId TEXT NULL,
                  CreatedAt TEXT NOT NULL);
                CREATE TABLE BookingRequests (
                  Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, ApprovedAppointmentId TEXT NULL);
                """;
            await command.ExecuteNonQueryAsync();
        }
        await using var context = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlite(connection).Options);

        await SchedulingAvailabilitySchema.EnsureAsync(context);

        Assert.True(await HasColumn(connection, "Appointments", "TenantId"));
        Assert.True(await HasColumn(connection, "Appointments", "AppointmentTypeId"));
        Assert.True(await HasColumn(connection, "SchedulingIntegrationConfigurations", "TimeZoneId"));
        Assert.True(await HasTable(connection, "SchedulingProviderWorkingHours"));
        Assert.True(await HasTable(connection, "SchedulingBlockedTimes"));
    }

    private SchedulingAvailabilityService Service() => new(
        _db, _clock, NullLogger<SchedulingAvailabilityService>.Instance);

    private SchedulingAvailabilityQuery Query(PatientRelationship relationship = PatientRelationship.New) => new()
    {
        TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc,
        AppointmentTypeId = "new-patient-exam", PatientRelationship = relationship,
        FromUtc = Offset(2026, 1, 5, 9), ToUtc = Offset(2026, 1, 5, 12)
    };

    private async Task SeedOfferableSchedule()
    {
        _db.SchedulingIntegrationConfigurations.Add(new SchedulingIntegrationConfiguration
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true,
            TimeZoneId = "UTC", MinimumBookingLeadMinutes = 0, MaximumBookingHorizonDays = 90
        });
        _db.SchedulingAppointmentTypes.Add(new SchedulingAppointmentTypeDefinition
        {
            TenantId = "practice-a", AppointmentTypeId = "new-patient-exam",
            DisplayName = "New Patient Exam", DurationMinutes = 30,
            NewPatientAllowed = true, ExistingPatientAllowed = false, IsActive = true
        });
        _db.SchedulingProviderWorkingHours.Add(WorkingHours("practice-a", 12, _location, DayOfWeek.Monday));
        AddMapping(SchedulingResourceType.Provider, "12", "provider-12");
        AddMapping(SchedulingResourceType.Location, _location.ToString(), "location-a");
        AddMapping(SchedulingResourceType.VisitReason, "new-patient-exam", "reason-exam");
        await _db.SaveChangesAsync();
    }

    private static SchedulingProviderWorkingHours WorkingHours(
        string tenantId, int providerId, Guid locationId, DayOfWeek day) => new()
    {
        TenantId = tenantId, ProviderId = providerId, LocationId = locationId, DayOfWeek = day,
        StartLocal = new TimeOnly(9, 0), EndLocal = new TimeOnly(12, 0)
    };

    private void AddMapping(SchedulingResourceType type, string internalId, string externalId) =>
        _db.ExternalSchedulingResourceMappings.Add(new ExternalSchedulingResourceMapping
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, ResourceType = type,
            InternalId = internalId, ExternalId = externalId, IsActive = true
        });

    private static Appointment Appointment(string tenantId, int providerId, DateTime start, DateTime end) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, PatientId = 1, ProviderId = providerId,
        StartTime = start, EndTime = end, Status = AppointmentStatus.Scheduled
    };

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static DateTimeOffset Offset(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static async Task<bool> HasColumn(SqliteConnection connection, string table, string column)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<bool> HasTable(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : ISchedulingClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class SchedulingIntegrationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ResolverReturnsRegisteredAdapterForEnabledTenantConfiguration()
    {
        _db.SchedulingIntegrationConfigurations.Add(Configuration("practice-a", SchedulingChannel.Google, true));
        await _db.SaveChangesAsync();
        var adapter = new FakeAdapter(SchedulingChannel.Google);
        var resolver = Resolver(adapter);

        var resolved = await resolver.ResolveAsync("practice-a", SchedulingChannel.Google);

        Assert.Same(adapter, resolved);
    }

    [Fact]
    public async Task ResolverRejectsDisabledIntegration()
    {
        _db.SchedulingIntegrationConfigurations.Add(Configuration("practice-a", SchedulingChannel.Google, false));
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<SchedulingIntegrationDisabledException>(() =>
            Resolver(new FakeAdapter(SchedulingChannel.Google)).ResolveAsync("practice-a", SchedulingChannel.Google));
    }

    [Fact]
    public async Task ResolverRejectsUnsupportedChannel()
    {
        await Assert.ThrowsAsync<UnsupportedSchedulingChannelException>(() =>
            Resolver(new FakeAdapter(SchedulingChannel.Google)).ResolveAsync("practice-a", SchedulingChannel.Zocdoc));
    }

    [Fact]
    public void ResolverReportsDuplicateAdapterChannelClearly()
    {
        var exception = Assert.Throws<DuplicateSchedulingChannelAdapterException>(() => Resolver(
            new FakeAdapter(SchedulingChannel.Google),
            new FakeAdapter(SchedulingChannel.Google)));

        Assert.Equal(SchedulingChannel.Google, exception.Channel);
        Assert.Equal(2, exception.RegistrationCount);
        Assert.Contains("Google", exception.Message);
    }

    [Fact]
    public async Task ResolverDoesNotBorrowConfigurationFromAnotherTenant()
    {
        _db.SchedulingIntegrationConfigurations.Add(Configuration("practice-b", SchedulingChannel.Google, true));
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<SchedulingIntegrationDisabledException>(() =>
            Resolver(new FakeAdapter(SchedulingChannel.Google)).ResolveAsync("practice-a", SchedulingChannel.Google));
    }

    [Theory]
    [InlineData(PatientRelationship.New)]
    [InlineData(PatientRelationship.Existing)]
    [InlineData(PatientRelationship.Unknown)]
    public void AvailabilityPreservesPatientRelationshipAsRoutingInformation(PatientRelationship relationship)
    {
        var slot = new SchedulingAvailabilitySlot
        {
            TenantId = "practice-a", ProviderId = 12, LocationId = Guid.NewGuid(), AppointmentTypeId = "exam",
            StartUtc = DateTimeOffset.UtcNow, EndUtc = DateTimeOffset.UtcNow.AddMinutes(30), PatientRelationship = relationship
        };

        Assert.Equal(relationship, slot.PatientRelationship);
    }

    [Theory]
    [InlineData(PatientRelationship.New)]
    [InlineData(PatientRelationship.Existing)]
    [InlineData(PatientRelationship.Unknown)]
    public void PatientRelationshipNeverSubstitutesForResolvedPatient(PatientRelationship relationship)
    {
        var command = BookingCommand(relationship) with { ResolvedPatientId = 0 };

        Assert.Throws<ArgumentException>(() => SchedulingBookingRules.ValidateForAppointmentCreation(command));
    }

    [Fact]
    public void BookingBoundaryAcceptsInternallyResolvedPatient()
    {
        SchedulingBookingRules.ValidateForAppointmentCreation(BookingCommand(PatientRelationship.Unknown));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BookingBoundaryRejectsMissingCanonicalIdentifiers(bool missingExternalAppointment)
    {
        var valid = BookingCommand(PatientRelationship.Unknown);
        var command = missingExternalAppointment
            ? valid with { ExternalAppointmentId = " " }
            : valid with { AppointmentTypeId = " " };

        Assert.Throws<ArgumentException>(() => SchedulingBookingRules.ValidateForAppointmentCreation(command));
    }

    [Fact]
    public async Task ProviderMappingIsCanonicalAndTenantScoped()
    {
        var store = new ExternalSchedulingResourceMappingStore(_db);
        await store.AddAsync(new ExternalSchedulingResourceMapping
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Google,
            ResourceType = SchedulingResourceType.Provider, InternalId = "42", ExternalId = "google-provider-7"
        });

        var found = await store.FindByExternalIdAsync(
            "practice-a", SchedulingChannel.Google, SchedulingResourceType.Provider, "google-provider-7");
        var otherTenant = await store.FindByExternalIdAsync(
            "practice-b", SchedulingChannel.Google, SchedulingResourceType.Provider, "google-provider-7");

        Assert.Equal("42", found!.InternalId);
        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task ExternalAppointmentReferenceRoundTripsAndIsTenantScoped()
    {
        var store = new ExternalAppointmentReferenceStore(_db);
        var appointmentId = Guid.NewGuid();
        await store.AddAsync(new ExternalAppointmentReference
        {
            TenantId = "practice-a", AppointmentId = appointmentId, Channel = SchedulingChannel.Other,
            ExternalAppointmentId = "external-123", ExternalProviderId = "provider-8",
            ExternalLocationId = "location-4", ExternalVisitReasonId = "reason-2"
        });

        var found = await store.FindAsync("practice-a", SchedulingChannel.Other, "external-123");
        var otherTenant = await store.FindAsync("practice-b", SchedulingChannel.Other, "external-123");

        Assert.NotNull(found);
        Assert.Equal(appointmentId, found!.AppointmentId);
        Assert.Equal("provider-8", found.ExternalProviderId);
        Assert.Null(otherTenant);
    }

    [Fact]
    public async Task DuplicateExternalEventAcquiresExactlyOneLease()
    {
        var store = new SchedulingIntegrationIdempotencyStore(_db);

        var first = await store.TryBeginAsync("practice-a", SchedulingChannel.Zocdoc, "event-99");
        var duplicate = await store.TryBeginAsync("practice-a", SchedulingChannel.Zocdoc, "event-99");
        var appointmentId = Guid.NewGuid();
        await store.CompleteAsync("practice-a", SchedulingChannel.Zocdoc, "event-99", appointmentId);
        var afterCompletion = await store.TryBeginAsync("practice-a", SchedulingChannel.Zocdoc, "event-99");

        Assert.True(first.Acquired);
        Assert.False(duplicate.Acquired);
        Assert.Equal(first.Id, duplicate.Id);
        Assert.False(afterCompletion.Acquired);
        Assert.Equal(appointmentId, afterCompletion.AppointmentId);
        Assert.Single(await _db.SchedulingIntegrationEvents.ToListAsync());
    }

    [Fact]
    public async Task SameExternalEventIsIndependentAcrossTenantsAndChannels()
    {
        var store = new SchedulingIntegrationIdempotencyStore(_db);

        var first = await store.TryBeginAsync("practice-a", SchedulingChannel.Zocdoc, "event-1");
        var tenantTwo = await store.TryBeginAsync("practice-b", SchedulingChannel.Zocdoc, "event-1");
        var otherChannel = await store.TryBeginAsync("practice-a", SchedulingChannel.Google, "event-1");

        Assert.True(first.Acquired);
        Assert.True(tenantTwo.Acquired);
        Assert.True(otherChannel.Acquired);
        Assert.Equal(3, await _db.SchedulingIntegrationEvents.CountAsync());
    }

    private SchedulingChannelAdapterResolver Resolver(params ISchedulingChannelAdapter[] adapters) =>
        new(adapters, new SchedulingIntegrationConfigurationStore(_db));

    private static SchedulingIntegrationConfiguration Configuration(
        string tenantId, SchedulingChannel channel, bool enabled) => new()
        {
            TenantId = tenantId, Channel = channel, Enabled = enabled,
            Environment = "Sandbox", CredentialReference = "keyvault://scheduling/example"
        };

    private static SchedulingBookingCommand BookingCommand(PatientRelationship relationship) => new()
    {
        TenantId = "practice-a", Channel = SchedulingChannel.Other, ExternalEventId = "event-1",
        ExternalAppointmentId = "appointment-1", ResolvedPatientId = 42, ProviderId = 12,
        LocationId = Guid.NewGuid(), AppointmentTypeId = "exam", StartUtc = DateTime.UtcNow,
        EndUtc = DateTime.UtcNow.AddMinutes(30), PatientRelationship = relationship
    };

    private sealed class FakeAdapter(SchedulingChannel channel) : ISchedulingChannelAdapter
    {
        public SchedulingChannel Channel { get; } = channel;
    }
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

public sealed class PublicWebsiteSchedulingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private readonly FakeAvailability _availability = new();
    private readonly Guid _location = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _db.SchedulingAppointmentTypes.Add(new() { TenantId = "practice-a", AppointmentTypeId = "exam", DisplayName = "New patient exam", DurationMinutes = 60, NewPatientAllowed = true, ExistingPatientAllowed = false });
        _db.ExternalSchedulingResourceMappings.AddRange(
            Map(SchedulingResourceType.Provider, "12", "dr-phillips", "Dr. Phillips"),
            Map(SchedulingResourceType.Location, _location.ToString(), "tempe", "Tempe office"),
            Map(SchedulingResourceType.VisitReason, "exam", "new-exam", "New patient exam"));
        await _db.SaveChangesAsync();
        _availability.Slots = [new() { TenantId = "practice-a", ProviderId = 12, LocationId = _location,
            AppointmentTypeId = "exam", StartUtc = At(10), EndUtc = At(11), PatientRelationship = PatientRelationship.New }];
    }
    public async Task DisposeAsync() { await _db.DisposeAsync(); await _connection.DisposeAsync(); }

    [Fact]
    public async Task ReturnsOnlyPublicAliasesAndRoutesRelationshipToCanonicalEngine()
    {
        var result = await Service().GetAsync("practice-a", Request(PatientRelationship.New));
        var slot = Assert.Single(result);
        Assert.Equal("new-exam", slot.AppointmentTypeCode); Assert.Equal("dr-phillips", slot.ProviderCode);
        Assert.Equal("tempe", slot.LocationCode); Assert.DoesNotContain("practice-a", System.Text.Json.JsonSerializer.Serialize(slot));
        Assert.Equal(PatientRelationship.New, _availability.LastQuery!.PatientRelationship);
    }

    [Fact]
    public async Task OpaqueSelectionCannotCrossTenantsAndIsRevalidated()
    {
        var offered = Assert.Single(await Service().GetAsync("practice-a", Request(PatientRelationship.New)));
        var encoded = offered.AvailabilityToken.Replace('-', '+').Replace('_', '/');
        encoded += new string('=', (4 - encoded.Length % 4) % 4);
        Assert.DoesNotContain("practice-a", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        Assert.Null(await Service().ValidateAsync("practice-b", offered.AvailabilityToken, PatientRelationship.New));
        Assert.NotNull(await Service().ValidateAsync("practice-a", offered.AvailabilityToken, PatientRelationship.New));
        var tamperIndex = offered.AvailabilityToken.Length / 2;
        var tampered = offered.AvailabilityToken[..tamperIndex] +
            (offered.AvailabilityToken[tamperIndex] == 'A' ? 'B' : 'A') + offered.AvailabilityToken[(tamperIndex + 1)..];
        Assert.Null(await Service().ValidateAsync("practice-a", tampered, PatientRelationship.New));
        _availability.Slots = [];
        Assert.Null(await Service().ValidateAsync("practice-a", offered.AvailabilityToken, PatientRelationship.New));
        Assert.Empty(_db.Appointments);
    }

    [Fact]
    public async Task ExistingPatientRelationshipIsNotSubstitutedForIdentity()
    {
        _availability.Slots = [];
        Assert.Empty(await Service().GetAsync("practice-a", Request(PatientRelationship.Existing)));
        Assert.Equal(PatientRelationship.Existing, _availability.LastQuery!.PatientRelationship);
    }

    [Fact]
    public void InternalCredentialResolvesOnlyItsConfiguredTenant()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalApi:PublicIntakeClients:0:TenantId"] = "practice-a",
            ["InternalApi:PublicIntakeClients:0:ApiKey"] = "first-secret",
            ["InternalApi:PublicIntakeClients:1:TenantId"] = "practice-b",
            ["InternalApi:PublicIntakeClients:1:ApiKey"] = "second-secret"
        }).Build();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Headers["X-CDO-Service-Key"] = "first-secret";
        Assert.Equal("practice-a", SchedulingInternalAuth.ResolveTenant(http, config));
        http.Request.Headers["X-CDO-Service-Key"] = "wrong";
        Assert.Null(SchedulingInternalAuth.ResolveTenant(http, config));
    }

    private PublicWebsiteSchedulingService Service() => new(_db, _availability,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["PublicAvailability:SlotTokenKey"] = "test-slot-key-at-least-thirty-two-characters" }).Build());
    private PublicSchedulingAvailabilityRequest Request(PatientRelationship relationship) => new(relationship, At(9), At(17));
    private ExternalSchedulingResourceMapping Map(SchedulingResourceType type, string internalId, string externalId, string name) =>
        new() { TenantId = "practice-a", Channel = SchedulingChannel.PublicWebsite, ResourceType = type,
            InternalId = internalId, ExternalId = externalId, ExternalDisplayName = name };
    private static DateTimeOffset At(int hour) => new(2030, 8, 12, hour, 0, 0, TimeSpan.Zero);

    private sealed class FakeAvailability : ISchedulingAvailabilityService
    {
        public IReadOnlyList<SchedulingAvailabilitySlot> Slots { get; set; } = [];
        public SchedulingAvailabilityQuery? LastQuery { get; private set; }
        public Task<IReadOnlyList<SchedulingAvailabilitySlot>> GetAvailabilityAsync(SchedulingAvailabilityQuery query, CancellationToken cancellationToken = default)
        { LastQuery = query; return Task.FromResult(Slots); }
    }
}

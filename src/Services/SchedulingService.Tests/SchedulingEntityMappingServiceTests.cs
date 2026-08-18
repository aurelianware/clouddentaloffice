using System.Security.Claims;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

public sealed class SchedulingEntityMappingServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;
    private ISchedulingEntityMappingService _service = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new SchedulingDbContext(new DbContextOptionsBuilder<SchedulingDbContext>()
            .UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new SchedulingEntityMappingService(_db, new SchedulingEntityCatalog(_db));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task MapsProviderAndSupportsReverseLookup()
    {
        await AddCatalogRequest("practice-a", providerId: 42);

        var created = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "zocdoc-provider-7", "Dr. Rivera"));
        var byInternal = await _service.FindByInternalIdAsync(
            "practice-a", SchedulingChannel.Zocdoc, SchedulingResourceType.Provider, "42");
        var byExternal = await _service.FindByExternalIdAsync(
            "practice-a", SchedulingChannel.Zocdoc, SchedulingResourceType.Provider, "zocdoc-provider-7");

        Assert.Equal(created.Id, byInternal!.Id);
        Assert.Equal("42", byExternal!.InternalId);
        Assert.Equal("Dr. Rivera", byExternal.ExternalDisplayName);
    }

    [Fact]
    public async Task MapsLocationUsingCanonicalGuid()
    {
        var locationId = Guid.NewGuid();
        await AddCatalogRequest("practice-a", locationId: locationId);

        var mapping = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Location, locationId.ToString(), "zocdoc-location-2", "Mesa"));

        Assert.Equal(locationId.ToString(), mapping.InternalId);
        Assert.Equal("zocdoc-location-2", mapping.ExternalId);
    }

    [Fact]
    public async Task MapsAppointmentTypeToVisitReason()
    {
        _db.SchedulingAppointmentTypes.Add(AppointmentType("practice-a", "new-patient-exam"));
        await _db.SaveChangesAsync();

        var mapping = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.VisitReason, "new-patient-exam", "visit-reason-101", "New patient exam"));

        Assert.Equal(SchedulingResourceType.VisitReason, mapping.EntityType);
        Assert.Equal("visit-reason-101", mapping.ExternalId);
    }

    [Fact]
    public async Task RejectsDuplicateActiveExternalMapping()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        await AddCatalogRequest("practice-a", providerId: 43);
        await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-shared", null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpsertAsync(
            "practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "43", "provider-shared", null)));
    }

    [Fact]
    public async Task RejectsInactiveDuplicatesBeforeDatabaseConstraintFailure()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        await AddCatalogRequest("practice-a", providerId: 43);
        await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-shared", null, IsActive: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpsertAsync(
            "practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "43", "provider-shared", null, IsActive: false)));
    }

    [Fact]
    public async Task FindsMappingByIdWithinTenantAndChannel()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        var mapping = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-42", null));

        Assert.Equal(mapping.Id, (await _service.FindByIdAsync(
            "practice-a", SchedulingChannel.Zocdoc, mapping.Id))!.Id);
        Assert.Null(await _service.FindByIdAsync("practice-b", SchedulingChannel.Zocdoc, mapping.Id));
        Assert.Null(await _service.FindByIdAsync("practice-a", SchedulingChannel.Google, mapping.Id));
    }

    [Fact]
    public async Task MappingLookupAndUpdateAreTenantIsolated()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        await AddCatalogRequest("practice-b", providerId: 42);
        var mapping = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-a", null));

        Assert.Null(await _service.FindByExternalIdAsync(
            "practice-b", SchedulingChannel.Zocdoc, SchedulingResourceType.Provider, "provider-a"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpsertAsync(
            "practice-b", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-b", null), mapping.Id));
    }

    [Fact]
    public async Task InactiveMappingsAreExcludedFromLookupsAndDefaultLists()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        var mapping = await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-42", null));
        await _service.DeactivateAsync("practice-a", SchedulingChannel.Zocdoc, mapping.Id);

        Assert.Null(await _service.FindByInternalIdAsync(
            "practice-a", SchedulingChannel.Zocdoc, SchedulingResourceType.Provider, "42"));
        Assert.Empty(await _service.ListAsync("practice-a", SchedulingChannel.Zocdoc));
        Assert.Single(await _service.ListAsync("practice-a", SchedulingChannel.Zocdoc, includeInactive: true));
    }

    [Fact]
    public async Task ListsUnmappedAndStaleMappings()
    {
        await AddCatalogRequest("practice-a", providerId: 42);
        await AddCatalogRequest("practice-a", providerId: 43);
        await _service.UpsertAsync("practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-42", null));
        _db.ExternalSchedulingResourceMappings.Add(new ExternalSchedulingResourceMapping
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc,
            ResourceType = SchedulingResourceType.Provider, InternalId = "99", ExternalId = "deleted-provider"
        });
        await _db.SaveChangesAsync();

        var unmapped = await _service.ListUnmappedAsync(
            "practice-a", SchedulingChannel.Zocdoc, SchedulingResourceType.Provider);
        var invalid = await _service.ListInvalidAsync("practice-a", SchedulingChannel.Zocdoc);

        Assert.Contains(unmapped, x => x.InternalId == "43");
        Assert.DoesNotContain(unmapped, x => x.InternalId == "42");
        Assert.Single(invalid, x => x.InternalId == "99");
    }

    [Fact]
    public async Task RejectsMissingEntitiesMalformedIdsAndUnsupportedChannels()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpsertAsync(
            "practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "42", "provider-42", null)));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.UpsertAsync(
            "practice-a", SchedulingChannel.Zocdoc,
            new(SchedulingResourceType.Provider, "not-an-integer", "provider-42", null)));
        await Assert.ThrowsAsync<UnsupportedSchedulingChannelException>(() => _service.ListAsync(
            "practice-a", SchedulingChannel.Other));
    }

    [Fact]
    public void AppointmentTypeEligibilityUsesCanonicalPatientRelationship()
    {
        var appointmentType = AppointmentType("practice-a", "new-patient-exam");

        Assert.Equal(90, appointmentType.DurationMinutes);
        Assert.True(appointmentType.Allows(PatientRelationship.New));
        Assert.False(appointmentType.Allows(PatientRelationship.Existing));
        Assert.False(appointmentType.Allows(PatientRelationship.Unknown));
        Assert.Null(appointmentType.ProviderId);
        Assert.Null(appointmentType.LocationId);
    }

    [Fact]
    public void AdminTenantComesOnlyFromAuthenticatedClaims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "practice-a")], "test"));

        Assert.Equal("practice-a", SchedulingIntegrationAdminApi.TenantId(principal));
        Assert.Null(SchedulingIntegrationAdminApi.TenantId(new ClaimsPrincipal()));
    }

    [Fact]
    public async Task SchemaBootstrapUpgradesExistingMappingTableAndCreatesAppointmentTypes()
    {
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE ExternalSchedulingResourceMappings;");
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE SchedulingAppointmentTypes;");
        await _db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE ExternalSchedulingResourceMappings (
              Id TEXT NOT NULL PRIMARY KEY, TenantId TEXT NOT NULL, Channel INTEGER NOT NULL,
              ResourceType INTEGER NOT NULL, InternalId TEXT NOT NULL, ExternalId TEXT NOT NULL,
              CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            """);

        await SchedulingIntegrationSchema.EnsureAsync(_db);

        Assert.True(await HasColumn("ExternalSchedulingResourceMappings", "ExternalDisplayName"));
        Assert.True(await HasColumn("ExternalSchedulingResourceMappings", "IsActive"));
        Assert.True(await HasTable("SchedulingAppointmentTypes"));
    }

    private async Task AddCatalogRequest(string tenantId, int? providerId = null, Guid? locationId = null)
    {
        _db.BookingRequests.Add(new BookingRequest
        {
            TenantId = tenantId, EventId = Guid.NewGuid(), Name = "Catalog seed", Phone = "4805550100",
            PreferredStartUtc = DateTime.UtcNow.AddDays(1), RequestedProviderId = providerId,
            RequestedLocationId = locationId
        });
        await _db.SaveChangesAsync();
    }

    private static SchedulingAppointmentTypeDefinition AppointmentType(string tenantId, string id) => new()
    {
        TenantId = tenantId, AppointmentTypeId = id, DisplayName = "New Patient Comprehensive Exam",
        DurationMinutes = 90, NewPatientAllowed = true, ExistingPatientAllowed = false,
        ProviderId = null, LocationId = null, IsActive = true
    };

    private async Task<bool> HasColumn(string table, string column)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $name";
        command.Parameters.AddWithValue("$name", column);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }

    private async Task<bool> HasTable(string table)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync()) == 1;
    }
}

using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SchedulingService.Integrations.Zocdoc;

public sealed class ZocdocOperationsTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private SchedulingDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task ReconciliationIsTenantScopedAndDoesNotExposePatientData()
    {
        var old = DateTime.UtcNow.AddDays(-2);
        _db.ExternalAppointmentReferences.AddRange(
            new ExternalAppointmentReference
            {
                TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc,
                AppointmentId = Guid.NewGuid(), ExternalAppointmentId = "external-a",
                SyncStatus = ExternalAppointmentSyncStatus.Conflict, UpdatedAt = old
            },
            new ExternalAppointmentReference
            {
                TenantId = "practice-b", Channel = SchedulingChannel.Zocdoc,
                AppointmentId = Guid.NewGuid(), ExternalAppointmentId = "external-b",
                SyncStatus = ExternalAppointmentSyncStatus.Failed, UpdatedAt = old
            });
        _db.SchedulingIntegrationEvents.Add(new SchedulingIntegrationEvent
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc,
            ExternalEventId = "event-a", Status = SchedulingIntegrationEventStatus.Failed,
            FailureReason = "sanitized"
        });
        await _db.SaveChangesAsync();

        var report = await Service().ReconcileAsync("practice-a", TimeSpan.FromHours(24));

        Assert.Equal(1, report.MissingLocalAppointments);
        Assert.Equal(1, report.IntegrationConflicts);
        Assert.Equal(1, report.FailedInboundEvents);
        Assert.Equal(0, report.FailedOutboundAppointments);
        Assert.DoesNotContain("external-a", string.Join(' ', report.Diagnostics));
    }

    [Fact]
    public async Task ReconciliationRejectsUnboundedStalenessWindows()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().ReconcileAsync("practice-a", TimeSpan.FromMinutes(1)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Service().ReconcileAsync("practice-a", TimeSpan.FromDays(31)));
    }

    [Fact]
    public async Task ReadinessWithoutAuthenticationProbeReportsStatusWithoutExternalCall()
    {
        _db.SchedulingIntegrationConfigurations.Add(new SchedulingIntegrationConfiguration
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true,
            Environment = "Sandbox", CredentialReference = "sandbox", MaximumBookingHorizonDays = 90
        });
        await _db.SaveChangesAsync();
        var mappings = new FakeMappings();
        var resolver = new FakeResolver();
        var service = new ZocdocOperationsService(_db, mappings, new FakeCredentials(), resolver,
            TimeProvider.System);

        var status = await service.GetReadinessAsync("practice-a", false);

        Assert.False(status.Ready);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(1, mappings.InvalidCalls);
        Assert.True(status.Checks.Single(x => x.Name == "Configuration valid").Ready);
        Assert.True(status.Checks.Single(x => x.Name == "Webhook configured").Ready);
        Assert.False(status.Checks.Single(x => x.Name == "Authentication successful").Ready);
        Assert.Contains("Not probed", status.Checks.Single(x => x.Name == "Authentication successful").Detail);
    }

    [Fact]
    public async Task ReadinessAuthenticationProbeUsesAdapterAndReportsSuccess()
    {
        _db.SchedulingIntegrationConfigurations.Add(new SchedulingIntegrationConfiguration
        {
            TenantId = "practice-a", Channel = SchedulingChannel.Zocdoc, Enabled = true,
            Environment = "Sandbox", CredentialReference = "sandbox", MaximumBookingHorizonDays = 90
        });
        await _db.SaveChangesAsync();
        var resolver = new FakeResolver();
        var service = new ZocdocOperationsService(_db, new FakeMappings(), new FakeCredentials(), resolver,
            TimeProvider.System);

        var status = await service.GetReadinessAsync("practice-a", true);

        Assert.Equal(1, resolver.ResolveCalls);
        Assert.Equal(1, resolver.Adapter.ValidateCalls);
        Assert.True(status.Checks.Single(x => x.Name == "Authentication successful").Ready);
    }

    private ZocdocOperationsService Service() => new(_db, null!, null!, null!, TimeProvider.System);

    private sealed class FakeCredentials : IZocdocCredentialProvider
    {
        public Task<ZocdocCredentials> GetAsync(string tenantId,
            SchedulingIntegrationConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ZocdocCredentials("client", "secret", "webhook-secret"));
    }

    private sealed class FakeResolver : ISchedulingChannelAdapterResolver
    {
        public FakeAdapter Adapter { get; } = new();
        public int ResolveCalls { get; private set; }
        public Task<ISchedulingChannelAdapter> ResolveAsync(string tenantId, SchedulingChannel channel,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return Task.FromResult<ISchedulingChannelAdapter>(Adapter);
        }
    }

    private sealed class FakeAdapter : ISchedulingExternalEntitySource
    {
        public SchedulingChannel Channel => SchedulingChannel.Zocdoc;
        public int ValidateCalls { get; private set; }
        public Task ValidateConnectionAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<ExternalSchedulingEntity>> GetExternalEntitiesAsync(string tenantId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExternalSchedulingEntity>>([]);
    }

    private sealed class FakeMappings : ISchedulingEntityMappingService
    {
        public int InvalidCalls { get; private set; }
        public Task<IReadOnlyList<SchedulingInternalEntity>> ListUnmappedAsync(string tenantId,
            SchedulingChannel channel, SchedulingResourceType entityType,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SchedulingInternalEntity>>([]);
        public Task<IReadOnlyList<SchedulingEntityMappingDto>> ListInvalidAsync(string tenantId,
            SchedulingChannel channel, CancellationToken cancellationToken = default)
        {
            InvalidCalls++;
            return Task.FromResult<IReadOnlyList<SchedulingEntityMappingDto>>([]);
        }
        public Task<SchedulingEntityMappingDto?> FindByIdAsync(string tenantId, SchedulingChannel channel,
            Guid mappingId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchedulingEntityMappingDto?> FindByInternalIdAsync(string tenantId, SchedulingChannel channel,
            SchedulingResourceType entityType, string internalId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchedulingEntityMappingDto?> FindByExternalIdAsync(string tenantId, SchedulingChannel channel,
            SchedulingResourceType entityType, string externalId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SchedulingEntityMappingDto>> ListAsync(string tenantId, SchedulingChannel channel,
            SchedulingResourceType? entityType = null, bool includeInactive = false,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SchedulingEntityMappingDto> UpsertAsync(string tenantId, SchedulingChannel channel,
            UpsertSchedulingEntityMapping request, Guid? mappingId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeactivateAsync(string tenantId, SchedulingChannel channel, Guid mappingId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

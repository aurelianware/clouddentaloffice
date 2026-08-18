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

    private ZocdocOperationsService Service() => new(_db, null!, null!, null!, TimeProvider.System);
}

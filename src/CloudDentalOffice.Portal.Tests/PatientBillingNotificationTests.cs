using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientBillingNotificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly Guid _accountId = Guid.NewGuid();

    public PatientBillingNotificationTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new DefaultTenantProvider());
        _db.Database.EnsureCreated();
        _db.Tenants.Add(new TenantRegistry { TenantId = "tenant-a", Name = "Example Dental" });
        _db.Patients.Add(new Patient { PatientId = 42, TenantId = "tenant-a", FirstName = "Pat",
            LastName = "Person", Email = "patient@example.test", DateOfBirth = new(1990, 1, 1),
            Gender = "U", Status = "Active" });
        _db.PatientAccounts.Add(new PatientAccount { Id = _accountId, TenantId = "tenant-a", PatientId = 42,
            Status = PatientAccountStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        _db.SaveChanges();
    }

    [Theory]
    [InlineData(PatientBillingNotificationType.NewStatement)]
    [InlineData(PatientBillingNotificationType.BalanceDue)]
    [InlineData(PatientBillingNotificationType.PaymentReceived)]
    [InlineData(PatientBillingNotificationType.PaymentFailed)]
    public async Task Messages_are_generic_and_direct_patients_to_authenticated_portal(
        PatientBillingNotificationType type)
    {
        var options = Options.Create(new PatientBillingNotificationOptions { Enabled = true,
            PatientPortalBaseUrl = "https://portal.example.test" });
        var service = new PatientBillingNotificationService(_db, TimeProvider.System, options);
        Assert.True(await service.EnqueueAsync("tenant-a", _accountId, type, "test", Guid.NewGuid().ToString("N")));
        var sender = new CaptureSender();
        var dispatcher = new PatientBillingNotificationDispatcher(_db, service, sender, options,
            TimeProvider.System, NullLogger<PatientBillingNotificationDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        var message = Assert.Single(sender.Messages);
        Assert.Equal("patient@example.test", message.Recipient);
        Assert.Contains("Example Dental", message.Body);
        Assert.Contains("https://portal.example.test/patient/billing", message.Body);
        Assert.DoesNotContain("Pat Person", message.Body);
        Assert.DoesNotContain("procedure", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnosis", message.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$", message.Body);
    }

    [Fact]
    public async Task Duplicate_source_is_enqueued_once()
    {
        var options = Options.Create(new PatientBillingNotificationOptions { Enabled = true });
        var service = new PatientBillingNotificationService(_db, TimeProvider.System, options);
        Assert.True(await service.EnqueueAsync("tenant-a", _accountId,
            PatientBillingNotificationType.PaymentReceived, "payment", "opaque-payment"));
        Assert.False(await service.EnqueueAsync("tenant-a", _accountId,
            PatientBillingNotificationType.PaymentReceived, "payment", "opaque-payment"));
        Assert.Single(await _db.PatientBillingNotifications.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class CaptureSender : IPatientBillingNotificationSender
    {
        public List<BillingNotificationMessage> Messages { get; } = [];
        public Task<BillingNotificationSendResult> SendAsync(BillingNotificationMessage message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(new BillingNotificationSendResult(BillingNotificationSendDisposition.Sent));
        }
    }

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}

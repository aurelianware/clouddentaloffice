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

    [Fact]
    public async Task Due_balance_queue_skips_already_notified_statements_before_batch_limit()
    {
        var now = DateTime.UtcNow;
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        _db.PatientStatements.AddRange(
            new PatientStatement
            {
                StatementId = first, TenantId = "tenant-a", PatientAccountId = _accountId,
                StatementDate = now.AddDays(-7), DueDate = now.AddDays(-1), Status = PatientStatementStatus.Sent,
                AmountDue = 25m, Currency = "USD", LedgerThroughDate = now.AddDays(-7), CreatedAt = now.AddDays(-7),
                CreatedBy = "test", StatusUpdatedAt = now.AddDays(-7)
            },
            new PatientStatement
            {
                StatementId = second, TenantId = "tenant-a", PatientAccountId = _accountId,
                StatementDate = now.AddDays(-6), DueDate = now, Status = PatientStatementStatus.Sent,
                AmountDue = 35m, Currency = "USD", LedgerThroughDate = now.AddDays(-6), CreatedAt = now.AddDays(-6),
                CreatedBy = "test", StatusUpdatedAt = now.AddDays(-6)
            });
        _db.PatientBillingNotifications.Add(new PatientBillingNotification
        {
            TenantId = "tenant-a", PatientAccountId = _accountId,
            NotificationType = PatientBillingNotificationType.BalanceDue, SourceType = "statement",
            SourceId = first.ToString("N"), RecipientEmail = "patient@example.test", PracticeName = "Example Dental",
            Status = PatientBillingNotificationStatus.Sent, ScheduledAt = now.AddDays(-1), SentAt = now.AddDays(-1),
            CreatedAt = now.AddDays(-1), UpdatedAt = now.AddDays(-1)
        });
        await _db.SaveChangesAsync();

        var options = Options.Create(new PatientBillingNotificationOptions { Enabled = true, BatchSize = 1 });
        var service = new PatientBillingNotificationService(_db, TimeProvider.System, options);
        var sender = new CaptureSender();
        var dispatcher = new PatientBillingNotificationDispatcher(_db, service, sender, options,
            TimeProvider.System, NullLogger<PatientBillingNotificationDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatchAsync());
        Assert.Single(sender.Messages);
        Assert.Contains(await _db.PatientBillingNotifications.IgnoreQueryFilters().ToListAsync(),
            x => x.SourceId == second.ToString("N") && x.NotificationType == PatientBillingNotificationType.BalanceDue);
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

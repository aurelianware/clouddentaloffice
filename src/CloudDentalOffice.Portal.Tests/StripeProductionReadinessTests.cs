using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripeProductionReadinessTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;

    public StripeProductionReadinessTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new Tenant("tenant-a"));
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Ready_status_requires_connect_webhook_and_clean_reconciliation()
    {
        var now = DateTime.UtcNow;
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(),
            TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe, Enabled = true,
            Environment = PaymentProcessorEnvironment.Sandbox, ConnectedMerchantReference = "acct_test",
            ChargesEnabled = true, PayoutsEnabled = true, LastReconciliationAt = now,
            LastReconciliationStatusCode = "clean", CreatedAt = now, UpdatedAt = now });
        _db.PaymentProcessorEvents.Add(new PaymentProcessorEvent { Id = Guid.NewGuid(), TenantId = "tenant-a",
            Processor = PaymentProcessorProvider.Stripe, ExternalEventId = "evt_opaque",
            Status = PaymentProcessorEventStatus.Processed, CreatedAt = now, ProcessedAt = now });
        await _db.SaveChangesAsync();
        var service = Service(new StripeInboxStatus(0, 0, 4, 0, null, true));

        var result = await service.GetAsync("tenant-a");

        Assert.True(result.PilotReady);
        Assert.True(result.WebhookHealthy);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task Failed_or_unavailable_inbox_blocks_pilot_and_tenant_is_enforced()
    {
        var service = Service(new StripeInboxStatus(2, 1, 0, 3, DateTime.UtcNow, true));
        var result = await service.GetAsync("tenant-a");
        Assert.False(result.PilotReady);
        Assert.Equal(3, result.PendingInboxCount);
        Assert.Equal(3, result.FailedInboxCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync("tenant-b"));
    }

    private StripeProductionReadinessService Service(StripeInboxStatus status) => new(_db,
        new Tenant("tenant-a"), new Inbox(status), Options.Create(new StripeReadinessOptions()), TimeProvider.System);
    private sealed class Inbox(StripeInboxStatus status) : IStripeInboxStatusClient
    {
        public Task<StripeInboxStatus> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(status);
    }
    private sealed class Tenant(string id) : ITenantProvider
    {
        public string TenantId => id;
        public ClaimsPrincipal? User => null;
    }
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }
}

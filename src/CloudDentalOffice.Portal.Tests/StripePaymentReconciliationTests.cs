using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Tests;

public sealed class StripePaymentReconciliationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTenant _tenant = new("tenant-a");
    private readonly CloudDentalDbContext _db;
    private readonly FakeStripe _stripe = new();

    public StripePaymentReconciliationTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        _db.Patients.Add(new Patient { PatientId = 1, TenantId = "tenant-a", FirstName = "Test",
            LastName = "Patient", DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" });
        var account = new PatientAccount { Id = Guid.NewGuid(), TenantId = "tenant-a", PatientId = 1,
            CreatedAt = now, UpdatedAt = now };
        _db.PatientAccounts.Add(account);
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(),
            TenantId = "tenant-a", Provider = PaymentProcessorProvider.Stripe, Enabled = true,
            Environment = PaymentProcessorEnvironment.Sandbox, ConnectedMerchantReference = "acct_test",
            CredentialReference = "secret", CreatedAt = now, UpdatedAt = now });
        _db.PatientPayments.Add(new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = "tenant-a",
            PatientAccountId = account.Id, Amount = 50m, Currency = "USD", PaymentDate = now,
            Method = PatientPaymentMethod.Card, Processor = PaymentProcessorProvider.Stripe,
            ExternalPaymentId = "pi_local", InternalPaymentReference = "pay_local", Status = PaymentStatus.Succeeded,
            CreatedAt = now, UpdatedAt = now });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Reconciliation_reports_missing_unknown_and_amount_mismatch_without_mutation()
    {
        _stripe.Payments =
        [
            new("pi_local", 5100, "USD", "succeeded"),
            new("pi_unknown", 1000, "USD", "succeeded")
        ];
        var result = await Service().ReconcileAsync("tenant-a", DateTime.UtcNow.AddDays(-1));
        Assert.Contains(result.Diagnostics, x => x.Type == PaymentReconciliationIssueType.AmountMismatch);
        Assert.Contains(result.Diagnostics, x => x.Type == PaymentReconciliationIssueType.UnknownStripePayment);
        Assert.All(result.Diagnostics, x => Assert.True(string.IsNullOrEmpty(x.SafeExternalReference) ||
            x.SafeExternalReference.Length <= 9));
        Assert.Equal(PaymentStatus.Succeeded,
            (await _db.PatientPayments.IgnoreQueryFilters().SingleAsync()).Status);
    }

    [Fact]
    public async Task Reconciliation_is_tenant_scoped()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service().ReconcileAsync("tenant-b", DateTime.UtcNow.AddDays(-1)));
    }

    private StripePaymentReconciliationService Service() => new(_db, _stripe, _tenant, TimeProvider.System);
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FixedTenant(string tenant) : ITenantProvider
    { public string TenantId => tenant; public ClaimsPrincipal? User => null; }
    private sealed class FakeStripe : IStripeApiClient
    {
        public IReadOnlyList<StripePaymentSnapshot> Payments { get; set; } = [];
        public IReadOnlyList<StripeRefundSnapshot> Refunds { get; set; } = [];
        public Task<IReadOnlyList<StripePaymentSnapshot>> ListPaymentsAsync(PaymentProcessorConfiguration c,
            string a, DateTime s, CancellationToken ct = default) => Task.FromResult(Payments);
        public Task<IReadOnlyList<StripeRefundSnapshot>> ListRefundsAsync(PaymentProcessorConfiguration c,
            string a, DateTime s, CancellationToken ct = default) => Task.FromResult(Refunds);
        public Task<StripePaymentSnapshot?> GetPaymentAsync(PaymentProcessorConfiguration c, string a,
            string id, CancellationToken ct = default) => Task.FromResult(Payments.SingleOrDefault(x => x.Id == id));
        public Task<StripeRefundSnapshot?> GetRefundAsync(PaymentProcessorConfiguration c, string a,
            string id, CancellationToken ct = default) => Task.FromResult(Refunds.SingleOrDefault(x => x.Id == id));
        public Task<StripeAccountSnapshot> CreateConnectedAccountAsync(PaymentProcessorConfiguration c,
            string e, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StripeAccountSnapshot> GetConnectedAccountAsync(PaymentProcessorConfiguration c,
            string a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StripeOnboardingLink> CreateAccountLinkAsync(PaymentProcessorConfiguration c, string a,
            Uri r, Uri u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StripeCheckoutSessionSnapshot> CreateCheckoutSessionAsync(PaymentProcessorConfiguration c,
            string a, PaymentRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StripeRefundSnapshot> CreateRefundAsync(PaymentProcessorConfiguration c, string a,
            PaymentRefundRequest r, string p, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

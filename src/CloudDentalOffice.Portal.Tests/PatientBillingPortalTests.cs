using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CloudDentalOffice.Portal.Tests;

public sealed class PatientBillingPortalTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly FakeCheckout _checkout = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _statementId = Guid.NewGuid();

    public PatientBillingPortalTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new DefaultTenantProvider());
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        _db.Patients.AddRange(
            new Patient { PatientId = 101, TenantId = "tenant-a", FirstName = "Pat", LastName = "One",
                DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" },
            new Patient { PatientId = 202, TenantId = "tenant-a", FirstName = "Pat", LastName = "Two",
                DateOfBirth = new(1981, 1, 1), Gender = "U", Status = "Active" });
        _db.PatientPortalIdentities.Add(new PatientPortalIdentity { Id = Guid.NewGuid(), TenantId = "tenant-a",
            PatientId = 101, Issuer = "https://issuer.test", Subject = "subject-101", IsActive = true,
            CreatedAt = now, UpdatedAt = now });
        _db.PatientAccounts.Add(new PatientAccount { Id = _accountId, TenantId = "tenant-a", PatientId = 101,
            Status = PatientAccountStatus.Active, CreatedAt = now, UpdatedAt = now });
        _db.PatientLedgerEntries.AddRange(
            Ledger(PatientLedgerEntryType.Charge, 500m, "charge"),
            Ledger(PatientLedgerEntryType.InsurancePayment, 200m, "insurance"),
            Ledger(PatientLedgerEntryType.Credit, 25m, "credit"));
        _db.PatientStatements.Add(new PatientStatement { StatementId = _statementId, TenantId = "tenant-a",
            PatientAccountId = _accountId, StatementDate = now, DueDate = now.AddDays(30),
            Status = PatientStatementStatus.Sent, BalanceForward = 100m, NewCharges = 400m,
            InsurancePayments = 200m, Adjustments = 25m, AmountDue = 275m, Currency = "USD",
            LedgerThroughDate = now, CreatedAt = now, CreatedBy = "test", StatusUpdatedAt = now });
        _db.PatientPayments.AddRange(Payment(PaymentStatus.Succeeded, 50m, "pay_success"),
            Payment(PaymentStatus.Failed, 25m, "pay_failed"));
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(), TenantId = "tenant-a",
            Provider = PaymentProcessorProvider.Stripe, Enabled = true, Environment = PaymentProcessorEnvironment.Sandbox,
            CredentialReference = "secret", ConnectedMerchantReference = "acct_test",
            OnboardingStatus = PaymentProcessorOnboardingStatus.Enabled, ChargesEnabled = true, PayoutsEnabled = true,
            CreatedAt = now, UpdatedAt = now });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Authenticated_linked_patient_sees_balance_statements_and_payment_history()
    {
        var result = await Service(partial: true).GetAsync(Principal());
        Assert.Equal(_accountId, result.PatientAccountId);
        Assert.Equal(275m, result.CurrentBalance);
        Assert.Equal(25m, result.Credits);
        Assert.Equal(200m, result.InsurancePayments);
        Assert.Single(result.Statements);
        Assert.Equal(2, result.Payments.Count);
        Assert.Contains(result.Payments, x => x.Status == PaymentStatus.Succeeded);
        Assert.Contains(result.Payments, x => x.Status == PaymentStatus.Failed);
        Assert.True(result.PartialPaymentsAllowed);
        Assert.True(result.StripeAvailable);
    }

    [Theory]
    [InlineData("subject-202", "tenant-a")]
    [InlineData("subject-101", "tenant-b")]
    public async Task Wrong_patient_or_tenant_cannot_view_account(string subject, string tenant)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().GetAsync(Principal(subject, tenant)));
    }

    [Fact]
    public async Task Non_patient_role_cannot_view_patient_billing()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service().GetAsync(Principal(role: "Staff")));
    }

    [Fact]
    public async Task Checkout_uses_identity_bound_account_and_server_service()
    {
        await Service().CreateCheckoutAsync(Principal(), PatientPaymentSelection.StatementBalance, _statementId, null);
        Assert.NotNull(_checkout.Last);
        Assert.Equal("tenant-a", _checkout.Last!.TenantId);
        Assert.Equal(_accountId, _checkout.Last.PatientAccountId);
        Assert.Equal(_statementId, _checkout.Last.StatementId);
    }

    [Fact]
    public async Task Partial_payment_setting_is_exposed_to_patient_experience()
    {
        Assert.False((await Service(partial: false).GetAsync(Principal())).PartialPaymentsAllowed);
    }

    private PatientBillingPortalService Service(bool partial = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Payments:Checkout:AllowPartialPayments"] = partial.ToString() }).Build();
        return new(_db, _checkout, config);
    }

    private static ClaimsPrincipal Principal(string subject = "subject-101", string tenant = "tenant-a", string role = "Patient") =>
        new(new ClaimsIdentity(new[] { new System.Security.Claims.Claim("iss", "https://issuer.test"),
            new System.Security.Claims.Claim("sub", subject), new System.Security.Claims.Claim("tenant_id", tenant),
            new System.Security.Claims.Claim(ClaimTypes.Role, role) }, "test"));
    private PatientLedgerEntry Ledger(PatientLedgerEntryType type, decimal amount, string source) => new()
    {
        LedgerEntryId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = _accountId, EntryType = type,
        Amount = amount, Currency = "USD", EffectiveDate = DateTime.UtcNow, SourceType = PatientLedgerSourceType.Procedure,
        SourceId = source, DescriptionCode = source, CreatedAt = DateTime.UtcNow, CreatedBy = "test"
    };
    private PatientPayment Payment(PaymentStatus status, decimal amount, string reference) => new()
    {
        PaymentId = Guid.NewGuid(), TenantId = "tenant-a", PatientAccountId = _accountId, Amount = amount,
        Currency = "USD", PaymentDate = DateTime.UtcNow, Method = PatientPaymentMethod.Card,
        Processor = PaymentProcessorProvider.Stripe, InternalPaymentReference = reference, Status = status,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
    };
    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FakeCheckout : IPatientBalanceCheckoutService
    {
        public PatientBalanceCheckoutRequest? Last;
        public Task<PatientBalanceCheckoutResult> CreateAsync(PatientBalanceCheckoutRequest request,
            CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new PatientBalanceCheckoutResult(Guid.NewGuid(), Guid.NewGuid(), "pay_opaque",
                new Money(50m), new Uri("https://checkout.stripe.test/session"), DateTime.UtcNow.AddMinutes(30)));
        }
    }
}

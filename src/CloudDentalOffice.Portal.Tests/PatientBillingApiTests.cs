using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// End-to-end IDOR coverage for the identity-bound patient billing surface. Two
/// patients share a tenant; the tests drive the same server components the
/// <c>/api/patient/billing</c> endpoints use (<see cref="PatientBillingPortalService"/>
/// over the real <see cref="PatientBalanceCheckoutService"/>) and prove Patient A
/// can only ever reach Patient A's account — even when supplying Patient B's
/// statement id — because the account is resolved from the authenticated identity.
/// </summary>
public sealed class PatientBillingApiTests : IDisposable
{
    private const string Tenant = "tenant-a";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FixedTenant _tenant = new(Tenant);
    private readonly CloudDentalDbContext _db;
    private readonly FakeCheckout _checkout = new();
    private Guid _accountA;
    private Guid _accountB;
    private Guid _statementA;
    private Guid _statementB;

    public PatientBillingApiTests()
    {
        _connection.Open();
        _db = new(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options, _tenant);
        _db.Database.EnsureCreated();
        var now = DateTime.UtcNow;
        _accountA = Seed(patientId: 101, subject: "subject-A", out _statementA);
        _accountB = Seed(patientId: 202, subject: "subject-B", out _statementB);
        _db.PaymentProcessorConfigurations.Add(new PaymentProcessorConfiguration { Id = Guid.NewGuid(), TenantId = Tenant,
            Provider = PaymentProcessorProvider.Stripe, Enabled = true, Environment = PaymentProcessorEnvironment.Sandbox,
            CredentialReference = "secret", ConnectedMerchantReference = "acct_practice",
            OnboardingStatus = PaymentProcessorOnboardingStatus.Enabled, ChargesEnabled = true, PayoutsEnabled = true,
            DetailsSubmitted = true, CreatedAt = now, UpdatedAt = now });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Patient_sees_only_their_own_account_statements_and_payments()
    {
        var snapshot = await Portal().GetAsync(PatientA());
        Assert.Equal(_accountA, snapshot.PatientAccountId);
        Assert.All(snapshot.Statements, s => Assert.NotEqual(_statementB, s.StatementId));
        Assert.Single(snapshot.Statements);
        Assert.Equal(_statementA, snapshot.Statements[0].StatementId);
    }

    [Fact]
    public async Task Patient_can_create_checkout_for_their_own_statement()
    {
        await Portal().CreateCheckoutAsync(PatientA(), PatientPaymentSelection.StatementBalance, _statementA, null);
        Assert.NotNull(_checkout.Last);
        Assert.Equal(_accountA, _checkout.Last!.PatientAccountId);
        Assert.Equal(_statementA, _checkout.Last.StatementId);
    }

    [Fact]
    public async Task Patient_cannot_create_checkout_for_another_patients_statement()
    {
        // Patient A supplies Patient B's statement id. The account is resolved from A's
        // identity, so the statement is not found for A's account -> KeyNotFound (404).
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Portal().CreateCheckoutAsync(PatientA(), PatientPaymentSelection.StatementBalance, _statementB, null));
        Assert.Null(_checkout.Last);
    }

    [Fact]
    public async Task Patient_from_another_tenant_cannot_resolve_an_account()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Portal().GetAsync(Principal("subject-A", "tenant-b", "Patient")));
    }

    [Fact]
    public async Task Non_patient_principal_cannot_use_patient_billing_service()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Portal().GetAsync(Principal("subject-A", Tenant, "BillingAdmin")));
    }

    private PatientBillingPortalService Portal()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        _checkout.Db = _db;
        var checkout = new PatientBalanceCheckoutService(_db, _checkout, _tenant,
            Options.Create(new PatientCheckoutOptions { PublicBaseUrl = "https://portal.example.test" }),
            TimeProvider.System);
        return new(_db, checkout, config);
    }

    private Guid Seed(int patientId, string subject, out Guid statementId)
    {
        var now = DateTime.UtcNow;
        var accountId = Guid.NewGuid();
        statementId = Guid.NewGuid();
        _db.Patients.Add(new Patient { PatientId = patientId, TenantId = Tenant, FirstName = "Pat",
            LastName = patientId.ToString(), DateOfBirth = new(1980, 1, 1), Gender = "U", Status = "Active" });
        _db.PatientPortalIdentities.Add(new PatientPortalIdentity { Id = Guid.NewGuid(), TenantId = Tenant,
            PatientId = patientId, Issuer = "https://issuer.test", Subject = subject, IsActive = true,
            CreatedAt = now, UpdatedAt = now });
        _db.PatientAccounts.Add(new PatientAccount { Id = accountId, TenantId = Tenant, PatientId = patientId,
            Status = PatientAccountStatus.Active, CreatedAt = now, UpdatedAt = now });
        _db.PatientLedgerEntries.Add(new PatientLedgerEntry { LedgerEntryId = Guid.NewGuid(), TenantId = Tenant,
            PatientAccountId = accountId, EntryType = PatientLedgerEntryType.Charge, Amount = 300m, Currency = "USD",
            EffectiveDate = now, SourceType = PatientLedgerSourceType.Procedure, SourceId = $"charge-{patientId}",
            DescriptionCode = "charge", CreatedAt = now, CreatedBy = "test" });
        _db.PatientStatements.Add(new PatientStatement { StatementId = statementId, TenantId = Tenant,
            PatientAccountId = accountId, StatementDate = now, DueDate = now.AddDays(30),
            Status = PatientStatementStatus.Sent, BalanceForward = 0m, NewCharges = 300m, AmountDue = 300m,
            Currency = "USD", LedgerThroughDate = now, CreatedAt = now, CreatedBy = "test", StatusUpdatedAt = now });
        return accountId;
    }

    private ClaimsPrincipal PatientA() => Principal("subject-A", Tenant, "Patient");
    private static ClaimsPrincipal Principal(string subject, string tenant, string role) =>
        new(new ClaimsIdentity(new[] { new System.Security.Claims.Claim("iss", "https://issuer.test"), new System.Security.Claims.Claim("sub", subject),
            new System.Security.Claims.Claim("tenant_id", tenant), new System.Security.Claims.Claim(ClaimTypes.Role, role) }, "test"));

    public void Dispose() { _db.Dispose(); _connection.Dispose(); }

    private sealed class FixedTenant(string tenant) : ITenantProvider
    { public string TenantId => tenant; public ClaimsPrincipal? User => null; }

    private sealed class FakeCheckout : IPaymentCheckoutService
    {
        public PaymentRequest? Last;
        public CloudDentalDbContext? Db;
        public Task<PaymentSession> CreateAsync(PaymentRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            // Mirror the real processor: persist the pending payment the checkout service reads back.
            Db!.PatientPayments.Add(new PatientPayment { PaymentId = Guid.NewGuid(), TenantId = request.TenantId,
                PatientAccountId = request.PatientAccountId, StatementId = request.StatementId,
                Amount = request.Amount.Amount, Currency = request.Amount.Currency, PaymentDate = DateTime.UtcNow,
                Method = request.Method, Processor = PaymentProcessorProvider.Stripe,
                InternalPaymentReference = request.InternalPaymentReference, Status = PaymentStatus.Pending,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            Db.SaveChanges();
            return Task.FromResult(new PaymentSession(request.InternalPaymentReference, "cs_test", null,
                new Uri("https://checkout.stripe.test/session"), null, DateTime.UtcNow.AddMinutes(30), PaymentStatus.Pending));
        }
    }
}

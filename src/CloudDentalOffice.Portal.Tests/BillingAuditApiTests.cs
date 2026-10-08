using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Claim = System.Security.Claims.Claim;
using System.Text.Encodings.Web;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>Billing HTTP mutations leave the same financial audit trail as the Billing screen.</summary>
public sealed class BillingAuditApiTests : IAsyncDisposable
{
    private const string Email = "billing@example.test";
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeStatements _statements = new();
    private readonly FakeCheckout _checkout = new();
    private WebApplication? _app;

    [Fact]
    public async Task Statement_lifecycle_changes_are_audited_with_the_staff_actor()
    {
        var client = await Client(BillingPermissions.Adjust);
        var id = Guid.NewGuid();

        await Ok(client.PostAsJsonAsync("/api/patient-statements",
            new CreateStatementRequest(101, DateTime.UtcNow, DateTime.UtcNow.AddDays(30), null, true)));
        await Ok(client.PostAsync($"/api/patient-statements/{id}/finalize", null));
        await Ok(client.PostAsJsonAsync($"/api/patient-statements/{id}/status",
            new TransitionStatementRequest(PatientStatementStatus.Sent)));
        await Ok(client.PostAsJsonAsync($"/api/patient-statements/{id}/void", new VoidStatementRequest("billing-error")));
        await Ok(client.PostAsJsonAsync($"/api/patient-statements/{id}/supersede",
            new SupersedeStatementRequest(Guid.Empty)));

        var audit = await Audit();
        Assert.Equal(["StatementGenerated", "StatementFinalized", "StatementStatusChanged", "StatementVoided",
            "StatementSuperseded"], audit.Select(x => x.Action));
        Assert.All(audit, x => Assert.Equal((Email, "tenant-a", nameof(PatientStatement)), (x.Actor, x.TenantId, x.EntityType)));
        Assert.Equal(["finalized", null, "Sent", "billing-error", Guid.Empty.ToString("N")], audit.Select(x => x.ReasonCode));
        Assert.Equal(id.ToString("N"), audit[3].EntityId);
    }

    [Fact]
    public async Task Staff_payment_link_from_the_API_is_audited()
    {
        var client = await Client(BillingPermissions.PostPayment);

        await Ok(client.PostAsJsonAsync($"/api/patient-accounts/{Guid.NewGuid()}/checkout",
            new PatientCheckoutApiRequest(PatientPaymentSelection.FullBalance, null, null)));

        var entry = Assert.Single(await Audit());
        Assert.Equal(("PaymentLinkCreated", nameof(PatientPaymentAttempt), _checkout.AttemptId.ToString(), Email),
            (entry.Action, entry.EntityType, entry.EntityId, entry.Actor));
    }

    [Fact]
    public async Task Caller_without_an_identity_to_audit_is_refused_before_anything_changes()
    {
        var client = await Client(BillingPermissions.Adjust, email: null);

        var response = await client.PostAsJsonAsync($"/api/patient-statements/{Guid.NewGuid()}/void",
            new VoidStatementRequest("billing-error"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _statements.Calls);
        Assert.Empty(await Audit());
    }

    [Fact]
    public async Task Failed_statement_change_writes_no_audit_entry()
    {
        var client = await Client(BillingPermissions.Adjust);
        _statements.Failure = new InvalidOperationException("Only a draft statement can be finalized.");

        await Assert.ThrowsAnyAsync<Exception>(() => client.PostAsync($"/api/patient-statements/{Guid.NewGuid()}/finalize", null));

        Assert.Empty(await Audit());
    }

    private async Task<HttpClient> Client(string permission, string? email = Email)
    {
        _connection.Open();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<ITenantProvider>(new FixedTenant("tenant-a"));
        builder.Services.AddDbContext<CloudDentalDbContext>(options => options.UseSqlite(_connection));
        builder.Services.AddSingleton<IPatientStatementService>(_statements);
        builder.Services.AddSingleton<IPatientBalanceCheckoutService>(_checkout);
        builder.Services.AddSingleton(Moq.Mock.Of<IPatientAccountService>());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAuthentication("Test").AddScheme<TestUserOptions, TestUser>("Test", options =>
        {
            options.Claims = [new("TenantId", "tenant-a"), new(BillingPermissions.ClaimType, permission),
                .. email is null ? Array.Empty<Claim>() : [new Claim(ClaimTypes.Email, email)]];
        });
        builder.Services.AddBillingAuthorization();
        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapPatientStatementApi();
        _app.MapPatientAccountApi();
        await _app.StartAsync();
        using (var scope = _app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>().Database.EnsureCreatedAsync();
        return _app.GetTestClient();
    }

    private async Task<List<FinancialAuditEvent>> Audit()
    {
        if (_app is null) return [];
        using var scope = _app.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CloudDentalDbContext>().FinancialAuditEvents
            .IgnoreQueryFilters().ToListAsync()).OrderBy(x => x.CreatedAt).ToList();
    }

    private static async Task Ok(Task<HttpResponseMessage> request)
    {
        var response = await request;
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class FixedTenant(string tenant) : ITenantProvider
    { public string TenantId => tenant; public ClaimsPrincipal? User => null; }

    private sealed class TestUserOptions : AuthenticationSchemeOptions { public List<Claim> Claims { get; set; } = []; }
    private sealed class TestUser(IOptionsMonitor<TestUserOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<TestUserOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(Options.Claims, "Test")), "Test")));
    }

    private sealed class FakeStatements : IPatientStatementService
    {
        public int Calls; public Exception? Failure;
        private Task<PatientStatement> Change(Guid id)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new PatientStatement { StatementId = id, TenantId = "tenant-a", Currency = "USD" });
        }
        public Task<PatientStatementPreview> PreviewAsync(string tenantId, int patientId, DateTime statementDate,
            DateTime dueDate, DateTime ledgerThroughDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PatientStatement> CreateAsync(string tenantId, int patientId, DateTime statementDate, DateTime dueDate,
            DateTime ledgerThroughDate, bool finalize, string createdBy, CancellationToken cancellationToken = default) =>
            Change(Guid.NewGuid());
        public Task<PatientStatement> FinalizeAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default) =>
            Change(statementId);
        public Task<PatientStatement> TransitionAsync(string tenantId, Guid statementId, PatientStatementStatus status,
            CancellationToken cancellationToken = default) => Change(statementId);
        public Task<IReadOnlyList<PatientStatement>> ListAsync(string tenantId, int? patientId = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PatientStatement?> GetAsync(string tenantId, Guid statementId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<PatientStatement> VoidAsync(string tenantId, Guid statementId, string reasonCode,
            CancellationToken cancellationToken = default) => Change(statementId);
        public Task<PatientStatement> SupersedeAsync(string tenantId, Guid statementId, Guid replacementStatementId,
            CancellationToken cancellationToken = default) => Change(statementId);
    }

    private sealed class FakeCheckout : IPatientBalanceCheckoutService
    {
        public readonly Guid AttemptId = Guid.NewGuid();
        public Task<PatientBalanceCheckoutResult> CreateAsync(PatientBalanceCheckoutRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new PatientBalanceCheckoutResult(AttemptId,
            Guid.NewGuid(), "pay_test", new Money(10m), new Uri("https://checkout.stripe.test/1"), null));
    }
}

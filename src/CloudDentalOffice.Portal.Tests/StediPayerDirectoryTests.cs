using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Stedi;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>Payer directory search and import. Synthetic directory data only.</summary>
public sealed class StediPayerDirectoryTests : IDisposable
{
    private const string TenantA = "practice-a";
    private const string TenantB = "practice-b";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly StediOptions _options = new();

    public StediPayerDirectoryTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    // ── Search ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Search_uses_the_directory_host_and_the_calling_practice_key()
    {
        var handler = new RecordingHandler(DirectoryJson);
        var credentials = new FixedCredentials();

        await Client(handler, credentials).SearchAsync(TenantA, new StediPayerSearchRequest { Query = "delta dental" });

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("payers.us.stedi.com", sent.Uri.Host);
        Assert.Equal("/2024-04-01/payers/search", sent.Uri.AbsolutePath);
        Assert.Contains("query=delta%20dental", sent.Uri.Query);
        Assert.Equal("key-for-" + TenantA, sent.Authorization);
        Assert.Equal(new[] { TenantA }, credentials.Requested.ToArray());
    }

    [Fact]
    public async Task Default_filters_keep_dental_payers_that_support_eligibility()
    {
        var results = await Client(new RecordingHandler(DirectoryJson)).SearchAsync(TenantA,
            new StediPayerSearchRequest { Query = "dental" });

        Assert.Equal(["86027", "52133", "77777", "62308"], results.Select(r => r.PrimaryPayerId));
        Assert.All(results, r => Assert.True(r.SupportsEligibility));
    }

    [Fact]
    public async Task State_filter_keeps_that_state_and_national_payers()
    {
        var results = await Client(new RecordingHandler(DirectoryJson)).SearchAsync(TenantA,
            new StediPayerSearchRequest { Query = "dental", State = "az" });

        Assert.Equal(["86027", "52133", "62308"], results.Select(r => r.PrimaryPayerId));
    }

    [Fact]
    public async Task Filters_can_be_turned_off()
    {
        var results = await Client(new RecordingHandler(DirectoryJson)).SearchAsync(TenantA,
            new StediPayerSearchRequest { Query = "dental", DentalOnly = false, EligibilityOnly = false });

        Assert.Equal(6, results.Count);
    }

    [Theory]
    [InlineData("d")]
    [InlineData(" ")]
    public async Task Too_short_a_query_is_refused_before_calling_the_directory(string query)
    {
        var handler = new RecordingHandler(DirectoryJson);

        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(handler).SearchAsync(TenantA, new StediPayerSearchRequest { Query = query }));

        Assert.Contains("2 to 80 characters", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Invalid_state_is_refused()
    {
        await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(new RecordingHandler(DirectoryJson)).SearchAsync(TenantA,
                new StediPayerSearchRequest { Query = "dental", State = "Arizona" }));
    }

    [Fact]
    public async Task Missing_tenant_is_refused_before_any_request()
    {
        var handler = new RecordingHandler(DirectoryJson);

        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(handler).SearchAsync(" ", new StediPayerSearchRequest { Query = "dental" }));

        var inner = Assert.IsType<StediCredentialUnavailableException>(error.InnerException);
        Assert.Equal(StediCredentialFailure.MissingTenant, inner.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Practice_without_a_connection_gets_a_staff_message_and_nothing_is_sent()
    {
        var handler = new RecordingHandler(DirectoryJson);
        var credentials = new FixedCredentials { Failure = StediCredentialFailure.NotConnected };

        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(handler, credentials).SearchAsync(TenantA, new StediPayerSearchRequest { Query = "dental" }));

        Assert.Contains("isn't connected yet", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejected_credential_is_reported_without_the_key()
    {
        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(new RecordingHandler("{}", HttpStatusCode.Unauthorized)).SearchAsync(TenantA,
                new StediPayerSearchRequest { Query = "dental" }));

        Assert.Contains("did not accept our credentials", error.Message);
        Assert.DoesNotContain("key-for-", error.Message);
    }

    [Theory]
    [InlineData("http://payers.us.stedi.com", "/2024-04-01/payers/search")]
    [InlineData("https://payers.us.stedi.com", "https://attacker.example/steal")]
    [InlineData("https://payers.us.stedi.com", "//attacker.example/steal")]
    public async Task Key_is_only_sent_to_the_configured_https_directory(string baseUrl, string path)
    {
        _options.PayersBaseUrl = baseUrl;
        _options.PayerSearchPath = path;
        var handler = new RecordingHandler(DirectoryJson);
        var credentials = new FixedCredentials();

        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Client(handler, credentials).SearchAsync(TenantA, new StediPayerSearchRequest { Query = "dental" }));

        Assert.Contains("misconfigured", error.Message);
        Assert.Empty(handler.Requests);
        Assert.Empty(credentials.Requested);
    }

    // ── Import ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Adding_a_payer_creates_an_active_plan_with_the_directory_payer_id()
    {
        await using var db = Db(TenantA);
        var service = Import(db, TenantA);

        var result = await service.AddAsync(Payer("86027", "Delta Dental Arizona"));

        Assert.True(result.Created);
        var plan = await db.InsurancePlans.SingleAsync();
        Assert.Equal(TenantA, plan.TenantId);
        Assert.Equal("86027", plan.PayerId);
        Assert.Equal("Delta Dental Arizona", plan.PayerName);
        Assert.True(plan.IsActive);
        Assert.False(plan.EdiEnabled);
        Assert.Contains("86027", await service.ExistingPayerIdsAsync());
    }

    [Fact]
    public async Task Adding_the_same_payer_twice_returns_the_existing_plan()
    {
        await using var db = Db(TenantA);
        var service = Import(db, TenantA);

        var first = await service.AddAsync(Payer("86027", "Delta Dental Arizona"));
        var second = await service.AddAsync(Payer("86027", "Delta Dental Arizona"));

        Assert.False(second.Created);
        Assert.Equal(first.Plan.InsurancePlanId, second.Plan.InsurancePlanId);
        Assert.Equal(1, await db.InsurancePlans.CountAsync());
    }

    [Fact]
    public async Task Each_practice_gets_its_own_plan_for_the_same_payer()
    {
        await using (var dbA = Db(TenantA))
            await Import(dbA, TenantA).AddAsync(Payer("86027", "Delta Dental Arizona"));

        await using var dbB = Db(TenantB);
        var result = await Import(dbB, TenantB).AddAsync(Payer("86027", "Delta Dental Arizona"));

        Assert.True(result.Created);
        Assert.Equal(TenantB, result.Plan.TenantId);
        Assert.Equal(2, await dbB.InsurancePlans.IgnoreQueryFilters().CountAsync());
        Assert.Single(await Import(dbB, TenantB).ExistingPayerIdsAsync());
    }

    [Fact]
    public async Task Payer_id_longer_than_the_plan_field_is_refused()
    {
        await using var db = Db(TenantA);

        var error = await Assert.ThrowsAsync<StediPayerDirectoryException>(() =>
            Import(db, TenantA).AddAsync(Payer("ABCDEFGHIJK", "Long ID Payer")));

        Assert.Contains("10 characters", error.Message);
        Assert.False(await db.InsurancePlans.AnyAsync());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // Six payers: four dental with eligibility (AZ, national, CA, multi-state incl. AZ via national),
    // one medical-only, one dental without eligibility support.
    private const string DirectoryJson = """
        {
          "items": [
            { "score": 9, "payer": { "stediId": "S1", "primaryPayerId": "86027", "displayName": "Delta Dental Arizona",
              "coverageTypes": ["dental"], "operatingStates": ["AZ"], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "SUPPORTED", "dentalClaimSubmission": "SUPPORTED" } } },
            { "score": 8, "payer": { "stediId": "S2", "primaryPayerId": "52133", "displayName": "UnitedHealthcare Dental",
              "coverageTypes": ["dental"], "operatingStates": ["NATIONAL"], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "SUPPORTED", "dentalClaimSubmission": "SUPPORTED" } } },
            { "score": 7, "payer": { "stediId": "S3", "primaryPayerId": "77777", "displayName": "Delta Dental of California",
              "coverageTypes": ["dental"], "operatingStates": ["CA"], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "ENROLLMENT_REQUIRED", "dentalClaimSubmission": "SUPPORTED" } } },
            { "score": 6, "payer": { "stediId": "S4", "primaryPayerId": "62308", "displayName": "Cigna",
              "coverageTypes": ["medical", "dental"], "operatingStates": [], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "SUPPORTED", "dentalClaimSubmission": "SUPPORTED" } } },
            { "score": 5, "payer": { "stediId": "S5", "primaryPayerId": "53589", "displayName": "AZ Blue",
              "coverageTypes": ["medical"], "operatingStates": ["AZ"], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "SUPPORTED" } } },
            { "score": 4, "payer": { "stediId": "S6", "primaryPayerId": "99001", "displayName": "Paper Dental Plan",
              "coverageTypes": ["dental"], "operatingStates": ["NATIONAL"], "aliases": [],
              "transactionSupport": { "eligibilityCheck": "NOT_SUPPORTED" } } }
          ]
        }
        """;

    private StediPayerSearchClient Client(RecordingHandler handler, FixedCredentials? credentials = null) =>
        new(new HttpClient(new StediCredentialHandler(credentials ?? new FixedCredentials()) { InnerHandler = handler }),
            Options.Create(_options), NullLogger<StediPayerSearchClient>.Instance);

    private CloudDentalDbContext Db(string tenant)
    {
        var db = new CloudDentalDbContext(new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options,
            new FixedTenantProvider(tenant));
        db.Database.EnsureCreated();
        return db;
    }

    private static PayerImportService Import(CloudDentalDbContext db, string tenant) =>
        new(db, new FixedTenantProvider(tenant), TimeProvider.System, NullLogger<PayerImportService>.Instance);

    private static StediPayerSummary Payer(string id, string name) =>
        new("S-" + id, id, name, [], ["dental"], ["AZ"], "SUPPORTED", "SUPPORTED");

    private sealed class FixedCredentials : IStediCredentialProvider
    {
        public ConcurrentQueue<string> Requested { get; } = new();
        public StediCredentialFailure? Failure { get; init; }

        public Task<StediCredential> GetAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            Requested.Enqueue(tenantId);
            if (Failure is { } failure)
                throw new StediCredentialUnavailableException(tenantId, failure, "unavailable");
            return Task.FromResult(new StediCredential(tenantId, ClearinghouseConnectionMode.Integrated, "key-for-" + tenantId));
        }

        public void Invalidate(string tenantId) { }
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpMethod Method, Uri Uri, string Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var auth = request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : "";
            Requests.Enqueue((request.Method, request.RequestUri!, auth));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
}

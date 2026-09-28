using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services;
using CloudDentalOffice.Portal.Services.Stedi;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// A scanned card's company and printed payer ID become the practice's clearinghouse
/// payer only when they point at exactly one. Synthetic payers only.
/// </summary>
public sealed class CardPayerResolverTests : IDisposable
{
    private const string Tenant = "tenant-a";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly CloudDentalDbContext _db;
    private readonly FakeDirectory _directory = new();
    private readonly CardPayerResolver _resolver;

    private static readonly StediPayerSummary DeltaArizona = Payer("DDAZ1", "86027", "Delta Dental of Arizona", ["AZ"]);
    private static readonly StediPayerSummary DeltaCalifornia = Payer("DDCA1", "77777", "Delta Dental of California", ["CA"]);
    private static readonly StediPayerSummary Cigna = Payer("CIG01", "62308", "Cigna Dental", []);

    public CardPayerResolverTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<CloudDentalDbContext>().UseSqlite(_connection).Options;
        _db = new CloudDentalDbContext(options, new FixedTenantProvider(Tenant));
        _db.Database.EnsureCreated();
        _db.InsurancePlans.AddRange(
            new InsurancePlan { InsurancePlanId = 11, TenantId = Tenant, PayerId = "52133", PayerName = "UnitedHealthcare Dental", IsActive = true },
            new InsurancePlan { InsurancePlanId = 12, TenantId = Tenant, PayerId = "BCAZ1", PayerName = "Blue Cross Arizona", IsActive = true });
        _db.SaveChanges();
        _resolver = new CardPayerResolver(_db, _directory, NullLogger<CardPayerResolver>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task A_printed_payer_id_picks_one_payer_among_same_named_companies()
    {
        _directory.Results["86027"] = [DeltaArizona];
        _directory.Results["DELTA DENTAL"] = [DeltaArizona, DeltaCalifornia];

        var result = await _resolver.ResolveAsync("DELTA DENTAL", "86027", "az");

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("86027", result.Suggested!.PayerId);
        Assert.True(result.Suggested.MatchesCardPayerId);
        Assert.Contains("Operates in AZ", result.Suggested.Why);
        Assert.Contains(_directory.Requests, r => r.Query == "DELTA DENTAL" && r.State == "AZ");
    }

    [Fact]
    public async Task A_name_alone_that_fits_several_payers_suggests_none()
    {
        _directory.Results["Delta Dental"] = [DeltaArizona, DeltaCalifornia];

        var result = await _resolver.ResolveAsync("Delta Dental", null, null);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Suggested);
    }

    [Fact]
    public async Task One_of_the_practices_payers_is_used_without_adding_anything()
    {
        var result = await _resolver.ResolveAsync("UnitedHealthcare", "52133", "AZ");

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(11, candidate.InsurancePlanId);
        Assert.Same(candidate, result.Suggested);
        Assert.Equal(11, (await _resolver.UseAsync(candidate)).InsurancePlanId);
        Assert.Empty(_directory.Added);
    }

    [Fact]
    public async Task A_directory_payer_is_added_when_used()
    {
        _directory.Results["Cigna"] = [Cigna];

        var result = await _resolver.ResolveAsync("Cigna", null, "AZ");
        var plan = await _resolver.UseAsync(result.Suggested!);

        Assert.Equal("62308", plan.PayerId);
        Assert.Same(Cigna, Assert.Single(_directory.Added));
    }

    [Fact]
    public async Task Place_names_and_common_words_do_not_make_a_match()
    {
        _directory.Results["Delta Dental of Arizona"] = [DeltaArizona];

        var result = await _resolver.ResolveAsync("Delta Dental of Arizona", null, "AZ");

        // "Blue Cross Arizona" shares only the state with the card.
        Assert.DoesNotContain(result.Candidates, c => c.InsurancePlanId == 12);
        Assert.Equal("86027", result.Suggested!.PayerId);
        Assert.Equal(["delta"], CardPayerResolver.Significant("Delta Dental Insurance Company of Arizona"));
    }

    [Fact]
    public async Task Without_the_directory_the_practices_payers_still_match()
    {
        _directory.Fail = true;

        var result = await _resolver.ResolveAsync("UnitedHealthcare Dental", null, "AZ");

        Assert.Equal(11, Assert.Single(result.Candidates).InsurancePlanId);
        Assert.Contains("couldn't be searched", result.Message);
    }

    [Fact]
    public async Task Nothing_readable_on_the_card_gives_no_candidates()
    {
        var result = await _resolver.ResolveAsync(" ", null, "Arizona");

        Assert.Empty(result.Candidates);
        Assert.Null(result.Suggested);
        Assert.Empty(_directory.Requests);
    }

    private static StediPayerSummary Payer(string stediId, string payerId, string name, IReadOnlyList<string> states) =>
        new(stediId, payerId, name, [], ["dental"], states, "SUPPORTED", "SUPPORTED");

    private sealed class FakeDirectory : IPayerImportService
    {
        public Dictionary<string, IReadOnlyList<StediPayerSummary>> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<StediPayerSearchRequest> Requests { get; } = [];
        public List<StediPayerSummary> Added { get; } = [];
        public bool Fail { get; set; }

        public Task<IReadOnlyList<StediPayerSummary>> SearchAsync(StediPayerSearchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Fail) throw new StediPayerDirectoryException("Payer search is temporarily unavailable. Try again in a minute.");
            return Task.FromResult(Results.GetValueOrDefault(request.Query) ?? []);
        }

        public Task<PayerImportResult> AddAsync(StediPayerSummary payer, CancellationToken cancellationToken = default)
        {
            Added.Add(payer);
            return Task.FromResult(new PayerImportResult(
                new InsurancePlan { InsurancePlanId = 99, TenantId = Tenant, PayerId = payer.PrimaryPayerId, PayerName = payer.DisplayName, IsActive = true },
                Created: true));
        }

        public Task<IReadOnlySet<string>> ExistingPayerIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    private sealed class FixedTenantProvider(string tenantId) : ITenantProvider
    {
        public string TenantId => tenantId;
        public ClaimsPrincipal? User => null;
    }
}

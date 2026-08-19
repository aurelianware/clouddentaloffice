using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Text;
using System.Security.Cryptography;

public sealed class SearchConsoleAnalyticsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly SchedulingDbContext db;
    private readonly TestClock clock = new(new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));

    public SearchConsoleAnalyticsTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    [Theory]
    [InlineData("https://www.3rdsetsmiles.com/special-offers/", "/special-offers/", true)]
    [InlineData("https://3rdsetsmiles.com/special-offers", "/special-offers/", true)]
    [InlineData("http://3rdsetsmiles.com/special-offers?utm_source=x#offer", "/special-offers/", true)]
    [InlineData("https://www.3rdsetsmiles.com/hero-demo/implant/", "/hero-demo/implant/", false)]
    [InlineData("https://old-demo.example/", "/", false)]
    public void Normalization_rolls_up_canonical_hosts_and_classifies_demo_urls(string url, string path, bool production)
    {
        var result = SearchLandingPageNormalizer.Normalize(url, "www.3rdsetsmiles.com");
        Assert.Equal(path, result.Path);
        Assert.Equal(production, result.IsProduction);
    }

    [Fact]
    public async Task Google_client_authenticates_and_sends_paginated_dimension_query()
    {
        var handler = new GoogleHandler(HttpStatusCode.OK);
        var client = GoogleClient(handler);

        var result = await client.QueryAsync("tenant-a", "test", new("https://www.3rdsetsmiles.com/",
            new(2026, 8, 16), new(2026, 8, 16), StartRow: 25000, RowLimit: 500));

        var row = Assert.Single(result.Rows);
        Assert.Equal("emergency dentist tempe", row.Query);
        Assert.Contains("\"startRow\":25000", handler.QueryBody);
        Assert.Contains("\"dimensions\":[\"date\",\"query\",\"page\",\"device\"]", handler.QueryBody);
        Assert.StartsWith("Bearer ", handler.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "rate_limited", true)]
    [InlineData(HttpStatusCode.Forbidden, "permission_denied", false)]
    [InlineData(HttpStatusCode.BadRequest, "invalid_property_or_request", false)]
    public async Task Google_client_classifies_api_failures(HttpStatusCode status, string code, bool transient)
    {
        var error = await Assert.ThrowsAsync<SearchConsoleApiException>(() => GoogleClient(new GoogleHandler(status)).QueryAsync(
            "tenant-a", "test", new("https://www.3rdsetsmiles.com/", new(2026, 8, 16), new(2026, 8, 16))));
        Assert.Equal(code, error.Code);
        Assert.Equal(transient, error.IsTransient);
    }

    [Fact]
    public async Task Sync_paginates_and_reimport_is_idempotent()
    {
        await Integration("tenant-a");
        var date = new DateOnly(2026, 8, 16);
        var client = new FakeClient(
            [new(date, "emergency dentist tempe", "https://3rdsetsmiles.com/services/emergency-dentistry", "mobile", 4, 30, 7.5),
             new(date, "dentist near me", "https://www.3rdsetsmiles.com/", "desktop", 2, 20, 9)],
            [new(date, "dental implants tempe", "https://www.3rdsetsmiles.com/services/dental-implants/", "mobile", 1, 10, 12)]);
        var service = Sync(client, pageSize: 2);

        Assert.Equal(3, await service.SyncAsync("tenant-a", true));
        db.SearchConsoleIntegrations.Single().NextSyncAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        Assert.Equal(3, await service.SyncAsync("tenant-a", true));

        Assert.Equal(3, await db.SearchPerformanceDaily.CountAsync());
        Assert.Contains(client.Requests, x => x.StartRow == 2);
    }

    [Fact]
    public async Task Report_uses_weighted_metrics_and_aggregate_page_booking_join()
    {
        await Integration("tenant-a");
        var date = new DateOnly(2026, 8, 16);
        db.SearchPerformanceDaily.AddRange(
            Search("tenant-a", date, "emergency dentist", "/services/emergency-dentistry/", 4, 20, 5),
            Search("tenant-a", date, "emergency dental", "/services/emergency-dentistry/", 1, 80, 15),
            Search("tenant-b", date, "tenant b private query", "/services/emergency-dentistry/", 99, 100, 1),
            Search("tenant-a", date, "old demo", "/hero-demo/old/", 20, 20, 1, production: false));
        db.PatientAcquisitionEvents.AddRange(
            Event("tenant-a", "session-0000000001", AcquisitionEventType.BookingStarted),
            Event("tenant-a", "session-0000000001", AcquisitionEventType.BookingRequestSubmitted),
            Event("tenant-a", "session-0000000001", AcquisitionEventType.AppointmentScheduled),
            Event("tenant-b", "session-0000000002", AcquisitionEventType.BookingRequestSubmitted));
        await db.SaveChangesAsync();

        var report = await new SearchAcquisitionReportingService(db).GetAsync("tenant-a",
            new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(5, report.Summary.Clicks);
        Assert.Equal(100, report.Summary.Impressions);
        Assert.Equal(5m, report.Summary.CtrPercent);
        Assert.Equal(13m, report.Summary.AveragePosition);
        var page = Assert.Single(report.LandingPages);
        Assert.Equal(1, page.BookingStarts);
        Assert.Equal(1, page.BookingRequests);
        Assert.Equal(1, page.ScheduledAppointments);
        Assert.DoesNotContain(report.TopQueries, x => x.Query.Contains("private"));
        Assert.Contains("aggregate", report.AttributionDisclaimer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Permission_failure_keeps_existing_data_and_marks_integration_degraded()
    {
        await Integration("tenant-a");
        db.SearchPerformanceDaily.Add(Search("tenant-a", new(2026, 8, 15), "existing", "/", 1, 5, 2));
        await db.SaveChangesAsync();

        await Sync(new FakeClient(new SearchConsoleApiException("permission_denied", false))).SyncAsync("tenant-a", false);

        Assert.Single(db.SearchPerformanceDaily);
        var integration = db.SearchConsoleIntegrations.Single();
        Assert.Equal(SearchConsoleSyncStatus.Degraded, integration.SyncStatus);
        Assert.Equal("permission_denied", integration.LastError);
        Assert.Equal("credential-ref", integration.CredentialReference);
    }

    private SearchConsoleSyncService Sync(ISearchConsoleClient client, int pageSize = 100) => new(db, client,
        Options.Create(new SearchConsoleOptions { InitialBackfillDays = 1, RepairWindowDays = 1, PageSize = pageSize,
            MaxRowsPerDay = 10, MaxAttempts = 2 }), clock, NullLogger<SearchConsoleSyncService>.Instance);
    private static GoogleSearchConsoleClient GoogleClient(HttpMessageHandler handler)
    {
        using var rsa = RSA.Create(2048);
        var values = new Dictionary<string, string?>
        {
            ["SearchConsoleCredentials:test:ClientEmail"] = "service-account@example.test",
            ["SearchConsoleCredentials:test:PrivateKey"] = rsa.ExportRSAPrivateKeyPem()
        };
        return new(new HttpClient(handler), new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
    private async Task Integration(string tenant) { db.SearchConsoleIntegrations.Add(new() { TenantId = tenant, Enabled = true,
        PropertyUrl = "https://www.3rdsetsmiles.com/", CredentialReference = "credential-ref", CanonicalHost = "www.3rdsetsmiles.com",
        SyncStatus = SearchConsoleSyncStatus.Pending }); await db.SaveChangesAsync(); }
    private static SearchPerformanceDaily Search(string tenant, DateOnly date, string query, string path, long clicks, long impressions,
        double position, bool production = true) => new() { TenantId = tenant, Date = date, Query = query, PagePath = path,
        Device = "mobile", Clicks = clicks, Impressions = impressions, PositionSum = position * impressions,
        IsProduction = production, ImportedAt = DateTime.UtcNow, SourceProperty = "https://www.3rdsetsmiles.com/" };
    private static PatientAcquisitionEvent Event(string tenant, string session, AcquisitionEventType type) => new()
    { TenantId = tenant, EventId = Guid.NewGuid(), SessionId = session, EventType = type, Source = "google-organic",
        LandingPage = "/services/emergency-dentistry/", OccurredAt = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc) };

    public void Dispose() { db.Dispose(); connection.Dispose(); }
    private sealed class TestClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class FakeClient : ISearchConsoleClient
    {
        private readonly IReadOnlyList<IReadOnlyList<SearchConsoleRow>> pages;
        private readonly Exception? error;
        public List<SearchConsoleQuery> Requests { get; } = [];
        public FakeClient(params IReadOnlyList<SearchConsoleRow>[] pages) => this.pages = pages;
        public FakeClient(Exception error) { this.error = error; pages = []; }
        public Task<SearchConsoleQueryResult> QueryAsync(string tenantId, string credentialReference, SearchConsoleQuery request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request); if (error is not null) throw error;
            var page = request.StartRow == 0 ? 0 : 1;
            return Task.FromResult(new SearchConsoleQueryResult(page < pages.Count ? pages[page] : []));
        }
    }
    private sealed class GoogleHandler(HttpStatusCode queryStatus) : HttpMessageHandler
    {
        public string QueryBody { get; private set; } = "";
        public string Authorization { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"test-token\"}");
            QueryBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString() ?? "";
            return queryStatus == HttpStatusCode.OK
                ? Json(queryStatus, "{\"rows\":[{\"keys\":[\"2026-08-16\",\"emergency dentist tempe\",\"https://www.3rdsetsmiles.com/services/emergency-dentistry/\",\"mobile\"],\"clicks\":4,\"impressions\":30,\"position\":7.5}]}")
                : Json(queryStatus, "{\"error\":{}}");
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CloudDentalOffice.Contracts.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public enum SearchConsoleSyncStatus { Disabled, Pending, Syncing, Healthy, Degraded }

[Index(nameof(TenantId), IsUnique = true)]
[Index(nameof(Enabled), nameof(NextSyncAt))]
public sealed class SearchConsoleIntegration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    [MaxLength(512)] public string PropertyUrl { get; set; } = string.Empty;
    [MaxLength(256)] public string CredentialReference { get; set; } = string.Empty;
    [MaxLength(256)] public string? CanonicalHost { get; set; }
    public SearchConsoleSyncStatus SyncStatus { get; set; } = SearchConsoleSyncStatus.Disabled;
    public DateTime? LastSuccessfulSyncAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextSyncAt { get; set; }
    public DateOnly? LatestImportedDate { get; set; }
    [MaxLength(128)] public string? LastError { get; set; }
    public Guid? LockId { get; set; }
    public DateTime? LockedUntil { get; set; }
}

[Index(nameof(TenantId), nameof(Date), nameof(Query), nameof(PagePath), nameof(Device), IsUnique = true)]
[Index(nameof(TenantId), nameof(Date))]
[Index(nameof(TenantId), nameof(PagePath), nameof(Date))]
[Index(nameof(TenantId), nameof(Query), nameof(Date))]
[Index(nameof(TenantId), nameof(Device), nameof(Date))]
public sealed class SearchPerformanceDaily
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    [MaxLength(500)] public string Query { get; set; } = string.Empty;
    [MaxLength(300)] public string PagePath { get; set; } = "/";
    [MaxLength(20)] public string Device { get; set; } = "unknown";
    public bool IsProduction { get; set; } = true;
    public long Clicks { get; set; }
    public long Impressions { get; set; }
    public double PositionSum { get; set; }
    public DateTime ImportedAt { get; set; }
    [MaxLength(512)] public string SourceProperty { get; set; } = string.Empty;
}

public sealed class SearchConsoleOptions
{
    public const string SectionName = "SearchConsole";
    [Range(1, 30)] public int RepairWindowDays { get; set; } = 7;
    [Range(1, 480)] public int InitialBackfillDays { get; set; } = 90;
    [Range(100, 25000)] public int PageSize { get; set; } = 25000;
    [Range(100, 250000)] public int MaxRowsPerDay { get; set; } = 50000;
    [Range(1, 24)] public int SyncHourUtc { get; set; } = 6;
    [Range(1, 20)] public int MaxAttempts { get; set; } = 3;
    [Range(1, 300)] public int PollMinutes { get; set; } = 15;
    [Range(30, 3600)] public int LeaseSeconds { get; set; } = 600;
}

public sealed record SearchConsoleQuery(string PropertyUrl, DateOnly StartDate, DateOnly EndDate,
    int StartRow = 0, int RowLimit = 25000);
public sealed record SearchConsoleRow(DateOnly Date, string Query, string Page, string Device,
    long Clicks, long Impressions, double Position);
public sealed record SearchConsoleQueryResult(IReadOnlyList<SearchConsoleRow> Rows);

public interface ISearchConsoleClient
{
    Task<SearchConsoleQueryResult> QueryAsync(string tenantId, string credentialReference,
        SearchConsoleQuery request, CancellationToken cancellationToken = default);
}

public sealed class SearchConsoleApiException(string code, bool transient, HttpStatusCode? statusCode = null) : Exception(code)
{
    public string Code { get; } = code;
    public bool IsTransient { get; } = transient;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class GoogleSearchConsoleClient(HttpClient http, IConfiguration configuration) : ISearchConsoleClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SearchConsoleQueryResult> QueryAsync(string tenantId, string credentialReference,
        SearchConsoleQuery request, CancellationToken cancellationToken = default)
    {
        Validate(tenantId, credentialReference, request);
        var token = await AccessTokenAsync(credentialReference, cancellationToken);
        var url = $"https://searchconsole.googleapis.com/webmasters/v3/sites/{Uri.EscapeDataString(request.PropertyUrl)}/searchAnalytics/query";
        using var message = new HttpRequestMessage(HttpMethod.Post, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Content = JsonContent.Create(new
        {
            startDate = request.StartDate.ToString("yyyy-MM-dd"), endDate = request.EndDate.ToString("yyyy-MM-dd"),
            dimensions = new[] { "date", "query", "page", "device" }, rowLimit = request.RowLimit,
            startRow = request.StartRow, dataState = "final"
        });
        using var response = await http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GoogleResponse>(JsonOptions, cancellationToken) ?? new();
        return new(payload.Rows.Where(x => x.Keys.Count >= 4 && DateOnly.TryParse(x.Keys[0], out _)).Select(x =>
            new SearchConsoleRow(DateOnly.Parse(x.Keys[0]), x.Keys[1], x.Keys[2], x.Keys[3], x.Clicks, x.Impressions, x.Position)).ToArray());
    }

    private async Task<string> AccessTokenAsync(string reference, CancellationToken cancellationToken)
    {
        var section = configuration.GetSection($"SearchConsoleCredentials:{reference}");
        var clientEmail = section["ClientEmail"];
        var privateKey = section["PrivateKey"]?.Replace("\\n", "\n", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
            throw new SearchConsoleApiException("credentials_not_configured", false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail, scope = "https://www.googleapis.com/auth/webmasters.readonly",
            aud = "https://oauth2.googleapis.com/token", iat = now, exp = now + 3600
        }));
        using var rsa = RSA.Create();
        try { rsa.ImportFromPem(privateKey); }
        catch (CryptographicException) { throw new SearchConsoleApiException("invalid_private_key", false); }
        var input = $"{header}.{claims}";
        var assertion = $"{input}.{Base64Url(rsa.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion }), cancellationToken);
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken);
        return !string.IsNullOrWhiteSpace(token?.AccessToken) ? token.AccessToken : throw new SearchConsoleApiException("token_missing", false);
    }

    private static SearchConsoleApiException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new("authentication_failed", false, status),
        HttpStatusCode.Forbidden => new("permission_denied", false, status),
        HttpStatusCode.BadRequest => new("invalid_property_or_request", false, status),
        HttpStatusCode.TooManyRequests => new("rate_limited", true, status),
        _ when (int)status >= 500 => new("google_unavailable", true, status),
        _ => new("search_console_request_failed", false, status)
    };
    private static void Validate(string tenantId, string reference, SearchConsoleQuery request)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64) throw new ArgumentException("Tenant is invalid.");
        if (string.IsNullOrWhiteSpace(reference)) throw new SearchConsoleApiException("credential_reference_missing", false);
        if (!Uri.TryCreate(request.PropertyUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new SearchConsoleApiException("invalid_property", false);
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string? AccessToken);
    private sealed record GoogleRow(List<string> Keys, long Clicks, long Impressions, double Position);
    private sealed record GoogleResponse { public List<GoogleRow> Rows { get; init; } = []; }
}

public static class SearchLandingPageNormalizer
{
    public static (string Path, bool IsProduction) Normalize(string url, string? canonicalHost)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return ("/invalid-search-console-url/", false);
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (!path.StartsWith('/')) path = "/" + path;
        path = path.Length > 1 ? path.TrimEnd('/') + "/" : "/";
        var hostMatches = string.IsNullOrWhiteSpace(canonicalHost) || Host(uri.Host) == Host(canonicalHost);
        var production = hostMatches && !path.StartsWith("/hero-demo/", StringComparison.OrdinalIgnoreCase);
        return (path[..Math.Min(path.Length, 300)], production);
    }
    private static string Host(string value)
    {
        var host = value.Trim().TrimEnd('/').Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}

public interface ISearchConsoleSyncService
{
    Task<int> SyncAsync(string tenantId, bool backfill, CancellationToken cancellationToken = default);
    Task<int> SyncDueAsync(CancellationToken cancellationToken = default);
}

public sealed class SearchConsoleSyncService(SchedulingDbContext db, ISearchConsoleClient client,
    IOptions<SearchConsoleOptions> options, TimeProvider clock, ILogger<SearchConsoleSyncService> logger) : ISearchConsoleSyncService
{
    public async Task<int> SyncDueAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var tenants = await db.SearchConsoleIntegrations.AsNoTracking().Where(x => x.Enabled &&
            (!x.NextSyncAt.HasValue || x.NextSyncAt <= now) && (x.LockedUntil == null || x.LockedUntil <= now))
            .OrderBy(x => x.NextSyncAt).ThenBy(x => x.TenantId).Select(x => x.TenantId).Take(10).ToListAsync(cancellationToken);
        var rows = 0;
        foreach (var tenant in tenants) rows += await SyncAsync(tenant, false, cancellationToken);
        return rows;
    }

    public async Task<int> SyncAsync(string tenantId, bool backfill, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var lockId = Guid.NewGuid();
        var claimed = await db.SearchConsoleIntegrations.Where(x => x.TenantId == tenantId && x.Enabled &&
                (x.LockedUntil == null || x.LockedUntil <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockId, lockId)
                .SetProperty(x => x.LockedUntil, now.AddSeconds(opts.LeaseSeconds))
                .SetProperty(x => x.LastAttemptAt, now).SetProperty(x => x.SyncStatus, SearchConsoleSyncStatus.Syncing), cancellationToken);
        if (claimed != 1) return 0;
        var integration = await db.SearchConsoleIntegrations.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.LockId == lockId, cancellationToken);
        var end = DateOnly.FromDateTime(now).AddDays(-2);
        var start = backfill || integration.LatestImportedDate is null
            ? end.AddDays(-(opts.InitialBackfillDays - 1))
            : integration.LatestImportedDate.Value.AddDays(-(opts.RepairWindowDays - 1));
        var imported = 0;
        try
        {
            for (var date = start; date <= end; date = date.AddDays(1))
            {
                var offset = 0;
                var dayRows = new List<SearchConsoleRow>();
                while (offset < opts.MaxRowsPerDay)
                {
                    var size = Math.Min(opts.PageSize, opts.MaxRowsPerDay - offset);
                    var result = await QueryWithRetry(tenantId, integration, new(integration.PropertyUrl, date, date, offset, size), cancellationToken);
                    dayRows.AddRange(result.Rows);
                    imported += result.Rows.Count;
                    if (result.Rows.Count < size) break;
                    offset += result.Rows.Count;
                }
                await ReplaceDay(tenantId, integration, date, dayRows, now, cancellationToken);
            }
            var next = new DateTime(now.Year, now.Month, now.Day, opts.SyncHourUtc, 0, 0, DateTimeKind.Utc).AddDays(1);
            await db.SearchConsoleIntegrations.Where(x => x.Id == integration.Id && x.LockId == lockId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SyncStatus, SearchConsoleSyncStatus.Healthy).SetProperty(x => x.LastSuccessfulSyncAt, now)
                .SetProperty(x => x.LatestImportedDate, end).SetProperty(x => x.NextSyncAt, next)
                .SetProperty(x => x.LastError, (string?)null).SetProperty(x => x.LockId, (Guid?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null), cancellationToken);
            logger.LogInformation("Search Console sync completed for tenant {TenantId}, property {Property}, {DateFrom} to {DateTo}, {RowsImported} rows.",
                tenantId, integration.PropertyUrl, start, end, imported);
            db.ChangeTracker.Clear();
            return imported;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is SearchConsoleApiException api ? api.Code : ex.GetType().Name;
            reason = reason[..Math.Min(128, reason.Length)];
            await db.SearchConsoleIntegrations.Where(x => x.Id == integration.Id && x.LockId == lockId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SyncStatus, SearchConsoleSyncStatus.Degraded).SetProperty(x => x.LastError, reason)
                .SetProperty(x => x.NextSyncAt, now.AddHours(6)).SetProperty(x => x.LockId, (Guid?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null), cancellationToken);
            logger.LogWarning(ex, "Search Console sync failed for tenant {TenantId}, property {Property}, {DateFrom} to {DateTo}.",
                tenantId, integration.PropertyUrl, start, end);
            db.ChangeTracker.Clear();
            return 0;
        }
    }

    private async Task<SearchConsoleQueryResult> QueryWithRetry(string tenantId, SearchConsoleIntegration integration,
        SearchConsoleQuery request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await client.QueryAsync(tenantId, integration.CredentialReference, request, cancellationToken); }
            catch (SearchConsoleApiException ex) when (ex.IsTransient && attempt < options.Value.MaxAttempts)
            { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken); }
        }
    }

    private async Task ReplaceDay(string tenantId, SearchConsoleIntegration integration, DateOnly date,
        IReadOnlyList<SearchConsoleRow> rows,
        DateTime importedAt, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SearchPerformanceDaily.Where(x => x.TenantId == tenantId && x.Date == date).ExecuteDeleteAsync(cancellationToken);
        db.ChangeTracker.Clear();
        var normalizedRows = rows.GroupBy(source =>
        {
            var normalized = SearchLandingPageNormalizer.Normalize(source.Page, integration.CanonicalHost);
            var query = source.Query.Trim()[..Math.Min(source.Query.Trim().Length, 500)];
            var device = source.Device.Trim().ToLowerInvariant()[..Math.Min(source.Device.Trim().Length, 20)];
            return new { source.Date, Query = query, Page = normalized.Path, normalized.IsProduction, Device = device };
        }).Select(group => new SearchPerformanceDaily
        {
            TenantId = tenantId, Date = group.Key.Date, Query = group.Key.Query, PagePath = group.Key.Page,
            Device = group.Key.Device, IsProduction = group.Key.IsProduction, Clicks = group.Sum(x => x.Clicks),
            Impressions = group.Sum(x => x.Impressions), PositionSum = group.Sum(x => x.Position * x.Impressions),
            ImportedAt = importedAt, SourceProperty = integration.PropertyUrl
        });
        db.SearchPerformanceDaily.AddRange(normalizedRows);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class SearchConsoleSyncWorker(IServiceProvider services, IOptions<SearchConsoleOptions> options,
    ILogger<SearchConsoleSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.PollMinutes));
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISearchConsoleSyncService>().SyncDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Search Console durable sync scan failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public interface ISearchAcquisitionReportingService
{
    Task<SearchAcquisitionDashboard> GetAsync(string tenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

public sealed class SearchAcquisitionReportingService(SchedulingDbContext db) : ISearchAcquisitionReportingService
{
    public async Task<SearchAcquisitionDashboard> GetAsync(string tenantId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (to <= from || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a valid reporting range up to 366 days.");
        var fromDate = DateOnly.FromDateTime(from.UtcDateTime); var toDate = DateOnly.FromDateTime(to.UtcDateTime);
        var search = await db.SearchPerformanceDaily.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsProduction &&
            x.Date >= fromDate && x.Date < toDate).ToListAsync(cancellationToken);
        var acquisition = await db.PatientAcquisitionEvents.AsNoTracking().Where(x => x.TenantId == tenantId &&
            x.OccurredAt >= from.UtcDateTime && x.OccurredAt < to.UtcDateTime && x.LandingPage != null)
            .Select(x => new { x.LandingPage, x.EventType, x.SessionId }).ToListAsync(cancellationToken);
        var integration = await db.SearchConsoleIntegrations.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        var summary = Summary(search);
        var pages = search.GroupBy(x => x.PagePath).Select(g =>
        {
            var events = acquisition.Where(x => AcquisitionVocabulary.Path(x.LandingPage) == g.Key).ToList();
            long Count(AcquisitionEventType type) => events.Where(x => x.EventType == type).Select(x => x.SessionId).Distinct().LongCount();
            var clicks = g.Sum(x => x.Clicks); var requests = Count(AcquisitionEventType.BookingRequestSubmitted);
            var metrics = Summary(g);
            return new SearchLandingPagePerformance(g.Key, true, clicks, metrics.Impressions, metrics.CtrPercent,
                metrics.AveragePosition, Count(AcquisitionEventType.BookingStarted), requests,
                Count(AcquisitionEventType.AppointmentScheduled), clicks > 0 ? Math.Round(requests * 100m / clicks, 1) : null);
        }).OrderByDescending(x => x.Clicks).ThenByDescending(x => x.Impressions).ToArray();
        return new()
        {
            From = from, To = to, Summary = summary,
            Daily = search.GroupBy(x => x.Date).OrderBy(x => x.Key).Select(x => new SearchDailyTotal(x.Key, x.Sum(y => y.Clicks), x.Sum(y => y.Impressions))).ToArray(),
            TopQueries = search.GroupBy(x => x.Query).Select(x => Row(x.Key, x)).OrderByDescending(x => x.Clicks).ThenByDescending(x => x.Impressions).Take(100).ToArray(),
            LandingPages = pages,
            QueryPages = search.GroupBy(x => new { x.Query, x.PagePath }).Select(x => QueryPage(x.Key.Query, x.Key.PagePath, x)).OrderByDescending(x => x.Clicks).Take(200).ToArray(),
            Devices = search.GroupBy(x => x.Device).Select(x => Device(x.Key, x)).OrderByDescending(x => x.Clicks).ToArray(),
            Status = new(integration is not null, integration?.Enabled ?? false, integration?.PropertyUrl,
                integration?.SyncStatus.ToString() ?? "NotConfigured", AsOffset(integration?.LastSuccessfulSyncAt),
                integration?.LatestImportedDate, integration?.LastError)
        };
    }

    private static SearchPerformanceSummary Summary(IEnumerable<SearchPerformanceDaily> rows)
    {
        var data = rows.ToArray(); var clicks = data.Sum(x => x.Clicks); var impressions = data.Sum(x => x.Impressions);
        return new(clicks, impressions, impressions > 0 ? Math.Round(clicks * 100m / impressions, 2) : 0,
            impressions > 0 ? Math.Round((decimal)(data.Sum(x => x.PositionSum) / impressions), 1) : 0);
    }
    private static SearchQueryPerformance Row(string query, IEnumerable<SearchPerformanceDaily> rows)
    { var m = Summary(rows); return new(query, m.Clicks, m.Impressions, m.CtrPercent, m.AveragePosition); }
    private static SearchQueryPagePerformance QueryPage(string query, string page, IEnumerable<SearchPerformanceDaily> rows)
    { var m = Summary(rows); return new(query, page, m.Clicks, m.Impressions, m.CtrPercent, m.AveragePosition); }
    private static SearchDevicePerformance Device(string device, IEnumerable<SearchPerformanceDaily> rows)
    { var m = Summary(rows); return new(device, m.Clicks, m.Impressions, m.CtrPercent, m.AveragePosition); }
    private static DateTimeOffset? AsOffset(DateTime? value) => value.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)) : null;
}

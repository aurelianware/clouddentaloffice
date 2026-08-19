using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public enum IntegrationInboxStatus { Received, Publishing, Published, Failed }

[Index(nameof(TenantId), nameof(Channel), nameof(ExternalEventId), IsUnique = true)]
[Index(nameof(Status), nameof(NextAttemptAt))]
public sealed class IntegrationInboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(40)] public string Channel { get; set; } = string.Empty;
    [MaxLength(256)] public string ExternalEventId { get; set; } = string.Empty;
    [MaxLength(128)] public string EventType { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; }
    public IntegrationInboxStatus Status { get; set; } = IntegrationInboxStatus.Received;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? PublishedAt { get; set; }
    [MaxLength(128)] public string? LastError { get; set; }
    public string Payload { get; set; } = string.Empty;
    public Guid? LockId { get; set; }
    public DateTime? LockedUntil { get; set; }
}

public sealed class IntakeDbContext(DbContextOptions<IntakeDbContext> options) : DbContext(options)
{
    public DbSet<IntegrationInboxMessage> IntegrationInboxMessages => Set<IntegrationInboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationInboxMessage>().Property(x => x.Payload).HasColumnType("text");
    }
}

public sealed class IntegrationInboxOptions
{
    public const string SectionName = "IntegrationInbox";
    [Range(1, 100)] public int BatchSize { get; set; } = 20;
    [Range(1, 60)] public int PollIntervalSeconds { get; set; } = 2;
    [Range(1, 100)] public int MaxAttempts { get; set; } = 8;
    [Range(1, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 3600)] public int InitialRetrySeconds { get; set; } = 5;
    [Range(1, 86400)] public int MaximumRetrySeconds { get; set; } = 300;
}

public sealed record IntegrationInboxPersistResult(Guid Id, bool Created);
public sealed record IntegrationInboxTenantStatus(
    int Received, int Publishing, int Published, int Failed, DateTime? OldestPendingAt);

public interface IIntegrationInbox
{
    Task<IntegrationInboxPersistResult> PersistAsync(string tenantId, string channel,
        string externalEventId, string eventType, IntegrationEvent payload,
        CancellationToken cancellationToken = default);
    Task<IntegrationInboxTenantStatus> GetStatusAsync(string tenantId, string? channel = null,
        CancellationToken cancellationToken = default);
    Task<bool> RequeueAsync(string tenantId, Guid id, CancellationToken cancellationToken = default);
}

public sealed class IntegrationInbox(IntakeDbContext db, TimeProvider timeProvider) : IIntegrationInbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IntegrationInboxPersistResult> PersistAsync(string tenantId, string channel,
        string externalEventId, string eventType, IntegrationEvent payload,
        CancellationToken cancellationToken = default)
    {
        Validate(tenantId, channel, externalEventId, eventType);
        var message = new IntegrationInboxMessage
        {
            TenantId = tenantId.Trim(), Channel = channel, ExternalEventId = externalEventId,
            EventType = eventType, ReceivedAt = timeProvider.GetUtcNow().UtcDateTime,
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions)
        };
        db.IntegrationInboxMessages.Add(message);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(message.Id, true);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var existing = await db.IntegrationInboxMessages.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == tenantId && x.Channel == channel && x.ExternalEventId == externalEventId,
                cancellationToken);
            if (existing is null) throw;
            return new(existing.Id, false);
        }
    }

    public async Task<IntegrationInboxTenantStatus> GetStatusAsync(string tenantId, string? channel = null,
        CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        var query = db.IntegrationInboxMessages.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(channel))
        {
            if (channel.Length > 40) throw new ArgumentException("Channel is invalid.");
            query = query.Where(x => x.Channel == channel);
        }
        var counts = await query.GroupBy(x => x.Status).Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var oldest = await query.Where(x => x.Status == IntegrationInboxStatus.Received ||
                x.Status == IntegrationInboxStatus.Publishing)
            .MinAsync(x => (DateTime?)x.ReceivedAt, cancellationToken);
        return new(counts.GetValueOrDefault(IntegrationInboxStatus.Received),
            counts.GetValueOrDefault(IntegrationInboxStatus.Publishing),
            counts.GetValueOrDefault(IntegrationInboxStatus.Published),
            counts.GetValueOrDefault(IntegrationInboxStatus.Failed), oldest);
    }

    public async Task<bool> RequeueAsync(string tenantId, Guid id, CancellationToken cancellationToken = default)
    {
        ValidateTenant(tenantId);
        if (id == Guid.Empty) return false;
        return await db.IntegrationInboxMessages.Where(x => x.TenantId == tenantId && x.Id == id &&
                x.Status == IntegrationInboxStatus.Failed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, IntegrationInboxStatus.Received)
                .SetProperty(x => x.AttemptCount, 0)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.NextAttemptAt, (DateTime?)null)
                .SetProperty(x => x.LockId, (Guid?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null), cancellationToken) == 1;
    }

    private static void Validate(string tenantId, string channel, string externalEventId, string eventType)
    {
        ValidateTenant(tenantId);
        if (string.IsNullOrWhiteSpace(channel) || channel.Length > 40) throw new ArgumentException("Channel is invalid.");
        if (string.IsNullOrWhiteSpace(externalEventId) || externalEventId.Length > 256) throw new ArgumentException("External event ID is invalid.");
        if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 128) throw new ArgumentException("Event type is invalid.");
    }

    internal static void ValidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64)
            throw new ArgumentException("Tenant ID is invalid.");
    }
}

public interface IIntegrationInboxDispatcher
{
    Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default);
}

public sealed class IntegrationInboxDispatcher(
    IntakeDbContext db, IEventPublisher publisher, ServiceBusOptions serviceBus,
    IOptions<IntegrationInboxOptions> options, TimeProvider timeProvider,
    ZocdocWebhookMetrics metrics, ILogger<IntegrationInboxDispatcher> logger)
    : IIntegrationInboxDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var oldest = await db.IntegrationInboxMessages.AsNoTracking().Where(x =>
                x.Status == IntegrationInboxStatus.Received || x.Status == IntegrationInboxStatus.Publishing)
            .MinAsync(x => (DateTime?)x.ReceivedAt, cancellationToken);
        if (oldest.HasValue) metrics.OldestPendingAge.Record(Math.Max(0, (now - oldest.Value).TotalSeconds));

        var candidates = await db.IntegrationInboxMessages.AsNoTracking()
            .Where(x => (x.Status == IntegrationInboxStatus.Received &&
                  x.AttemptCount < settings.MaxAttempts &&
                  (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) ||
                 (x.Status == IntegrationInboxStatus.Publishing && x.LockedUntil <= now))
            .OrderBy(x => x.ReceivedAt).Select(x => x.Id).Take(settings.BatchSize).ToListAsync(cancellationToken);
        var published = 0;
        foreach (var id in candidates)
        {
            var lockId = Guid.NewGuid();
            var claimed = await db.IntegrationInboxMessages.Where(x => x.Id == id &&
                    ((x.Status == IntegrationInboxStatus.Received && x.AttemptCount < settings.MaxAttempts &&
                      (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) ||
                     (x.Status == IntegrationInboxStatus.Publishing && x.LockedUntil <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, IntegrationInboxStatus.Publishing)
                    .SetProperty(x => x.LockId, lockId)
                    .SetProperty(x => x.LockedUntil, now.AddSeconds(settings.LeaseSeconds))
                    .SetProperty(x => x.LastAttemptAt, now)
                    .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1), cancellationToken);
            if (claimed != 1) continue;
            var message = await db.IntegrationInboxMessages.AsNoTracking().SingleAsync(x =>
                x.Id == id && x.LockId == lockId, cancellationToken);
            try
            {
                if (!serviceBus.IsConfigured) throw new InvalidOperationException("Message broker is unavailable.");
                await publisher.PublishAsync(Deserialize(message), cancellationToken);
                await db.IntegrationInboxMessages.Where(x => x.Id == id && x.LockId == lockId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, IntegrationInboxStatus.Published)
                        .SetProperty(x => x.PublishedAt, now)
                        .SetProperty(x => x.LastError, (string?)null)
                        .SetProperty(x => x.LockId, (Guid?)null)
                        .SetProperty(x => x.LockedUntil, (DateTime?)null), cancellationToken);
                metrics.PublishSuccesses.Add(1);
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var poison = message.AttemptCount >= settings.MaxAttempts;
                var retrySeconds = Math.Min(settings.MaximumRetrySeconds,
                    settings.InitialRetrySeconds * Math.Pow(2, Math.Max(0, message.AttemptCount - 1)));
                var failureKind = ex.GetType().Name;
                failureKind = failureKind[..Math.Min(failureKind.Length, 128)];
                await db.IntegrationInboxMessages.Where(x => x.Id == id && x.LockId == lockId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(x => x.Status, poison ? IntegrationInboxStatus.Failed : IntegrationInboxStatus.Received)
                        .SetProperty(x => x.NextAttemptAt, poison ? null : now.AddSeconds(retrySeconds))
                        .SetProperty(x => x.LastError, failureKind)
                        .SetProperty(x => x.LockId, (Guid?)null)
                        .SetProperty(x => x.LockedUntil, (DateTime?)null), cancellationToken);
                metrics.PublishFailures.Add(1);
                if (poison) metrics.PoisonMessages.Add(1); else metrics.Retries.Add(1);
                logger.LogWarning("Inbox publication failed for tenant {TenantId}, event {ExternalEventId}, " +
                    "attempt {AttemptCount}, failure {FailureKind}.", message.TenantId,
                    message.ExternalEventId, message.AttemptCount, failureKind);
            }
        }
        return published;
    }

    private static IntegrationEvent Deserialize(IntegrationInboxMessage message) => message.EventType switch
    {
        nameof(ZocdocAppointmentWebhookEvent) =>
            JsonSerializer.Deserialize<ZocdocAppointmentWebhookEvent>(message.Payload, JsonOptions)
            ?? throw new JsonException("Inbox payload is invalid."),
        nameof(StripePaymentWebhookEvent) =>
            JsonSerializer.Deserialize<StripePaymentWebhookEvent>(message.Payload, JsonOptions)
            ?? throw new JsonException("Inbox payload is invalid."),
        nameof(StripeRefundWebhookEvent) =>
            JsonSerializer.Deserialize<StripeRefundWebhookEvent>(message.Payload, JsonOptions)
            ?? throw new JsonException("Inbox payload is invalid."),
        _ => throw new JsonException("Inbox event type is unsupported.")
    };
}

public sealed class IntegrationInboxWorker(
    IServiceProvider services, IOptions<IntegrationInboxOptions> options,
    ILogger<IntegrationInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IIntegrationInboxDispatcher>()
                    .DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError("Integration inbox dispatch cycle failed with {FailureKind}.", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class IntegrationInboxAdminAuth
{
    public static string? ResolveTenant(HttpContext http, IConfiguration configuration)
    {
        var supplied = http.Request.Headers["X-CDO-Service-Key"].ToString();
        if (string.IsNullOrWhiteSpace(supplied)) return null;
        foreach (var client in configuration.GetSection("IntegrationInbox:AdminClients").GetChildren())
        {
            var expected = client["ApiKey"];
            var tenantId = client["TenantId"];
            if (!string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(tenantId) &&
                FixedEquals(supplied, expected)) return tenantId;
        }
        return null;
    }

    private static bool FixedEquals(string supplied, string expected)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

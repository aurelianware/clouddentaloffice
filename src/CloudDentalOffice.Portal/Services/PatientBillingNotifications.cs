using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class PatientBillingNotificationOptions
{
    public const string SectionName = "Payments:Notifications";
    public bool Enabled { get; set; }
    public string? PatientPortalBaseUrl { get; set; }
    [Range(1, 100)] public int BatchSize { get; set; } = 20;
    [Range(1, 60)] public int PollIntervalSeconds { get; set; } = 10;
    [Range(1, 20)] public int MaxAttempts { get; set; } = 4;
    [Range(1, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 86400)] public int InitialRetrySeconds { get; set; } = 60;
    [Range(1, 86400)] public int MaximumRetrySeconds { get; set; } = 3600;
}

public interface IPatientBillingNotificationService
{
    Task<bool> EnqueueAsync(string tenantId, Guid patientAccountId, PatientBillingNotificationType type,
        string sourceType, string sourceId, CancellationToken cancellationToken = default);
}

public sealed class PatientBillingNotificationService(CloudDentalDbContext db, TimeProvider clock,
    IOptions<PatientBillingNotificationOptions> options) : IPatientBillingNotificationService
{
    public async Task<bool> EnqueueAsync(string tenantId, Guid patientAccountId, PatientBillingNotificationType type,
        string sourceType, string sourceId, CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return false;
        Validate(tenantId, patientAccountId, sourceType, sourceId);
        if (await db.PatientBillingNotifications.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == tenantId && x.NotificationType == type && x.SourceType == sourceType &&
                x.SourceId == sourceId, cancellationToken)) return false;
        var recipient = await (from account in db.PatientAccounts.IgnoreQueryFilters()
                               join patient in db.Patients.IgnoreQueryFilters()
                                   on new { account.TenantId, account.PatientId } equals
                                   new { patient.TenantId, patient.PatientId }
                               where account.TenantId == tenantId && account.Id == patientAccountId
                               select patient.Email).SingleOrDefaultAsync(cancellationToken);
        var practice = await db.Tenants.AsNoTracking().Where(x => x.TenantId == tenantId)
            .Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? await db.Organizations.AsNoTracking().Where(x => x.TenantId == tenantId)
                .Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? "your dental practice";
        var now = clock.GetUtcNow().UtcDateTime;
        db.PatientBillingNotifications.Add(new PatientBillingNotification
        {
            TenantId = tenantId, PatientAccountId = patientAccountId, NotificationType = type,
            SourceType = sourceType.Trim(), SourceId = sourceId.Trim(), RecipientEmail = recipient?.Trim() ?? string.Empty,
            PracticeName = practice[..Math.Min(120, practice.Length)], ScheduledAt = now,
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static void Validate(string tenantId, Guid accountId, string sourceType, string sourceId)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || tenantId.Length > 64) throw new ArgumentException("Tenant is invalid.");
        if (accountId == Guid.Empty) throw new ArgumentException("Patient account is invalid.");
        if (string.IsNullOrWhiteSpace(sourceType) || sourceType.Trim().Length > 32)
            throw new ArgumentException("Notification source type is invalid.");
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Trim().Length > 128)
            throw new ArgumentException("Notification source ID is invalid.");
    }
}

public enum BillingNotificationSendDisposition { Sent, TransientFailure, PermanentFailure }
public sealed record BillingNotificationMessage(string Recipient, string Subject, string Body);
public sealed record BillingNotificationSendResult(BillingNotificationSendDisposition Disposition,
    string? FailureReason = null);
public interface IPatientBillingNotificationSender
{
    Task<BillingNotificationSendResult> SendAsync(BillingNotificationMessage message,
        CancellationToken cancellationToken = default);
}

public sealed class EmailPatientBillingNotificationSender(IOptions<ReviewEmailOptions> emailOptions,
    ILogger<EmailPatientBillingNotificationSender> logger) : IPatientBillingNotificationSender
{
    public async Task<BillingNotificationSendResult> SendAsync(BillingNotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        var settings = emailOptions.Value;
        if (settings.Mode.Equals("DevelopmentSink", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Development sink accepted a patient billing notification.");
            return new(BillingNotificationSendDisposition.Sent);
        }
        if (!settings.Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.Host) ||
            string.IsNullOrWhiteSpace(settings.FromAddress))
            return new(BillingNotificationSendDisposition.PermanentFailure, "email_transport_not_configured");
        try
        {
            using var mail = new MailMessage(settings.FromAddress, message.Recipient)
            { Subject = message.Subject, Body = message.Body, IsBodyHtml = false };
            using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl };
            if (!string.IsNullOrWhiteSpace(settings.Username))
                client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(mail, cancellationToken);
            return new(BillingNotificationSendDisposition.Sent);
        }
        catch (SmtpFailedRecipientException)
        { return new(BillingNotificationSendDisposition.PermanentFailure, "recipient_rejected"); }
        catch (FormatException)
        { return new(BillingNotificationSendDisposition.PermanentFailure, "invalid_email_configuration"); }
        catch (Exception ex) when (ex is SmtpException or IOException)
        { return new(BillingNotificationSendDisposition.TransientFailure, ex.GetType().Name); }
    }
}

public interface IPatientBillingNotificationDispatcher
{
    Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default);
}

public sealed class PatientBillingNotificationDispatcher(CloudDentalDbContext db,
    IPatientBillingNotificationService notifications, IPatientBillingNotificationSender sender,
    IOptions<PatientBillingNotificationOptions> options, TimeProvider clock,
    ILogger<PatientBillingNotificationDispatcher> logger) : IPatientBillingNotificationDispatcher
{
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Value.Enabled) return 0;
        await QueueDueBalanceNotifications(cancellationToken);
        db.ChangeTracker.Clear();
        var now = clock.GetUtcNow().UtcDateTime;
        var settings = options.Value;
        var ids = await db.PatientBillingNotifications.IgnoreQueryFilters().AsNoTracking().Where(x =>
                (x.Status == PatientBillingNotificationStatus.Scheduled && x.ScheduledAt <= now &&
                 (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) ||
                (x.Status == PatientBillingNotificationStatus.Sending && x.LockedUntil <= now))
            .OrderBy(x => x.ScheduledAt).Select(x => x.Id).Take(settings.BatchSize).ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var id in ids)
        {
            var lockId = Guid.NewGuid();
            var claimed = await db.PatientBillingNotifications.IgnoreQueryFilters().Where(x => x.Id == id &&
                    ((x.Status == PatientBillingNotificationStatus.Scheduled && x.AttemptCount < settings.MaxAttempts &&
                      (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) ||
                     (x.Status == PatientBillingNotificationStatus.Sending && x.LockedUntil <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, PatientBillingNotificationStatus.Sending)
                    .SetProperty(x => x.LockId, lockId).SetProperty(x => x.LockedUntil, now.AddSeconds(settings.LeaseSeconds))
                    .SetProperty(x => x.LastAttemptAt, now).SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (claimed != 1) continue;
            var row = await db.PatientBillingNotifications.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == id && x.LockId == lockId, cancellationToken);
            if (string.IsNullOrWhiteSpace(row.RecipientEmail) || !new EmailAddressAttribute().IsValid(row.RecipientEmail))
            {
                await Finish(id, lockId, PatientBillingNotificationStatus.Suppressed, "email_missing_or_invalid", now,
                    cancellationToken);
                continue;
            }
            var message = CreateMessage(row, settings.PatientPortalBaseUrl);
            var result = await SendSafely(message, cancellationToken);
            if (result.Disposition == BillingNotificationSendDisposition.Sent)
            {
                await db.PatientBillingNotifications.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, PatientBillingNotificationStatus.Sent)
                        .SetProperty(x => x.SentAt, now).SetProperty(x => x.FailureReason, (string?)null)
                        .SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntil, (DateTime?)null)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken);
                sent++;
            }
            else if (result.Disposition == BillingNotificationSendDisposition.PermanentFailure ||
                     row.AttemptCount >= settings.MaxAttempts)
                await Finish(id, lockId, PatientBillingNotificationStatus.Failed, SafeReason(result.FailureReason), now,
                    cancellationToken);
            else
            {
                var delay = Math.Min(settings.MaximumRetrySeconds,
                    settings.InitialRetrySeconds * Math.Pow(2, Math.Max(0, row.AttemptCount - 1)));
                await db.PatientBillingNotifications.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, PatientBillingNotificationStatus.Scheduled)
                        .SetProperty(x => x.NextAttemptAt, now.AddSeconds(delay))
                        .SetProperty(x => x.FailureReason, SafeReason(result.FailureReason))
                        .SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntil, (DateTime?)null)
                        .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            }
        }
        return sent;
    }

    private async Task QueueDueBalanceNotifications(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.PatientStatements.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.DueDate <= now && x.AmountDue > 0 &&
                (x.Status == PatientStatementStatus.Sent || x.Status == PatientStatementStatus.PartiallyPaid))
            .OrderBy(x => x.DueDate).ThenBy(x => x.StatementId)
            .Select(x => new { x.TenantId, x.PatientAccountId, x.StatementId })
            .ToListAsync(cancellationToken);
        var dueIds = due.Select(x => x.StatementId.ToString("N")).Distinct(StringComparer.Ordinal).ToList();
        var existing = dueIds.Count == 0
            ? new HashSet<(string TenantId, string SourceId)>()
            : (await db.PatientBillingNotifications.IgnoreQueryFilters().AsNoTracking().Where(x =>
                    x.NotificationType == PatientBillingNotificationType.BalanceDue &&
                    x.SourceType == "statement" && dueIds.Contains(x.SourceId))
                .Select(x => new { x.TenantId, x.SourceId }).ToListAsync(cancellationToken))
            .Select(x => (x.TenantId, x.SourceId))
            .ToHashSet();
        var queued = 0;
        foreach (var statement in due)
        {
            var sourceId = statement.StatementId.ToString("N");
            if (existing.Contains((statement.TenantId, sourceId))) continue;
            if (await notifications.EnqueueAsync(statement.TenantId, statement.PatientAccountId,
                    PatientBillingNotificationType.BalanceDue, "statement", sourceId, cancellationToken))
                queued++;
            if (queued >= options.Value.BatchSize) break;
        }
    }

    private static BillingNotificationMessage CreateMessage(PatientBillingNotification row, string? portalBaseUrl)
    {
        var practice = row.PracticeName;
        var text = row.NotificationType switch
        {
            PatientBillingNotificationType.NewStatement => $"You have a new statement available from {practice}.",
            PatientBillingNotificationType.BalanceDue => $"You have a balance due with {practice}.",
            PatientBillingNotificationType.PaymentReceived => $"{practice} received your payment.",
            PatientBillingNotificationType.PaymentFailed => $"A payment to {practice} was not completed.",
            _ => throw new InvalidOperationException("Unsupported billing notification type.")
        };
        var subject = row.NotificationType switch
        {
            PatientBillingNotificationType.PaymentReceived => $"Payment received by {practice}",
            PatientBillingNotificationType.PaymentFailed => $"Payment not completed for {practice}",
            _ => $"Billing update from {practice}"
        };
        if (Uri.TryCreate(portalBaseUrl, UriKind.Absolute, out var baseUri) && baseUri.Scheme == Uri.UriSchemeHttps)
            text += $" Sign in securely to view it: {new Uri(baseUri, "/patient/billing").AbsoluteUri}";
        else text += " Sign in securely to CloudDentalOffice to view it.";
        return new(row.RecipientEmail, subject, text);
    }

    private async Task<BillingNotificationSendResult> SendSafely(BillingNotificationMessage message,
        CancellationToken cancellationToken)
    {
        try { return await sender.SendAsync(message, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Patient billing notification delivery failed ({FailureKind}).", ex.GetType().Name);
            return new(BillingNotificationSendDisposition.TransientFailure, ex.GetType().Name);
        }
    }

    private Task<int> Finish(Guid id, Guid lockId, PatientBillingNotificationStatus status, string reason,
        DateTime now, CancellationToken cancellationToken) =>
        db.PatientBillingNotifications.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, status)
                .SetProperty(x => x.FailureReason, reason).SetProperty(x => x.LockId, (Guid?)null)
                .SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.UpdatedAt, now), cancellationToken);
    private static string SafeReason(string? value) => string.IsNullOrWhiteSpace(value) ? "delivery_failed" :
        value[..Math.Min(128, value.Length)];
}

public sealed class PatientBillingNotificationWorker(IServiceProvider services,
    IOptions<PatientBillingNotificationOptions> options, ILogger<PatientBillingNotificationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IPatientBillingNotificationDispatcher>()
                    .DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            { logger.LogError("Patient billing notification cycle failed ({FailureKind}).", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

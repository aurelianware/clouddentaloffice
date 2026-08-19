using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Mail;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed record ReviewOutreachEligibilityResult(bool Eligible, string Reason,
    Appointment? Appointment = null, Patient? Patient = null, ReviewOutreachSettings? Settings = null);

public interface IReviewOutreachEligibilityService
{
    Task<ReviewOutreachEligibilityResult> EvaluateAsync(string tenantId, int appointmentId,
        bool checkExisting = true, CancellationToken cancellationToken = default);
}

public sealed class ReviewOutreachEligibilityService(CloudDentalDbContext db) : IReviewOutreachEligibilityService
{
    public async Task<ReviewOutreachEligibilityResult> EvaluateAsync(string tenantId, int appointmentId,
        bool checkExisting = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return new(false, "invalid_tenant");
        var settings = await db.ReviewOutreachSettings.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken);
        if (settings is null || !settings.Enabled) return new(false, "disabled");
        if (!TryPublicHttpUrl(settings.ReviewLandingPageUrl) || !TryPublicHttpUrl(settings.GoogleReviewUrl))
            return new(false, "invalid_configuration");

        var appointment = await db.Appointments.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.AppointmentId == appointmentId, cancellationToken);
        if (appointment is null) return new(false, "appointment_missing");
        if (!string.Equals(appointment.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            return new(false, "appointment_not_completed");

        var patient = await db.Patients.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PatientId == appointment.PatientId, cancellationToken);
        if (patient is null) return new(false, "patient_missing");
        if (!string.Equals(patient.Status, "Active", StringComparison.OrdinalIgnoreCase))
            return new(false, "patient_inactive");
        if (string.IsNullOrWhiteSpace(patient.Email) || !new EmailAddressAttribute().IsValid(patient.Email))
            return new(false, "email_missing_or_invalid");
        if (checkExisting && await db.ReviewOutreaches.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == tenantId && x.AppointmentId == appointmentId && x.Campaign == "google-review", cancellationToken))
            return new(false, "duplicate");
        return new(true, "eligible", appointment, patient, settings);
    }

    private static bool TryPublicHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && string.IsNullOrEmpty(uri.UserInfo);
}

public interface IReviewOutreachScheduler
{
    Task<bool> ScheduleAsync(string tenantId, int appointmentId, CancellationToken cancellationToken = default);
}

public sealed class ReviewOutreachScheduler(CloudDentalDbContext db, IReviewOutreachEligibilityService eligibility,
    TimeProvider timeProvider, ILogger<ReviewOutreachScheduler> logger) : IReviewOutreachScheduler
{
    public async Task<bool> ScheduleAsync(string tenantId, int appointmentId, CancellationToken cancellationToken = default)
    {
        var result = await eligibility.EvaluateAsync(tenantId, appointmentId, true, cancellationToken);
        if (!result.Eligible)
        {
            logger.LogInformation("Review outreach was not scheduled for tenant {TenantId}: {Reason}.", tenantId, result.Reason);
            return false;
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.ReviewOutreaches.Add(new ReviewOutreach
        {
            TenantId = tenantId, AppointmentId = appointmentId, PatientId = result.Appointment!.PatientId,
            ScheduledAt = now.AddMinutes(result.Settings!.DelayMinutes), CreatedAt = now, UpdatedAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.ReviewOutreaches.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId &&
                x.AppointmentId == appointmentId && x.Campaign == "google-review", cancellationToken)) return false;
            throw;
        }
        logger.LogInformation("Review outreach scheduled for tenant {TenantId}, channel email.", tenantId);
        return true;
    }
}

public enum ReviewOutreachSendDisposition { Sent, TransientFailure, PermanentFailure }
public sealed record ReviewOutreachSendRequest(string Recipient, string PracticeName, Uri LandingPageUrl);
public sealed record ReviewOutreachSendResult(ReviewOutreachSendDisposition Disposition, string? FailureReason = null);
public interface IReviewOutreachSender
{
    ReviewOutreachChannel Channel { get; }
    Task<ReviewOutreachSendResult> SendAsync(ReviewOutreachSendRequest request, CancellationToken cancellationToken);
}

public sealed class ReviewEmailOptions
{
    public const string SectionName = "ReviewOutreach:Email";
    public string Mode { get; set; } = "Disabled";
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromAddress { get; set; }
}

public sealed class EmailReviewOutreachSender(IOptions<ReviewEmailOptions> options,
    ILogger<EmailReviewOutreachSender> logger) : IReviewOutreachSender
{
    public ReviewOutreachChannel Channel => ReviewOutreachChannel.Email;

    public async Task<ReviewOutreachSendResult> SendAsync(ReviewOutreachSendRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode.Equals("DevelopmentSink", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Development sink accepted a neutral review invitation for {PracticeName}.", request.PracticeName);
            return new(ReviewOutreachSendDisposition.Sent);
        }
        if (!settings.Mode.Equals("Smtp", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(settings.Host) ||
            string.IsNullOrWhiteSpace(settings.FromAddress)) return new(ReviewOutreachSendDisposition.PermanentFailure, "email_transport_not_configured");
        try
        {
            using var message = new MailMessage(settings.FromAddress, request.Recipient)
            {
                Subject = $"Thanks for visiting {request.PracticeName}", IsBodyHtml = true,
                Body = $"<p>Thanks for visiting us.</p><p>If you'd like to share your experience, we'd appreciate your feedback.</p><p><a href=\"{WebUtility.HtmlEncode(request.LandingPageUrl.AbsoluteUri)}\">Leave a Review</a></p>"
            };
            using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl };
            if (!string.IsNullOrWhiteSpace(settings.Username)) client.Credentials = new NetworkCredential(settings.Username, settings.Password);
            await client.SendMailAsync(message, cancellationToken);
            return new(ReviewOutreachSendDisposition.Sent);
        }
        catch (SmtpFailedRecipientException ex) when (ex.StatusCode is SmtpStatusCode.MailboxUnavailable or SmtpStatusCode.UserNotLocalTryAlternatePath)
        { return new(ReviewOutreachSendDisposition.PermanentFailure, "recipient_rejected"); }
        catch (FormatException)
        { return new(ReviewOutreachSendDisposition.PermanentFailure, "invalid_email_configuration"); }
        catch (Exception ex) when (ex is SmtpException or IOException)
        { return new(ReviewOutreachSendDisposition.TransientFailure, ex.GetType().Name); }
    }
}

public sealed class ReviewOutreachWorkerOptions
{
    public const string SectionName = "ReviewOutreach:Worker";
    [Range(1, 100)] public int BatchSize { get; set; } = 20;
    [Range(1, 60)] public int PollIntervalSeconds { get; set; } = 10;
    [Range(1, 20)] public int MaxAttempts { get; set; } = 3;
    [Range(1, 3600)] public int LeaseSeconds { get; set; } = 60;
    [Range(1, 86400)] public int InitialRetrySeconds { get; set; } = 60;
    [Range(1, 86400)] public int MaximumRetrySeconds { get; set; } = 3600;
}

public interface IReviewOutreachDispatcher { Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default); }

public sealed class ReviewOutreachDispatcher(CloudDentalDbContext db, IReviewOutreachEligibilityService eligibility,
    IEnumerable<IReviewOutreachSender> senders, IOptions<ReviewOutreachWorkerOptions> options,
    TimeProvider timeProvider, ILogger<ReviewOutreachDispatcher> logger) : IReviewOutreachDispatcher
{
    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var opts = options.Value;
        var ids = await db.ReviewOutreaches.IgnoreQueryFilters().AsNoTracking().Where(x =>
            (x.Status == ReviewOutreachStatus.Scheduled && x.ScheduledAt <= now && (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) ||
            (x.Status == ReviewOutreachStatus.Sending && x.LockedUntil <= now)).OrderBy(x => x.ScheduledAt)
            .Select(x => x.Id).Take(opts.BatchSize).ToListAsync(cancellationToken);
        var sent = 0;
        foreach (var id in ids)
        {
            var lockId = Guid.NewGuid();
            var claimed = await db.ReviewOutreaches.IgnoreQueryFilters().Where(x => x.Id == id &&
                ((x.Status == ReviewOutreachStatus.Scheduled && x.AttemptCount < opts.MaxAttempts && x.ScheduledAt <= now &&
                  (!x.NextAttemptAt.HasValue || x.NextAttemptAt <= now)) || (x.Status == ReviewOutreachStatus.Sending && x.LockedUntil <= now)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ReviewOutreachStatus.Sending)
                    .SetProperty(x => x.LockId, lockId).SetProperty(x => x.LockedUntil, now.AddSeconds(opts.LeaseSeconds))
                    .SetProperty(x => x.LastAttemptAt, now).SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                    .SetProperty(x => x.UpdatedAt, now), cancellationToken);
            if (claimed != 1) continue;
            var row = await db.ReviewOutreaches.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == id && x.LockId == lockId, cancellationToken);
            var revalidation = await eligibility.EvaluateAsync(row.TenantId, row.AppointmentId, false, cancellationToken);
            if (!revalidation.Eligible)
            {
                await Finish(id, lockId, ReviewOutreachStatus.Suppressed, now, revalidation.Reason, cancellationToken);
                logger.LogInformation("Review outreach suppressed for tenant {TenantId}: {Reason}.", row.TenantId, revalidation.Reason);
                continue;
            }
            var sender = senders.SingleOrDefault(x => x.Channel == row.Channel);
            var result = sender is null
                ? new ReviewOutreachSendResult(ReviewOutreachSendDisposition.PermanentFailure, "sender_missing")
                : await sender.SendAsync(new(revalidation.Patient!.Email!, revalidation.Settings!.SenderName,
                    new Uri(revalidation.Settings.ReviewLandingPageUrl!)), cancellationToken);
            if (result.Disposition == ReviewOutreachSendDisposition.Sent)
            {
                await db.ReviewOutreaches.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, ReviewOutreachStatus.Sent).SetProperty(x => x.SentAt, now)
                    .SetProperty(x => x.FailureReason, (string?)null).SetProperty(x => x.LockId, (Guid?)null)
                    .SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                sent++;
            }
            else if (result.Disposition == ReviewOutreachSendDisposition.PermanentFailure || row.AttemptCount >= opts.MaxAttempts)
            {
                await Finish(id, lockId, ReviewOutreachStatus.Failed, now, SafeReason(result.FailureReason), cancellationToken);
                logger.LogWarning("Review outreach failed for tenant {TenantId}, channel {Channel}, after {AttemptCount} attempts: {Reason}.",
                    row.TenantId, row.Channel, row.AttemptCount, SafeReason(result.FailureReason));
            }
            else
            {
                var retry = Math.Min(opts.MaximumRetrySeconds, opts.InitialRetrySeconds * Math.Pow(2, Math.Max(0, row.AttemptCount - 1)));
                await db.ReviewOutreaches.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.Status, ReviewOutreachStatus.Scheduled).SetProperty(x => x.NextAttemptAt, now.AddSeconds(retry))
                    .SetProperty(x => x.FailureReason, SafeReason(result.FailureReason)).SetProperty(x => x.LockId, (Guid?)null)
                    .SetProperty(x => x.LockedUntil, (DateTime?)null).SetProperty(x => x.UpdatedAt, now), cancellationToken);
                logger.LogWarning("Review outreach delivery will retry for tenant {TenantId}, channel {Channel}, attempt {AttemptCount}: {Reason}.",
                    row.TenantId, row.Channel, row.AttemptCount, SafeReason(result.FailureReason));
            }
        }
        db.ChangeTracker.Clear();
        return sent;
    }

    private Task<int> Finish(Guid id, Guid lockId, ReviewOutreachStatus status, DateTime now, string? reason, CancellationToken ct) =>
        db.ReviewOutreaches.IgnoreQueryFilters().Where(x => x.Id == id && x.LockId == lockId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, status).SetProperty(x => x.FailureReason, SafeReason(reason))
            .SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntil, (DateTime?)null)
            .SetProperty(x => x.UpdatedAt, now), ct);
    private static string SafeReason(string? value) => string.IsNullOrWhiteSpace(value) ? "delivery_failed" : value[..Math.Min(128, value.Length)];
}

public sealed class ReviewOutreachWorker(IServiceProvider services, IOptions<ReviewOutreachWorkerOptions> options,
    ILogger<ReviewOutreachWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds));
        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IReviewOutreachDispatcher>().DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Review outreach dispatch failed with {FailureKind}.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

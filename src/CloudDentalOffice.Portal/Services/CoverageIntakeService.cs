using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Eligibility;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class CoverageIntakeOptions
{
    public const string SectionName = "CoverageIntake";

    /// <summary>Off by default: turning it on emails patients.</summary>
    public bool Enabled { get; set; }

    /// <summary>The public IntakeService address the link points at (https).</summary>
    public string? LinkBaseUrl { get; set; }

    /// <summary>Shared with IntakeService; at least 32 bytes, from a secret store.</summary>
    public string? SigningKey { get; set; }

    [Range(1, 30)] public int LinkValidDays { get; set; } = 7;

    /// <summary>One reminder this long before the visit, if the patient hasn't answered.</summary>
    [Range(1, 168)] public int ReminderHoursBefore { get; set; } = 48;

    /// <summary>How long a "no dental insurance" answer makes the patient's appointments self-pay.</summary>
    [Range(1, 730)] public int NoInsuranceValidDays { get; set; } = 180;

    [Range(1, 10)] public int MaxSendAttempts { get; set; } = 3;

    /// <summary>
    /// Listen-only connection for the coverage-intake topic. Preferred over the shared
    /// ServiceBus connection, which would also start the Portal's other consumers.
    /// </summary>
    public string? ServiceBusConnectionString { get; set; }

    public bool IsConfigured => Enabled && CoverageIntakeToken.IsUsableKey(SigningKey) && LinkOrigin is not null;

    /// <summary>
    /// The link's HTTPS origin, or null unless <see cref="LinkBaseUrl"/> is exactly that:
    /// credentials, a path, a query or a fragment would end up in every patient's link.
    /// </summary>
    public Uri? LinkOrigin =>
        Uri.TryCreate(LinkBaseUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.AbsolutePath == "/"
            ? new Uri(uri.GetLeftPart(UriPartial.Authority))
            : null;
}

/// <summary>
/// How an appointment with no coverage on file reads to the front desk, given what
/// the patient was asked and answered. Shared by the sweep and the answer consumer
/// so both write the same thing.
/// </summary>
public static class CoverageIntakeRules
{
    public const string NoEmailReason =
        "No dental coverage on file, and no email to ask the patient. Call them for their dental plan.";

    public static (EligibilityVerificationState State, string Reason) NoCoverage(
        CoverageIntakeRequest? latest, string? patientEmail, CoverageIntakeOptions options, TimeZoneInfo zone, DateTime now)
    {
        string Day(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("M/d");

        if (latest?.Answer == CoverageIntakeAnswer.NoInsurance && latest.AnsweredAt is { } none &&
            none >= now.AddDays(-options.NoInsuranceValidDays))
            return (EligibilityVerificationState.SelfPay, $"Patient said on {Day(none)} they have no dental insurance.");
        if (latest?.Answer == CoverageIntakeAnswer.Plan && latest.AnsweredAt is { } answered)
            return (EligibilityVerificationState.NeedsInfo,
                $"Patient sent their dental plan ({latest.CarrierName}) on {Day(answered)}. Review it under Insurance and add it.");
        if (!options.IsConfigured) return (EligibilityVerificationState.NeedsInfo, CoverageVerificationSweep.NoCoverageReason);
        if (latest is { Status: CoverageIntakeStatus.Failed })
            return (EligibilityVerificationState.NeedsInfo,
                "No dental coverage on file, and the email asking the patient couldn't be delivered. Call them for their dental plan.");
        if (latest is { Status: CoverageIntakeStatus.Sent, SentAt: { } sent })
            return (EligibilityVerificationState.NeedsInfo,
                $"No dental coverage on file. Asked the patient by email on {Day(sent)}; no answer yet.");
        if (!CoverageIntakeService.IsDeliverable(patientEmail))
            return (EligibilityVerificationState.NeedsInfo, NoEmailReason);
        return (EligibilityVerificationState.NeedsInfo, CoverageVerificationSweep.NoCoverageReason);
    }

    /// <summary>The most recent request per patient.</summary>
    public static async Task<Dictionary<int, CoverageIntakeRequest>> LatestByPatientAsync(
        IQueryable<CoverageIntakeRequest> requests, IReadOnlyCollection<int> patientIds, CancellationToken cancellationToken)
    {
        if (patientIds.Count == 0) return [];
        var rows = await requests.AsNoTracking()
            .Where(x => patientIds.Contains(x.PatientId))
            .ToListAsync(cancellationToken);
        return rows.GroupBy(x => x.PatientId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First());
    }
}

public interface ICoverageIntakeService
{
    /// <summary>Emails patients whose upcoming appointments have no coverage, and reminds once. Runs under a pinned tenant.</summary>
    Task<int> SendDueAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends the coverage intake link. One outstanding request per patient: a new one
/// only when there is none, the last link expired unanswered, or the last answer
/// was "no insurance" long enough ago. Sends go through the practice's email
/// transport; the message names the practice and visit date and nothing else.
/// </summary>
public sealed class CoverageIntakeService(
    CloudDentalDbContext db,
    IPatientBillingNotificationSender sender,
    ITenantProvider tenantProvider,
    IOptions<CoverageIntakeOptions> options,
    IOptions<CoverageVerificationOptions> verificationOptions,
    TimeProvider time,
    ILogger<CoverageIntakeService> logger,
    IOptions<PracticeEmailOptions>? practiceEmail = null) : ICoverageIntakeService
{
    private CoverageIntakeOptions Options => options.Value;

    // Longer than any send takes, shorter than the worker's interval.
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    public static bool IsDeliverable(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Trim().Length <= 320 && new EmailAddressAttribute().IsValid(email.Trim());

    public async Task<int> SendDueAsync(CancellationToken cancellationToken = default)
    {
        if (!Options.IsConfigured) return 0;
        var tenantId = tenantProvider.TenantId;
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new InvalidOperationException("Coverage intake must run for an explicit tenant.");
        var now = time.GetUtcNow().UtcDateTime;
        var zone = verificationOptions.Value.ResolveTimeZone(time);

        // The soonest upcoming appointment per patient with nothing on file.
        var rows = await db.CoverageVerifications.AsNoTracking()
            .Where(x => x.ClosedAt == null && x.AppointmentStart >= now && x.PatientInsuranceId == null &&
                        x.State == EligibilityVerificationState.NeedsInfo)
            .OrderBy(x => x.AppointmentStart)
            .ToListAsync(cancellationToken);
        var soonest = rows.GroupBy(x => x.PatientId).Select(g => g.First()).ToList();
        if (soonest.Count == 0) return 0;

        var patientIds = soonest.Select(x => x.PatientId).ToList();
        var emails = await db.Patients.AsNoTracking()
            .Where(p => patientIds.Contains(p.PatientId))
            .ToDictionaryAsync(p => p.PatientId, p => p.Email, cancellationToken);
        var latest = await CoverageIntakeRules.LatestByPatientAsync(db.CoverageIntakeRequests, patientIds, cancellationToken);
        var practice = await PracticeNameAsync(tenantId, cancellationToken);

        var sent = 0;
        foreach (var row in soonest)
        {
            var email = emails.GetValueOrDefault(row.PatientId);
            if (!IsDeliverable(email)) continue;
            latest.TryGetValue(row.PatientId, out var last);

            CoverageIntakeRequest request;
            var reminder = false;
            if (last is { Answer: CoverageIntakeAnswer.Plan }) continue;           // staff take it from here
            if (last is { Answer: CoverageIntakeAnswer.NoInsurance } &&
                last.AnsweredAt >= now.AddDays(-Options.NoInsuranceValidDays)) continue;
            if (last is { Status: CoverageIntakeStatus.Failed } && last.ExpiresAt > now) continue; // staff call instead

            if (last is { Status: CoverageIntakeStatus.Pending } && last.ExpiresAt > now)
            {
                // A retry, unless another instance is still sending (or just tried) it.
                if (last.SendAttempts >= Options.MaxSendAttempts || last.LastAttemptAt > now - RetryAfter) continue;
                request = last;
            }
            else if (last is { Status: CoverageIntakeStatus.Sent } && last.ExpiresAt > now)
            {
                var remindAt = row.AppointmentStart.AddHours(-Options.ReminderHoursBefore);
                if (last.ReminderSentAt is not null || now < remindAt || last.SentAt >= remindAt) continue;
                // Claimed before sending: only one instance reminds.
                var claimed = await db.CoverageIntakeRequests
                    .Where(x => x.Id == last.Id && x.ReminderSentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.ReminderSentAt, now), cancellationToken);
                if (claimed != 1) continue;
                request = last;
                request.ReminderSentAt = now;
                reminder = true;
            }
            else
            {
                request = new CoverageIntakeRequest
                {
                    // Derived from the request it follows, so two instances that both decide a new
                    // one is needed try to insert the same row and only one succeeds.
                    Id = NextRequestId(tenantId, row.PatientId, last?.Id),
                    TenantId = tenantId,
                    PatientId = row.PatientId,
                    CoverageVerificationId = row.Id,
                    RecipientEmail = email!.Trim(),
                    Status = CoverageIntakeStatus.Pending,
                    CreatedAt = now,
                    ExpiresAt = now.AddDays(Options.LinkValidDays)
                };
                db.CoverageIntakeRequests.Add(request);
                // Saved before sending, so a crash never sends a link the portal doesn't know about.
                try { await db.SaveChangesAsync(cancellationToken); }
                catch (DbUpdateException)
                {
                    db.ChangeTracker.Clear();
                    continue;
                }
                db.Entry(request).State = EntityState.Detached;
            }

            if (!reminder)
            {
                // Claim this delivery attempt, so two instances never send the same link twice.
                var attempt = request.SendAttempts;
                var retryBefore = now - RetryAfter;
                var claimed = await db.CoverageIntakeRequests
                    .Where(x => x.Id == request.Id && x.Status == CoverageIntakeStatus.Pending && x.SendAttempts == attempt &&
                                (x.LastAttemptAt == null || x.LastAttemptAt <= retryBefore))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.SendAttempts, attempt + 1)
                        .SetProperty(x => x.LastAttemptAt, now), cancellationToken);
                if (claimed != 1) continue;
                request.SendAttempts = attempt + 1;
                request.LastAttemptAt = now;
            }

            if (await SendAsync(request, practice, row.AppointmentStart, zone, reminder, now, cancellationToken)) sent++;
            await ShowOnAppointmentsAsync(request, email, zone, now, cancellationToken);
        }
        return sent;
    }

    public static Guid NextRequestId(string tenantId, int patientId, Guid? previousId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"coverage-intake\n{tenantId}\n{patientId}\n{previousId?.ToString("N") ?? "first"}")).AsSpan(0, 16));

    /// <summary>So the front desk sees "asked by email" (or "couldn't deliver") now, not at the next sweep.</summary>
    private async Task ShowOnAppointmentsAsync(CoverageIntakeRequest request, string? email, TimeZoneInfo zone, DateTime now,
        CancellationToken cancellationToken)
    {
        var (state, reason) = CoverageIntakeRules.NoCoverage(request, email, Options, zone, now);
        await db.CoverageVerifications
            .Where(x => x.PatientId == request.PatientId && x.ClosedAt == null && x.PatientInsuranceId == null &&
                        x.AppointmentStart >= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.Reason, reason)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
    }

    private async Task<bool> SendAsync(CoverageIntakeRequest request, string practice, DateTime appointmentStart,
        TimeZoneInfo zone, bool reminder, DateTime now, CancellationToken cancellationToken)
    {
        var token = CoverageIntakeToken.Create(
            new(request.TenantId, request.Id, practice, new DateTimeOffset(DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc))),
            Options.SigningKey!);
        var link = new Uri(Options.LinkOrigin!, $"/coverage/{token}");
        var visit = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(appointmentStart, DateTimeKind.Utc), zone);
        var expires = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(request.ExpiresAt, DateTimeKind.Utc), zone);
        var body =
            $"Hello,\n\n{practice} would like to confirm your dental insurance before your appointment on {visit:dddd, MMMM d}.\n\n" +
            $"Please tell us about your dental plan, or let us know you don't have one, using this secure link:\n{link.AbsoluteUri}\n\n" +
            $"The link works until {expires:MMMM d}. If you have questions, please call the office.\n\n{practice}";
        var subject = (reminder ? "Reminder: " : "") + $"Your dental insurance for your visit with {practice}";

        BillingNotificationSendResult result;
        try
        {
            result = await sender.SendAsync(new(request.RecipientEmail, subject, body, practice,
                practiceEmail?.Value.ReplyToFor(request.TenantId)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new(BillingNotificationSendDisposition.TransientFailure, ex.GetType().Name);
        }

        var delivered = result.Disposition == BillingNotificationSendDisposition.Sent;
        request.LastError = delivered ? null : Truncate(result.FailureReason ?? "delivery_failed");
        if (!reminder)
        {
            if (delivered)
            {
                request.Status = CoverageIntakeStatus.Sent;
                request.SentAt = now;
            }
            else if (result.Disposition == BillingNotificationSendDisposition.PermanentFailure ||
                     request.SendAttempts >= Options.MaxSendAttempts)
                request.Status = CoverageIntakeStatus.Failed;
        }
        // A failed reminder is only recorded: the first email went out.
        await db.CoverageIntakeRequests.Where(x => x.Id == request.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, request.Status)
                .SetProperty(x => x.SentAt, request.SentAt)
                .SetProperty(x => x.LastError, request.LastError), cancellationToken);
        if (!delivered)
            logger.LogWarning("Coverage intake email {RequestId} not delivered ({Reason}).", request.Id, request.LastError);
        return delivered;
    }

    private async Task<string> PracticeNameAsync(string tenantId, CancellationToken cancellationToken)
    {
        var name = await db.Tenants.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? await db.Organizations.AsNoTracking().Where(x => x.TenantId == tenantId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken)
            ?? "Your dental office";
        return name.Length <= 120 ? name : name[..120];
    }

    private static string Truncate(string value) => value.Length <= 128 ? value : value[..128];
}

// ── Answers from the public page ──────────────────────────────────────────

/// <summary>An answer that can never be applied (bad signature, unknown request); dead-lettered.</summary>
public sealed class CoverageIntakeRejectedException(string reason) : Exception(reason);

public interface ICoverageIntakeProcessor
{
    Task ProcessAsync(CoverageIntakeSubmittedEvent answer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies a patient's answer. The token is verified again here: the broker is a
/// transport, not a trust boundary. The first answer to a request wins; later ones
/// are ignored. The patient's open appointments without coverage are updated at
/// once, so staff see the answer without waiting for the next sweep.
/// </summary>
public sealed class CoverageIntakeProcessor(
    CloudDentalDbContext db,
    IOptions<CoverageIntakeOptions> options,
    IOptions<CoverageVerificationOptions> verificationOptions,
    TimeProvider time,
    ILogger<CoverageIntakeProcessor> logger) : ICoverageIntakeProcessor
{
    // Service Bus keeps messages 14 days; an older answer is not trusted.
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public async Task ProcessAsync(CoverageIntakeSubmittedEvent answer, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        // Switched off: leave the answer on the subscription (retried, then held until it
        // expires to the dead-letter queue) rather than apply it.
        if (!settings.Enabled || !CoverageIntakeToken.IsUsableKey(settings.SigningKey))
            throw new InvalidOperationException("Coverage intake is switched off or has no signing key.");
        var now = time.GetUtcNow().UtcDateTime;
        var submitted = DateTime.SpecifyKind(answer.SubmittedAtUtc, DateTimeKind.Utc);
        if (submitted > now.AddMinutes(5) || submitted < now - MaxAge)
            throw new CoverageIntakeRejectedException("submitted_at_out_of_range");
        // Valid when the patient sent it, even if it has expired while in transit.
        if (!CoverageIntakeToken.TryRead(answer.Token, settings.SigningKey!, submitted, out var ticket) ||
            ticket.TenantId != answer.TenantId || ticket.RequestId != answer.RequestId)
            throw new CoverageIntakeRejectedException("invalid_token");
        Validate(answer, DateOnly.FromDateTime(submitted));

        var request = await db.CoverageIntakeRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == ticket.RequestId && x.TenantId == ticket.TenantId, cancellationToken)
            ?? throw new CoverageIntakeRejectedException("unknown_request");
        var plan = answer.Answer == CoverageIntakeSubmittedEvent.Plan;
        // The first answer wins; a redelivery or a later answer only re-applies it below.
        await db.CoverageIntakeRequests.IgnoreQueryFilters()
            .Where(x => x.Id == request.Id && x.TenantId == request.TenantId && x.AnsweredAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, CoverageIntakeStatus.Answered)
                .SetProperty(x => x.AnsweredAt, submitted)
                .SetProperty(x => x.Answer, plan ? CoverageIntakeAnswer.Plan : CoverageIntakeAnswer.NoInsurance)
                .SetProperty(x => x.CarrierName, plan ? answer.CarrierName!.Trim() : null)
                .SetProperty(x => x.MemberId, plan ? answer.MemberId!.Trim() : null)
                .SetProperty(x => x.GroupNumber, plan ? answer.GroupNumber : null)
                .SetProperty(x => x.RelationshipToSubscriber, plan ? answer.RelationshipToSubscriber : null)
                .SetProperty(x => x.SubscriberFirstName, plan ? answer.SubscriberFirstName : null)
                .SetProperty(x => x.SubscriberLastName, plan ? answer.SubscriberLastName : null)
                .SetProperty(x => x.SubscriberDateOfBirth, plan ? answer.SubscriberDateOfBirth : null), cancellationToken);

        // Always (re)applied from the stored answer, so a failure here is repaired when the
        // message is retried instead of leaving the appointments stale.
        var stored = await db.CoverageIntakeRequests.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(x => x.Id == request.Id && x.TenantId == request.TenantId, cancellationToken);
        var (state, reason) = CoverageIntakeRules.NoCoverage(stored, request.RecipientEmail, settings,
            verificationOptions.Value.ResolveTimeZone(time), now);
        var updated = await db.CoverageVerifications.IgnoreQueryFilters()
            .Where(x => x.TenantId == request.TenantId && x.PatientId == request.PatientId &&
                        x.ClosedAt == null && x.PatientInsuranceId == null && x.AppointmentStart >= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.State, state)
                .SetProperty(x => x.Reason, reason)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);
        logger.LogInformation("Coverage intake {RequestId} answered ({Answer}); {Updated} appointments updated.",
            request.Id, stored.Answer, updated);
    }

    /// <summary>The same rules the public page applies, since the broker isn't a trust boundary.</summary>
    private static void Validate(CoverageIntakeSubmittedEvent answer, DateOnly submittedOn)
    {
        if (answer.Answer == CoverageIntakeSubmittedEvent.NoInsurance) return;
        if (answer.Answer != CoverageIntakeSubmittedEvent.Plan) throw new CoverageIntakeRejectedException("invalid_answer");
        var errors = CoverageIntakeAnswerRules.ValidatePlan(answer.CarrierName, answer.MemberId, answer.GroupNumber,
            answer.RelationshipToSubscriber, answer.SubscriberFirstName, answer.SubscriberLastName,
            answer.SubscriberDateOfBirth, submittedOn);
        if (errors.Count > 0) throw new CoverageIntakeRejectedException("invalid_plan");
    }
}

public sealed class CoverageIntakeConsumer(IServiceProvider services, ServiceBusOptions options,
    IOptions<CoverageIntakeOptions> intakeOptions, ILogger<CoverageIntakeConsumer> logger) : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off: answers wait on the subscription (up to its 14-day TTL, then dead-letter)
        // and are applied if intake is switched back on in time.
        if (!intakeOptions.Value.Enabled) return;
        var connection = !string.IsNullOrWhiteSpace(intakeOptions.Value.ServiceBusConnectionString)
            ? intakeOptions.Value.ServiceBusConnectionString
            : options.ConnectionString;
        if (string.IsNullOrWhiteSpace(connection))
        {
            logger.LogWarning("Coverage intake is on but has no Service Bus connection; patient answers won't be applied.");
            return;
        }
        _client = new ServiceBusClient(connection);
        _processor = _client.CreateProcessor(options.CoverageIntakeTopic, options.CoverageIntakeSubscription,
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        _processor.ProcessMessageAsync += ProcessAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError("Coverage intake broker processing error ({Source}, {FailureKind}).",
                args.ErrorSource, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(ProcessMessageEventArgs args)
    {
        CoverageIntakeSubmittedEvent? answer = null;
        if (args.Message.Subject == nameof(CoverageIntakeSubmittedEvent))
        {
            try { answer = JsonSerializer.Deserialize<CoverageIntakeSubmittedEvent>(args.Message.Body.ToString()); }
            catch (JsonException) { }
        }
        if (answer is null)
        {
            await args.DeadLetterMessageAsync(args.Message, "InvalidEvent");
            return;
        }

        await using var scope = services.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<ICoverageIntakeProcessor>().ProcessAsync(answer, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (CoverageIntakeRejectedException ex)
        {
            logger.LogWarning("Coverage intake answer {RequestId} rejected ({Reason}).", answer.RequestId, ex.Message);
            await args.DeadLetterMessageAsync(args.Message, "Rejected", ex.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Coverage intake answer {RequestId} will retry ({FailureKind}).", answer.RequestId, ex.GetType().Name);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) await _processor.DisposeAsync();
        if (_client is not null) await _client.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}

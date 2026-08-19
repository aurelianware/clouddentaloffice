using System.Data;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Messaging;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class StripeWebhookPermanentException(string reason) : InvalidOperationException(reason);

public sealed class StripePaymentMetrics : IDisposable
{
    private readonly Meter _meter = new("CloudDentalOffice.Portal.Stripe", "1.0");
    public Counter<long> Succeeded { get; }
    public Counter<long> Failed { get; }
    public Counter<long> Conflicts { get; }
    public Counter<long> DeadLetters { get; }
    public Histogram<double> PostingLatency { get; }

    public StripePaymentMetrics()
    {
        Succeeded = _meter.CreateCounter<long>("stripe.payments.succeeded");
        Failed = _meter.CreateCounter<long>("stripe.payments.failed");
        Conflicts = _meter.CreateCounter<long>("stripe.payments.conflicts");
        DeadLetters = _meter.CreateCounter<long>("stripe.events.dead_lettered");
        PostingLatency = _meter.CreateHistogram<double>("stripe.payment.posting_latency", "s");
    }

    public void Dispose() => _meter.Dispose();
}

public interface IStripePaymentWebhookProcessor
{
    Task ProcessAsync(StripePaymentWebhookEvent webhook, CancellationToken cancellationToken = default);
}

public sealed class StripePaymentPostingOptions
{
    public const string SectionName = "Payments:StripePosting";
    public bool AllocateStatementPayments { get; set; } = true;
}

public sealed class StripePaymentWebhookProcessor(CloudDentalDbContext db, TimeProvider clock,
    StripePaymentMetrics metrics, IOptions<StripePaymentPostingOptions> options,
    ILogger<StripePaymentWebhookProcessor> logger) : IStripePaymentWebhookProcessor
{
    public async Task ProcessAsync(StripePaymentWebhookEvent webhook, CancellationToken cancellationToken = default)
    {
        Validate(webhook);
        if (await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                x.ExternalEventId == webhook.ExternalEventId, cancellationToken)) return;

        try
        {
            await ProcessNewAsync(webhook, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another consumer can commit the same Stripe event after the optimistic
            // check above. The database uniqueness constraint is authoritative.
            db.ChangeTracker.Clear();
            if (await db.PaymentProcessorEvents.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
                    x.TenantId == webhook.TenantId && x.Processor == PaymentProcessorProvider.Stripe &&
                    x.ExternalEventId == webhook.ExternalEventId, cancellationToken)) return;
            throw;
        }
    }

    private async Task ProcessNewAsync(StripePaymentWebhookEvent webhook, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var configuration = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == webhook.TenantId && x.Provider == PaymentProcessorProvider.Stripe && x.Enabled,
            cancellationToken);
        if (configuration is null || configuration.ConnectedMerchantReference != webhook.ConnectedAccountId ||
            (configuration.Environment == PaymentProcessorEnvironment.Production) != webhook.LiveMode)
            throw new StripeWebhookPermanentException("Connected Stripe account mapping is invalid or disabled.");

        var attempt = await db.PatientPaymentAttempts.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.TenantId == webhook.TenantId && x.PaymentReference == webhook.PaymentReference, cancellationToken)
            ?? throw new StripeWebhookPermanentException("Stripe payment reference is unknown.");
        if (!attempt.PaymentId.HasValue)
            throw new StripeWebhookPermanentException("Stripe payment attempt has no canonical payment.");
        var payment = await db.PatientPayments.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == webhook.TenantId && x.PaymentId == attempt.PaymentId, cancellationToken);

        var now = clock.GetUtcNow().UtcDateTime;
        var processorEvent = new PaymentProcessorEvent
        {
            Id = Guid.NewGuid(), TenantId = webhook.TenantId, Processor = PaymentProcessorProvider.Stripe,
            ExternalEventId = webhook.ExternalEventId, ExternalPaymentId = webhook.PaymentIntentId,
            PaymentId = payment.PaymentId, Status = PaymentProcessorEventStatus.Received, CreatedAt = now
        };
        db.PaymentProcessorEvents.Add(processorEvent);

        var conflict = ConflictCode(webhook, attempt, payment);
        if (conflict is not null)
        {
            processorEvent.Status = PaymentProcessorEventStatus.Conflict;
            processorEvent.FailureCode = conflict;
            processorEvent.ProcessedAt = now;
            attempt.Status = PatientPaymentAttemptStatus.ReviewRequired;
            attempt.FailureCode = conflict;
            attempt.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.Conflicts.Add(1);
            logger.LogWarning("Stripe payment event {ExternalEventId} requires review ({ConflictCode}).",
                webhook.ExternalEventId, conflict);
            return;
        }

        if (webhook.EventType == "checkout.session.async_payment_failed")
        {
            payment.Status = PaymentStatus.Failed;
            payment.UpdatedAt = now;
            attempt.Status = PatientPaymentAttemptStatus.Failed;
            attempt.FailureCode = "stripe-payment-failed";
            attempt.UpdatedAt = now;
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            metrics.Failed.Add(1);
            return;
        }

        // A completed Checkout Session can still be unpaid for a delayed method.
        // Only Stripe's paid state, including async success, authorizes ledger posting.
        if (!string.Equals(webhook.PaymentStatus, "paid", StringComparison.Ordinal))
        {
            processorEvent.Status = PaymentProcessorEventStatus.Processed;
            processorEvent.ProcessedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        payment.ExternalSessionId = webhook.CheckoutSessionId;
        payment.ExternalPaymentId = webhook.PaymentIntentId;
        payment.Status = PaymentStatus.Succeeded;
        payment.PaymentDate = webhook.OccurredAt.Kind == DateTimeKind.Utc
            ? webhook.OccurredAt : webhook.OccurredAt.ToUniversalTime();
        payment.UpdatedAt = now;
        attempt.StripePaymentIntentId = webhook.PaymentIntentId;
        attempt.Status = PatientPaymentAttemptStatus.Completed;
        attempt.FailureCode = null;
        attempt.UpdatedAt = now;

        if (!payment.LedgerEntryId.HasValue)
        {
            var ledger = new PatientLedgerEntry
            {
                LedgerEntryId = Guid.NewGuid(), TenantId = webhook.TenantId,
                PatientAccountId = payment.PatientAccountId, EntryType = PatientLedgerEntryType.PatientPayment,
                Amount = payment.Amount, Currency = payment.Currency, EffectiveDate = payment.PaymentDate,
                SourceType = PatientLedgerSourceType.PatientPayment, SourceId = payment.PaymentId.ToString("N"),
                DescriptionCode = "patient-payment", CreatedAt = now, CreatedBy = "processor:Stripe"
            };
            db.PatientLedgerEntries.Add(ledger);
            payment.LedgerEntryId = ledger.LedgerEntryId;
        }

        if (options.Value.AllocateStatementPayments)
            await AllocateStatementAsync(payment, now, cancellationToken);
        await UpdateStatementAsync(payment, now, cancellationToken);
        processorEvent.Status = PaymentProcessorEventStatus.Processed;
        processorEvent.ProcessedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        metrics.Succeeded.Add(1);
        metrics.PostingLatency.Record(Math.Max(0, (now - webhook.OccurredAt).TotalSeconds));
        logger.LogInformation("Stripe event {ExternalEventId} posted payment {PaymentId}.",
            webhook.ExternalEventId, payment.PaymentId);
    }

    private async Task AllocateStatementAsync(PatientPayment payment, DateTime now, CancellationToken cancellationToken)
    {
        if (!payment.StatementId.HasValue) return; // Explicitly remains unapplied.
        var existingAmounts = await db.PatientPaymentAllocations.IgnoreQueryFilters().Where(x =>
                x.TenantId == payment.TenantId && x.PaymentId == payment.PaymentId && !x.UnappliedAt.HasValue)
            .Select(x => x.Amount).ToListAsync(cancellationToken);
        var existing = existingAmounts.Sum();
        var remaining = payment.Amount - existing;
        if (remaining <= 0) return;
        var lines = await db.PatientStatementLines.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == payment.TenantId && x.StatementId == payment.StatementId && x.Amount > 0)
            .OrderBy(x => x.ActivityDate).ThenBy(x => x.StatementLineId).ToListAsync(cancellationToken);
        var ledgerEntryIds = lines.Select(x => x.LedgerEntryId).ToList();
        var targetAllocationRows = await db.PatientPaymentAllocations.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == payment.TenantId && ledgerEntryIds.Contains(x.LedgerEntryId) && !x.UnappliedAt.HasValue)
            .Select(x => new { x.LedgerEntryId, x.Amount }).ToListAsync(cancellationToken);
        var targetAllocations = targetAllocationRows.GroupBy(x => x.LedgerEntryId)
            .ToDictionary(x => x.Key, x => x.Sum(row => row.Amount));
        foreach (var line in lines)
        {
            var allocated = targetAllocations.GetValueOrDefault(line.LedgerEntryId);
            var amount = Math.Min(remaining, Math.Max(0, line.Amount - allocated));
            if (amount <= 0) continue;
            db.PatientPaymentAllocations.Add(new PatientPaymentAllocation
            {
                PaymentAllocationId = Guid.NewGuid(), TenantId = payment.TenantId,
                PaymentId = payment.PaymentId, LedgerEntryId = line.LedgerEntryId,
                Amount = amount, CreatedAt = now, CreatedBy = "processor:Stripe"
            });
            remaining -= amount;
            if (remaining <= 0) break;
        }
    }

    private async Task UpdateStatementAsync(PatientPayment payment, DateTime now, CancellationToken cancellationToken)
    {
        if (!payment.StatementId.HasValue) return;
        var statement = await db.PatientStatements.IgnoreQueryFilters().SingleAsync(x =>
            x.TenantId == payment.TenantId && x.StatementId == payment.StatementId, cancellationToken);
        if (statement.Status is not (PatientStatementStatus.Sent or PatientStatementStatus.PartiallyPaid)) return;
        var priorAmounts = await db.PatientPayments.IgnoreQueryFilters().Where(x => x.TenantId == payment.TenantId &&
                x.StatementId == statement.StatementId && x.PaymentId != payment.PaymentId &&
                x.Status == PaymentStatus.Succeeded).Select(x => x.Amount).ToListAsync(cancellationToken);
        var prior = priorAmounts.Sum();
        statement.Status = prior + payment.Amount >= statement.AmountDue
            ? PatientStatementStatus.Paid : PatientStatementStatus.PartiallyPaid;
        statement.StatusUpdatedAt = now;
    }

    private static string? ConflictCode(StripePaymentWebhookEvent webhook, PatientPaymentAttempt attempt,
        PatientPayment payment)
    {
        if (attempt.ConnectedAccountId != webhook.ConnectedAccountId) return "connected-account-mismatch";
        if (attempt.StripeCheckoutSessionId != webhook.CheckoutSessionId ||
            payment.ExternalSessionId != webhook.CheckoutSessionId) return "checkout-session-mismatch";
        if (!string.Equals(payment.Currency, webhook.Currency, StringComparison.OrdinalIgnoreCase)) return "currency-mismatch";
        var multiplier = CurrencyExponent(webhook.Currency) switch
        {
            0 => 1m,
            3 => 1_000m,
            _ => 100m
        };
        var expectedMinor = decimal.ToInt64(decimal.Round(payment.Amount * multiplier, 0,
            MidpointRounding.AwayFromZero));
        if (expectedMinor != webhook.AmountMinor) return "amount-mismatch";
        if (attempt.StripePaymentIntentId is not null && webhook.PaymentIntentId is not null &&
            attempt.StripePaymentIntentId != webhook.PaymentIntentId) return "payment-intent-mismatch";
        return null;
    }

    private static readonly HashSet<string> ZeroDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
        { "BIF", "CLP", "DJF", "GNF", "JPY", "KMF", "KRW", "MGA", "PYG", "RWF", "UGX", "VND", "VUV", "XAF", "XOF", "XPF" };
    private static readonly HashSet<string> ThreeDecimalCurrencies = new(StringComparer.OrdinalIgnoreCase)
        { "BHD", "JOD", "KWD", "OMR", "TND" };

    private static int CurrencyExponent(string currency) => ZeroDecimalCurrencies.Contains(currency)
        ? 0 : ThreeDecimalCurrencies.Contains(currency) ? 3 : 2;

    private static void Validate(StripePaymentWebhookEvent webhook)
    {
        if (string.IsNullOrWhiteSpace(webhook.TenantId) || string.IsNullOrWhiteSpace(webhook.ExternalEventId) ||
            string.IsNullOrWhiteSpace(webhook.ConnectedAccountId) || string.IsNullOrWhiteSpace(webhook.CheckoutSessionId) ||
            string.IsNullOrWhiteSpace(webhook.PaymentReference) || webhook.AmountMinor < 0 || webhook.Currency.Length != 3 ||
            webhook.EventType is not ("checkout.session.completed" or "checkout.session.async_payment_succeeded" or
                "checkout.session.async_payment_failed"))
            throw new StripeWebhookPermanentException("Stripe payment event is invalid.");
    }
}

public sealed class StripePaymentWebhookConsumer(IServiceProvider services, ServiceBusOptions options,
    StripePaymentMetrics metrics, ILogger<StripePaymentWebhookConsumer> logger) : BackgroundService
{
    private ServiceBusClient? _client;
    private ServiceBusProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsConfigured) return;
        _client = new ServiceBusClient(options.ConnectionString!);
        _processor = _client.CreateProcessor(options.StripeWebhookTopic, options.StripeWebhookSubscription,
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        _processor.ProcessMessageAsync += ProcessAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogError("Stripe webhook broker processing error ({Source}, {FailureKind}).",
                args.ErrorSource, args.Exception.GetType().Name);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync(ProcessMessageEventArgs args)
    {
        if (args.Message.Subject is not (nameof(StripePaymentWebhookEvent) or nameof(StripeRefundWebhookEvent)))
        {
            metrics.DeadLetters.Add(1);
            await args.DeadLetterMessageAsync(args.Message, "UnexpectedSubject");
            return;
        }
        object? webhook;
        try
        {
            webhook = args.Message.Subject == nameof(StripeRefundWebhookEvent)
                ? JsonSerializer.Deserialize<StripeRefundWebhookEvent>(args.Message.Body.ToString())
                : JsonSerializer.Deserialize<StripePaymentWebhookEvent>(args.Message.Body.ToString());
        }
        catch (JsonException) { webhook = null; }
        if (webhook is null)
        {
            metrics.DeadLetters.Add(1);
            await args.DeadLetterMessageAsync(args.Message, "InvalidEvent");
            return;
        }
        await using var scope = services.CreateAsyncScope();
        try
        {
            if (webhook is StripeRefundWebhookEvent refund)
                await scope.ServiceProvider.GetRequiredService<IStripeRefundWebhookProcessor>()
                    .ProcessAsync(refund, args.CancellationToken);
            else
                await scope.ServiceProvider.GetRequiredService<IStripePaymentWebhookProcessor>()
                    .ProcessAsync((StripePaymentWebhookEvent)webhook, args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (StripeWebhookPermanentException ex)
        {
            metrics.DeadLetters.Add(1);
            logger.LogWarning("Stripe event {ExternalEventId} was rejected ({FailureKind}).",
                ExternalEventId(webhook), ex.GetType().Name);
            await args.DeadLetterMessageAsync(args.Message, "PermanentValidationFailure",
                cancellationToken: args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Stripe event {ExternalEventId} processing will retry ({FailureKind}).",
                ExternalEventId(webhook), ex.GetType().Name);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private static string ExternalEventId(object webhook) => webhook switch
    {
        StripePaymentWebhookEvent payment => payment.ExternalEventId,
        StripeRefundWebhookEvent refund => refund.ExternalEventId,
        _ => "unknown"
    };

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) { await _processor.StopProcessingAsync(cancellationToken); await _processor.DisposeAsync(); }
        if (_client is not null) await _client.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}

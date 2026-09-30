using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CloudDentalOffice.Portal.Services;

public sealed class PatientCheckoutOptions
{
    public const string SectionName = "Payments:Checkout";
    public decimal MaximumAmount { get; set; } = 50_000m;
    public bool AllowFullBalance { get; set; } = true;
    public bool AllowStatementBalance { get; set; } = true;
    public bool AllowPartialPayments { get; set; } = true;
    public bool AllowOverpayments { get; set; }
    public string PublicBaseUrl { get; set; } = string.Empty;
    /// <summary>Most payment links that may be created for one patient account in an hour.</summary>
    public int MaximumLinksPerAccountPerHour { get; set; } = 20;
}

public sealed record PatientBalanceCheckoutRequest(string TenantId, Guid PatientAccountId,
    PatientPaymentSelection Selection, Guid? StatementId = null, Money? CustomAmount = null);
public sealed record PatientBalanceCheckoutResult(Guid AttemptId, Guid PaymentId, string PaymentReference,
    Money Amount, Uri CheckoutUrl, DateTime? ExpiresAt);

public interface IPatientBalanceCheckoutService
{
    Task<PatientBalanceCheckoutResult> CreateAsync(PatientBalanceCheckoutRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PatientBalanceCheckoutService(CloudDentalDbContext db, IPaymentCheckoutService checkout,
    ITenantProvider tenantProvider, IOptions<PatientCheckoutOptions> options, TimeProvider clock)
    : IPatientBalanceCheckoutService
{
    public async Task<PatientBalanceCheckoutResult> CreateAsync(PatientBalanceCheckoutRequest request,
        CancellationToken cancellationToken = default)
    {
        PaymentTenantGuard.Ensure(tenantProvider, request.TenantId);
        ValidateSelectionFields(request);
        var settings = options.Value;
        if (settings.MaximumAmount <= 0) throw new InvalidOperationException("Payment maximum is not configured.");
        var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId && x.Id == request.PatientAccountId, cancellationToken)
            ?? throw new KeyNotFoundException("Patient account was not found for the tenant.");
        var entries = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == request.TenantId && x.PatientAccountId == account.Id).ToListAsync(cancellationToken);
        var balance = PatientAccountService.Calculate(entries);
        if (balance.AmountDue <= 0) throw new InvalidOperationException("The patient account has no payable balance.");

        var amount = request.Selection switch
        {
            PatientPaymentSelection.FullBalance when settings.AllowFullBalance => new Money(balance.AmountDue, balance.Currency),
            PatientPaymentSelection.StatementBalance when settings.AllowStatementBalance =>
                await StatementAmount(request, account.Id, cancellationToken),
            PatientPaymentSelection.Partial when settings.AllowPartialPayments && request.CustomAmount.HasValue => request.CustomAmount.Value,
            PatientPaymentSelection.FullBalance or PatientPaymentSelection.StatementBalance or PatientPaymentSelection.Partial =>
                throw new InvalidOperationException("The selected payment option is disabled or incomplete."),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Selection))
        };
        if (amount.Amount <= 0) throw new ArgumentOutOfRangeException(nameof(request.CustomAmount), "Payment amount must be positive.");
        if (amount.Amount > settings.MaximumAmount) throw new InvalidOperationException("Payment amount exceeds the configured maximum.");
        if (!amount.Currency.Equals(balance.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Payment currency does not match the patient account.");
        if (!settings.AllowOverpayments && amount.Amount > balance.AmountDue)
            throw new InvalidOperationException("Payment amount cannot exceed the current account balance.");

        var config = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId && x.Provider == PaymentProcessorProvider.Stripe, cancellationToken)
            ?? throw new PaymentProcessorUnavailableException("Stripe payment configuration is not configured for the tenant.");
        if (!config.Enabled || config.OnboardingStatus != PaymentProcessorOnboardingStatus.Enabled ||
            !config.ChargesEnabled || !config.PayoutsEnabled || string.IsNullOrWhiteSpace(config.ConnectedMerchantReference))
            throw new PaymentProcessorUnavailableException("The practice Stripe account is not ready to accept payments.");

        var baseUri = SafeBaseUri(settings.PublicBaseUrl);
        var now = clock.GetUtcNow().UtcDateTime;
        await EnsureWithinLinkLimitAsync(request.TenantId, account.Id, settings, now, cancellationToken);
        await SupersedeOpenLinksAsync(request.TenantId, account.Id, now, cancellationToken);
        var reference = $"pay_{Guid.NewGuid():N}";
        var attempt = new PatientPaymentAttempt
        {
            Id = Guid.NewGuid(), TenantId = request.TenantId, PatientAccountId = account.Id,
            StatementId = request.StatementId, Selection = request.Selection, Amount = amount.Amount,
            Currency = amount.Currency, PaymentReference = reference, Status = PatientPaymentAttemptStatus.Pending,
            ConnectedAccountId = config.ConnectedMerchantReference, CreatedAt = now, UpdatedAt = now
        };
        db.PatientPaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var session = await checkout.CreateAsync(new PaymentRequest(request.TenantId, account.Id,
                request.StatementId, amount, reference, PatientPaymentMethod.Card,
                $"{baseUri}/payments/success?session_id={{CHECKOUT_SESSION_ID}}",
                $"{baseUri}/payments/cancel"), cancellationToken);
            var paymentId = await db.PatientPayments.IgnoreQueryFilters().Where(x => x.TenantId == request.TenantId &&
                x.InternalPaymentReference == reference).Select(x => x.PaymentId).SingleAsync(cancellationToken);
            attempt.PaymentId = paymentId; attempt.StripeCheckoutSessionId = session.ExternalSessionId;
            attempt.StripePaymentIntentId = session.ExternalPaymentId; attempt.Status = PatientPaymentAttemptStatus.SessionCreated;
            attempt.UpdatedAt = clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync(cancellationToken);
            return new(attempt.Id, paymentId, reference, amount,
                session.CheckoutUrl ?? throw new InvalidOperationException("Stripe did not return a Checkout URL."), session.ExpiresAt);
        }
        catch
        {
            attempt.PaymentId = await db.PatientPayments.IgnoreQueryFilters().Where(x => x.TenantId == request.TenantId &&
                    x.InternalPaymentReference == reference).Select(x => (Guid?)x.PaymentId).SingleOrDefaultAsync(CancellationToken.None);
            attempt.Status = PatientPaymentAttemptStatus.Failed; attempt.FailureCode = "checkout-session-failed";
            attempt.UpdatedAt = clock.GetUtcNow().UtcDateTime; await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task EnsureWithinLinkLimitAsync(string tenantId, Guid accountId, PatientCheckoutOptions settings,
        DateTime now, CancellationToken cancellationToken)
    {
        var since = now.AddHours(-1);
        var recent = await db.PatientPaymentAttempts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId &&
            x.PatientAccountId == accountId && x.CreatedAt > since, cancellationToken);
        if (recent >= Math.Max(1, settings.MaximumLinksPerAccountPerHour))
            throw new InvalidOperationException("Too many payment links were created for this account in the last hour. Try again later.");
    }

    /// <summary>
    /// Each open link charges the amount it was created for, and the balance only counts payments
    /// that have completed, so two open links could each collect the whole balance. A new link
    /// therefore closes the account's earlier open links first. If one of them was already paid,
    /// no new link is created until that payment posts.
    /// </summary>
    private async Task SupersedeOpenLinksAsync(string tenantId, Guid accountId, DateTime now,
        CancellationToken cancellationToken)
    {
        var openSince = now - CheckoutSessionLifetime;
        var open = await db.PatientPaymentAttempts.IgnoreQueryFilters().Where(x => x.TenantId == tenantId &&
                x.PatientAccountId == accountId && x.Status == PatientPaymentAttemptStatus.SessionCreated &&
                x.StripeCheckoutSessionId != null && x.CreatedAt > openSince)
            .ToListAsync(cancellationToken);
        foreach (var attempt in open)
        {
            var closure = await checkout.ExpireAsync(tenantId, attempt.StripeCheckoutSessionId!, cancellationToken);
            if (closure == PaymentSessionClosure.Completed)
                throw new InvalidOperationException(
                    "A payment from an earlier link for this account is being processed. Wait for it to post before creating a new link.");
            attempt.Status = PatientPaymentAttemptStatus.Cancelled;
            attempt.FailureCode = "superseded";
            attempt.UpdatedAt = now;
            var payment = attempt.PaymentId is { } paymentId
                ? await db.PatientPayments.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.TenantId == tenantId &&
                    x.PaymentId == paymentId, cancellationToken)
                : null;
            if (payment is { Status: PaymentStatus.Pending })
            {
                payment.Status = PaymentStatus.Cancelled;
                payment.UpdatedAt = now;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Stripe Checkout Sessions stay open for 24 hours unless an expiry is set, and none is set here.</summary>
    private static readonly TimeSpan CheckoutSessionLifetime = TimeSpan.FromHours(24);

    private static void ValidateSelectionFields(PatientBalanceCheckoutRequest request)
    {
        if (request.Selection != PatientPaymentSelection.StatementBalance && request.StatementId.HasValue)
            throw new ArgumentException("A statement may only be supplied for a statement balance payment.",
                nameof(request.StatementId));
        if (request.Selection != PatientPaymentSelection.Partial && request.CustomAmount.HasValue)
            throw new ArgumentException("A custom amount may only be supplied for a partial payment.",
                nameof(request.CustomAmount));
    }

    private async Task<Money> StatementAmount(PatientBalanceCheckoutRequest request, Guid accountId,
        CancellationToken cancellationToken)
    {
        if (!request.StatementId.HasValue) throw new ArgumentException("A statement is required for statement payment.");
        var statement = await db.PatientStatements.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == request.TenantId && x.StatementId == request.StatementId &&
            x.PatientAccountId == accountId && (x.Status == PatientStatementStatus.Ready ||
                x.Status == PatientStatementStatus.Sent || x.Status == PatientStatementStatus.PartiallyPaid), cancellationToken)
            ?? throw new KeyNotFoundException("Payable statement was not found for the patient account.");
        var paymentAmounts = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == request.TenantId && x.StatementId == statement.StatementId &&
            x.Status == PaymentStatus.Succeeded).Select(x => x.Amount).ToListAsync(cancellationToken);
        return new(Math.Max(0, statement.AmountDue - paymentAmounts.Sum()), statement.Currency);
    }

    private static string SafeBaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Payments Checkout public base URL must be an HTTPS application origin.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}

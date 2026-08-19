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
        var reference = $"pay_{Guid.NewGuid():N}";
        var now = clock.GetUtcNow().UtcDateTime;
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

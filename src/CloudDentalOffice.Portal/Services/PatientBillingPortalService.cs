using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

public sealed record PatientBillingStatementLine(DateTime Date, string Description,
    PatientLedgerEntryType EntryType, decimal Amount, string Currency);
public sealed record PatientBillingStatement(Guid StatementId, DateTime StatementDate, DateTime DueDate,
    PatientStatementStatus Status, decimal PreviousBalance, decimal Charges, decimal InsurancePayments,
    decimal Adjustments, decimal PatientPayments, decimal AmountDue, decimal RemainingDue, string Currency,
    IReadOnlyList<PatientBillingStatementLine> Lines);
public sealed record PatientBillingPayment(DateTime Date, decimal Amount, string Currency,
    PaymentStatus Status, PatientPaymentMethod Method, string Reference);
public sealed record PatientBillingSnapshot(Guid PatientAccountId, decimal CurrentBalance, string Currency,
    decimal Credits, decimal InsurancePayments, IReadOnlyList<PatientBillingStatement> Statements,
    IReadOnlyList<PatientBillingPayment> Payments, bool PartialPaymentsAllowed,
    bool StripeAvailable, PatientPaymentAttemptStatus? LatestAttemptStatus);

public interface IPatientBillingPortalService
{
    Task<PatientBillingSnapshot> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
    Task<PatientBalanceCheckoutResult> CreateCheckoutAsync(ClaimsPrincipal principal,
        PatientPaymentSelection selection, Guid? statementId, decimal? customAmount,
        CancellationToken cancellationToken = default);
}

public sealed class PatientBillingPortalService(CloudDentalDbContext db,
    IPatientBalanceCheckoutService checkout, IConfiguration configuration) : IPatientBillingPortalService
{
    public async Task<PatientBillingSnapshot> GetAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveIdentityAsync(principal, cancellationToken);
        var account = await db.PatientAccounts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == identity.TenantId && x.PatientId == identity.PatientId, cancellationToken);
        if (account is null) return new(Guid.Empty, 0, "USD", 0, 0, [], [], PartialAllowed(), false, null);

        var ledger = await db.PatientLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(x =>
            x.TenantId == identity.TenantId && x.PatientAccountId == account.Id).ToListAsync(cancellationToken);
        var balance = PatientAccountService.Calculate(ledger);
        var statements = await db.PatientStatements.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.TenantId == identity.TenantId && x.PatientAccountId == account.Id &&
                x.Status != PatientStatementStatus.Draft && x.Status != PatientStatementStatus.Voided)
            .OrderByDescending(x => x.StatementDate).Take(12).ToListAsync(cancellationToken);
        var payments = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == identity.TenantId && x.PatientAccountId == account.Id)
            .OrderByDescending(x => x.PaymentDate).Take(12).ToListAsync(cancellationToken);
        var statementPaymentRows = await db.PatientPayments.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == identity.TenantId && x.PatientAccountId == account.Id && x.StatementId.HasValue &&
                x.Status == PaymentStatus.Succeeded).Select(x => new { x.StatementId, x.Amount })
            .ToListAsync(cancellationToken);
        var paidByStatement = statementPaymentRows.GroupBy(x => x.StatementId!.Value)
            .ToDictionary(x => x.Key, x => x.Sum(p => p.Amount));
        var statementViews = statements.Select(x => new PatientBillingStatement(x.StatementId, x.StatementDate,
            x.DueDate, x.Status, x.BalanceForward, x.NewCharges, x.InsurancePayments, x.Adjustments,
            x.PatientPayments, x.AmountDue, Math.Max(0, x.AmountDue - paidByStatement.GetValueOrDefault(x.StatementId)),
            x.Currency, x.Lines.OrderBy(line => line.ActivityDate).Select(line => new PatientBillingStatementLine(
                line.ActivityDate, line.PatientDescription, line.EntryType, line.Amount, line.Currency)).ToList())).ToList();
        var paymentViews = payments.Select(x => new PatientBillingPayment(x.PaymentDate, x.Amount, x.Currency,
            x.Status, x.Method, SafeReference(x.InternalPaymentReference))).ToList();
        var stripeAvailable = await db.PaymentProcessorConfigurations.IgnoreQueryFilters().AsNoTracking().AnyAsync(x =>
            x.TenantId == identity.TenantId && x.Provider == PaymentProcessorProvider.Stripe && x.Enabled &&
            x.OnboardingStatus == PaymentProcessorOnboardingStatus.Enabled && x.ChargesEnabled && x.PayoutsEnabled,
            cancellationToken);
        var latestAttempt = await db.PatientPaymentAttempts.IgnoreQueryFilters().AsNoTracking().Where(x =>
                x.TenantId == identity.TenantId && x.PatientAccountId == account.Id)
            .OrderByDescending(x => x.CreatedAt).Select(x => (PatientPaymentAttemptStatus?)x.Status)
            .FirstOrDefaultAsync(cancellationToken);
        return new(account.Id, balance.AmountDue, balance.Currency, balance.Credits,
            balance.InsurancePayments, statementViews, paymentViews, PartialAllowed(), stripeAvailable, latestAttempt);
    }

    public async Task<PatientBalanceCheckoutResult> CreateCheckoutAsync(ClaimsPrincipal principal,
        PatientPaymentSelection selection, Guid? statementId, decimal? customAmount,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveIdentityAsync(principal, cancellationToken);
        var accountId = await db.PatientAccounts.IgnoreQueryFilters().Where(x =>
            x.TenantId == identity.TenantId && x.PatientId == identity.PatientId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No patient account balance is available.");
        Money? amount = customAmount.HasValue ? new Money(customAmount.Value,
            await AccountCurrency(identity.TenantId, accountId, cancellationToken)) : null;
        return await checkout.CreateAsync(new(identity.TenantId, accountId, selection, statementId, amount),
            cancellationToken);
    }

    private async Task<PatientPortalIdentity> ResolveIdentityAsync(ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true || !principal.IsInRole("Patient"))
            throw new UnauthorizedAccessException("Patient authentication is required.");
        var tenant = PatientAccountApi.TrustedTenantId(principal)
            ?? throw new UnauthorizedAccessException("Authenticated tenant context is required.");
        var issuer = principal.FindFirst("iss")?.Value ?? principal.Identity.AuthenticationType;
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            principal.FindFirst("oid")?.Value;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException("Authenticated patient identity is incomplete.");
        return await db.PatientPortalIdentities.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant && x.Issuer == issuer && x.Subject == subject && x.IsActive, cancellationToken)
            ?? throw new UnauthorizedAccessException("This login is not linked to a patient account.");
    }

    private async Task<string> AccountCurrency(string tenant, Guid accountId, CancellationToken cancellationToken) =>
        await db.PatientLedgerEntries.IgnoreQueryFilters().Where(x => x.TenantId == tenant && x.PatientAccountId == accountId)
            .Select(x => x.Currency).FirstOrDefaultAsync(cancellationToken) ?? "USD";
    private bool PartialAllowed() => configuration.GetValue("Payments:Checkout:AllowPartialPayments", true);
    private static string SafeReference(string reference) => reference.Length <= 8 ? reference : reference[^8..];
}

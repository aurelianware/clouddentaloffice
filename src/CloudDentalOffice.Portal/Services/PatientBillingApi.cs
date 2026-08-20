using System.Security.Claims;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Request body for patient self-service Checkout. It carries only the patient's
/// payment selection — never a patient id or account id. The account is resolved
/// from the authenticated <c>PatientPortalIdentity</c>, so a patient can never
/// target another patient's account by shaping the request.
/// </summary>
public sealed record PatientSelfCheckoutRequest(PatientPaymentSelection Selection, Guid? StatementId, decimal? CustomAmount);

/// <summary>
/// Identity-bound patient billing HTTP surface. Every endpoint requires the
/// <see cref="BillingAuthorization.PatientSelfServicePolicy"/> (authenticated
/// Patient role) and derives the patient's account server-side through
/// <see cref="IPatientBillingPortalService"/>. No route or body accepts a
/// patientId, patientAccountId, statementId, or paymentId as the selection
/// authority, which structurally prevents same-tenant and cross-tenant IDOR.
/// </summary>
public static class PatientBillingApi
{
    public static IEndpointRouteBuilder MapPatientBillingApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/patient/billing")
            .RequireAuthorization(BillingAuthorization.PatientSelfServicePolicy)
            .WithTags("Patient Billing (Self-service)");

        group.MapGet("/account", async (ClaimsPrincipal user, IPatientBillingPortalService portal,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await portal.GetAsync(user, cancellationToken);
                return Results.Ok(snapshot);
            }
            // A valid Patient login that is not linked to a billing account is authenticated
            // but not authorized for any account; hide whether one exists behind a 403.
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapGet("/statements", async (ClaimsPrincipal user, IPatientBillingPortalService portal,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await portal.GetAsync(user, cancellationToken);
                return Results.Ok(snapshot.Statements);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapGet("/payments", async (ClaimsPrincipal user, IPatientBillingPortalService portal,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await portal.GetAsync(user, cancellationToken);
                return Results.Ok(snapshot.Payments);
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
        });

        group.MapPost("/checkout", async (PatientSelfCheckoutRequest request, ClaimsPrincipal user,
            IPatientBillingPortalService portal, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await portal.CreateCheckoutAsync(user, request.Selection, request.StatementId,
                    request.CustomAmount, cancellationToken);
                return Results.Ok(new
                {
                    result.AttemptId, result.PaymentReference, Amount = result.Amount.Amount,
                    result.Amount.Currency, CheckoutUrl = result.CheckoutUrl.AbsoluteUri, result.ExpiresAt
                });
            }
            catch (UnauthorizedAccessException) { return Results.Forbid(); }
            // A statement that is not payable or not owned by the resolved account is
            // reported as not found so a patient cannot probe another account's statements.
            catch (KeyNotFoundException) { return Results.NotFound(); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (PaymentProcessorUnavailableException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException ex)
            { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        return endpoints;
    }
}

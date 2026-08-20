using Microsoft.AspNetCore.Authorization;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Named authorization policies for the billing HTTP surface. Staff financial
/// endpoints are permission-bound (see <see cref="BillingPermissions"/>); the
/// patient self-service endpoints are role-bound and resolve patient identity
/// server-side. Policies are the single source of truth for the API boundary so
/// that handlers never re-implement authorization with ad-hoc role string checks.
/// </summary>
public static class BillingAuthorization
{
    // Staff policy names are intentionally identical to the underlying permission
    // strings so a route reads self-documenting (RequireAuthorization("Billing.View")).
    public const string ViewPolicy = BillingPermissions.View;
    public const string PostPaymentPolicy = BillingPermissions.PostPayment;
    public const string AdjustPolicy = BillingPermissions.Adjust;
    public const string RefundPolicy = BillingPermissions.Refund;
    public const string ConfigurePaymentsPolicy = BillingPermissions.ConfigurePayments;

    /// <summary>Patient self-service billing. Authenticated patient identity only.</summary>
    public const string PatientSelfServicePolicy = "Patient.BillingSelfService";

    public const string PatientRole = "Patient";

    public static IServiceCollection AddBillingAuthorization(this IServiceCollection services)
    {
        var builder = services.AddAuthorizationBuilder();

        // Each staff policy requires an authenticated principal (so anonymous callers
        // receive 401) AND the specific billing permission (so an authenticated but
        // unprivileged caller receives 403). BillingPermissions.Has is the existing,
        // role/claim-aware permission model — reused rather than reinvented here.
        AddPermissionPolicy(builder, ViewPolicy, BillingPermissions.View);
        AddPermissionPolicy(builder, PostPaymentPolicy, BillingPermissions.PostPayment);
        AddPermissionPolicy(builder, AdjustPolicy, BillingPermissions.Adjust);
        AddPermissionPolicy(builder, RefundPolicy, BillingPermissions.Refund);
        AddPermissionPolicy(builder, ConfigurePaymentsPolicy, BillingPermissions.ConfigurePayments);

        // Patient self-service billing never accepts a caller-supplied patient or
        // account identifier; the endpoint resolves the patient from the authenticated
        // PatientPortalIdentity. Requiring the Patient role keeps staff principals off
        // the patient trust surface (and vice-versa).
        builder.AddPolicy(PatientSelfServicePolicy, policy => policy
            .RequireAuthenticatedUser()
            .RequireRole(PatientRole));

        return services;
    }

    private static void AddPermissionPolicy(AuthorizationBuilder builder, string policyName, string permission) =>
        builder.AddPolicy(policyName, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => BillingPermissions.Has(context.User, permission)));
}

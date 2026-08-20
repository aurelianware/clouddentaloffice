using System.Security.Claims;
using CloudDentalOffice.Portal.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CloudDentalOffice.Portal.Tests;

/// <summary>
/// Verifies the billing HTTP boundary: staff endpoints are bound to the correct
/// permission policy and patient endpoints to the identity-bound self-service
/// policy. Two complementary angles are covered — (1) the policies themselves
/// grant/deny the right principals (401 vs 403 semantics), and (2) every mapped
/// billing endpoint actually carries the expected authorization metadata so no
/// route ships as authenticated-only.
/// </summary>
public sealed class BillingAuthorizationTests
{
    // --- Policy evaluation (does the policy admit the right principals?) ---

    [Theory]
    [InlineData(BillingAuthorization.ViewPolicy)]
    [InlineData(BillingAuthorization.PostPaymentPolicy)]
    [InlineData(BillingAuthorization.AdjustPolicy)]
    [InlineData(BillingAuthorization.RefundPolicy)]
    [InlineData(BillingAuthorization.ConfigurePaymentsPolicy)]
    public async Task Anonymous_is_denied_every_staff_policy(string policy)
    {
        Assert.False(await Allowed(Anonymous(), policy));
    }

    [Theory]
    [InlineData(BillingAuthorization.ViewPolicy)]
    [InlineData(BillingAuthorization.PostPaymentPolicy)]
    [InlineData(BillingAuthorization.AdjustPolicy)]
    [InlineData(BillingAuthorization.RefundPolicy)]
    [InlineData(BillingAuthorization.ConfigurePaymentsPolicy)]
    public async Task Authenticated_user_without_billing_permissions_is_denied(string policy)
    {
        Assert.False(await Allowed(Role("Staff"), policy));
    }

    [Theory]
    [InlineData(BillingAuthorization.ViewPolicy)]
    [InlineData(BillingAuthorization.PostPaymentPolicy)]
    [InlineData(BillingAuthorization.AdjustPolicy)]
    [InlineData(BillingAuthorization.RefundPolicy)]
    [InlineData(BillingAuthorization.ConfigurePaymentsPolicy)]
    public async Task Patient_role_is_denied_every_staff_policy(string policy)
    {
        Assert.False(await Allowed(Role("Patient"), policy));
    }

    [Fact]
    public async Task Billing_view_permission_allows_read_but_not_posting_or_config()
    {
        var user = Permission(BillingPermissions.View);
        Assert.True(await Allowed(user, BillingAuthorization.ViewPolicy));
        Assert.False(await Allowed(user, BillingAuthorization.PostPaymentPolicy));
        Assert.False(await Allowed(user, BillingAuthorization.AdjustPolicy));
        Assert.False(await Allowed(user, BillingAuthorization.RefundPolicy));
        Assert.False(await Allowed(user, BillingAuthorization.ConfigurePaymentsPolicy));
    }

    [Theory]
    [InlineData(BillingPermissions.PostPayment, BillingAuthorization.PostPaymentPolicy)]
    [InlineData(BillingPermissions.Adjust, BillingAuthorization.AdjustPolicy)]
    [InlineData(BillingPermissions.Refund, BillingAuthorization.RefundPolicy)]
    [InlineData(BillingPermissions.ConfigurePayments, BillingAuthorization.ConfigurePaymentsPolicy)]
    public async Task A_single_billing_permission_only_unlocks_its_own_policy(string permission, string policy)
    {
        var user = Permission(permission);
        Assert.True(await Allowed(user, policy));
        // The View policy stays closed unless the caller also holds View.
        Assert.False(await Allowed(user, BillingAuthorization.ViewPolicy));
    }

    [Theory]
    [InlineData("BillingStaff", true, true, false, false, false)]
    [InlineData("BillingAdmin", true, true, true, true, true)]
    [InlineData("Admin", true, true, true, true, true)]
    public async Task Billing_roles_map_to_expected_policies(string role, bool view, bool post,
        bool adjust, bool refund, bool config)
    {
        var user = Role(role);
        Assert.Equal(view, await Allowed(user, BillingAuthorization.ViewPolicy));
        Assert.Equal(post, await Allowed(user, BillingAuthorization.PostPaymentPolicy));
        Assert.Equal(adjust, await Allowed(user, BillingAuthorization.AdjustPolicy));
        Assert.Equal(refund, await Allowed(user, BillingAuthorization.RefundPolicy));
        Assert.Equal(config, await Allowed(user, BillingAuthorization.ConfigurePaymentsPolicy));
    }

    [Fact]
    public async Task Patient_self_service_policy_admits_only_authenticated_patients()
    {
        Assert.True(await Allowed(Role("Patient"), BillingAuthorization.PatientSelfServicePolicy));
        Assert.False(await Allowed(Anonymous(), BillingAuthorization.PatientSelfServicePolicy));
        Assert.False(await Allowed(Role("BillingAdmin"), BillingAuthorization.PatientSelfServicePolicy));
        Assert.False(await Allowed(Role("Staff"), BillingAuthorization.PatientSelfServicePolicy));
    }

    // --- Endpoint wiring (is every billing route bound to the expected policy?) ---

    [Fact]
    public void Every_billing_endpoint_declares_its_expected_authorization_policy()
    {
        var expected = new Dictionary<string, string>
        {
            ["GET /api/patient-accounts/patients/{patientId:int}/summary"] = BillingAuthorization.ViewPolicy,
            ["GET /api/patient-accounts/patients/{patientId:int}/ledger"] = BillingAuthorization.ViewPolicy,
            ["POST /api/patient-accounts/{patientAccountId:guid}/checkout"] = BillingAuthorization.PostPaymentPolicy,
            ["POST /api/patient-statements/preview"] = BillingAuthorization.ViewPolicy,
            ["POST /api/patient-statements"] = BillingAuthorization.AdjustPolicy,
            ["GET /api/patient-statements"] = BillingAuthorization.ViewPolicy,
            ["GET /api/patient-statements/{statementId:guid}"] = BillingAuthorization.ViewPolicy,
            ["POST /api/patient-statements/{statementId:guid}/finalize"] = BillingAuthorization.AdjustPolicy,
            ["POST /api/patient-statements/{statementId:guid}/status"] = BillingAuthorization.AdjustPolicy,
            ["POST /api/patient-statements/{statementId:guid}/void"] = BillingAuthorization.AdjustPolicy,
            ["POST /api/patient-statements/{statementId:guid}/supersede"] = BillingAuthorization.AdjustPolicy,
            ["GET /api/patient/billing/account"] = BillingAuthorization.PatientSelfServicePolicy,
            ["GET /api/patient/billing/statements"] = BillingAuthorization.PatientSelfServicePolicy,
            ["GET /api/patient/billing/payments"] = BillingAuthorization.PatientSelfServicePolicy,
            ["POST /api/patient/billing/checkout"] = BillingAuthorization.PatientSelfServicePolicy,
        };

        var actual = MappedBillingEndpoints();

        foreach (var (route, policy) in expected)
        {
            Assert.True(actual.TryGetValue(route, out var policies),
                $"Billing endpoint '{route}' was not mapped. Mapped: {string.Join(" | ", actual.Keys)}");
            Assert.Contains(policy, policies!);
        }

        // Guard: no billing endpoint may ship without an authorization policy.
        foreach (var (route, policies) in actual)
            Assert.True(policies.Count > 0, $"Billing endpoint '{route}' has no authorization policy.");
    }

    private static Dictionary<string, List<string>> MappedBillingEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddBillingAuthorization();
        builder.Services.AddRouting();
        // Register the handler service dependencies so minimal-API parameter inference
        // recognizes them as services (not request bodies) while materializing metadata.
        builder.Services.AddSingleton(Moq.Mock.Of<IPatientAccountService>());
        builder.Services.AddSingleton(Moq.Mock.Of<IPatientStatementService>());
        builder.Services.AddSingleton(Moq.Mock.Of<IPatientBalanceCheckoutService>());
        builder.Services.AddSingleton(Moq.Mock.Of<IPatientBillingPortalService>());
        builder.Services.AddSingleton(TimeProvider.System);
        var app = builder.Build();
        app.MapPatientAccountApi();
        app.MapPatientStatementApi();
        app.MapPatientBillingApi();

        var result = new Dictionary<string, List<string>>();
        foreach (var endpoint in ((IEndpointRouteBuilder)app).DataSources
                     .SelectMany(source => source.Endpoints).OfType<RouteEndpoint>())
        {
            var method = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "ANY";
            // A collection endpoint mapped with an empty pattern inside a group prefix
            // renders with a trailing slash (e.g. "/api/patient-statements/"); normalize
            // it away so the assertions are independent of that framework detail.
            var path = "/" + endpoint.RoutePattern.RawText!.TrimStart('/');
            if (path.Length > 1) path = path.TrimEnd('/');
            var route = $"{method} {path}";
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(x => x.Policy).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
            result[route] = policies;
        }
        return result;
    }

    private static async Task<bool> Allowed(ClaimsPrincipal user, string policy)
    {
        var services = new ServiceCollection();
        services.AddBillingAuthorization();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(user, resource: null, policy)).Succeeded;
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
    private static ClaimsPrincipal Role(string role) =>
        new(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.Role, role) }, "test"));
    private static ClaimsPrincipal Permission(string permission) =>
        new(new ClaimsIdentity(new[] { new System.Security.Claims.Claim(ClaimTypes.Role, "Staff"),
            new System.Security.Claims.Claim(BillingPermissions.ClaimType, permission) }, "test"));
}

using System.Security.Claims;

namespace CloudDentalOffice.Portal.Services.Auth;

/// <summary>
/// Practice staff roles. The Patient role (patient billing portal) is not staff
/// and must never reach practice configuration or the practice's clearinghouse
/// credential.
/// </summary>
public static class StaffRoles
{
    /// <summary>Roles that may manage insurance payers, for <c>[Authorize(Roles = …)]</c>.</summary>
    public const string PayerManagers = "Admin,Dentist,Staff,BillingAdmin,BillingStaff";

    private static readonly string[] PayerManagerRoles = PayerManagers.Split(',');

    public static bool CanManagePayers(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true && PayerManagerRoles.Any(user.IsInRole);
}

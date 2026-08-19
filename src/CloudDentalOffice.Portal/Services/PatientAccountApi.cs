using System.Security.Claims;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

public sealed record PatientLedgerEntryResponse(Guid Id, PatientLedgerEntryType EntryType, decimal Amount,
    string Currency, DateTime EffectiveDate, PatientLedgerSourceType SourceType, string SourceId,
    string DescriptionCode, DateTime CreatedAt, string CreatedBy, Guid? ReversalOfEntryId);

public static class PatientAccountApi
{
    public static IEndpointRouteBuilder MapPatientAccountApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/patient-accounts").RequireAuthorization().WithTags("Patient Accounts");

        group.MapGet("/patients/{patientId:int}/summary", async (int patientId, ClaimsPrincipal user,
            IPatientAccountService accounts, CancellationToken cancellationToken) =>
        {
            var tenantId = TrustedTenantId(user);
            if (tenantId is null) return Results.Forbid();
            var result = await accounts.GetSummaryAsync(tenantId, patientId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        group.MapGet("/patients/{patientId:int}/ledger", async (int patientId, ClaimsPrincipal user,
            IPatientAccountService accounts, CancellationToken cancellationToken) =>
        {
            var tenantId = TrustedTenantId(user);
            if (tenantId is null) return Results.Forbid();
            var rows = await accounts.GetLedgerAsync(tenantId, patientId, cancellationToken);
            return Results.Ok(rows.Select(x => new PatientLedgerEntryResponse(x.LedgerEntryId, x.EntryType, x.Amount,
                x.Currency, x.EffectiveDate, x.SourceType, x.SourceId, x.DescriptionCode, x.CreatedAt,
                x.CreatedBy, x.ReversalOfEntryId)));
        });
        return endpoints;
    }

    public static string? TrustedTenantId(ClaimsPrincipal user) =>
        NormalizeTenantId(user.FindFirst("TenantId")?.Value ?? user.FindFirst("tenant_id")?.Value ??
            user.FindFirst("tenantId")?.Value ?? user.FindFirst("tenant")?.Value);

    private static string? NormalizeTenantId(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
}

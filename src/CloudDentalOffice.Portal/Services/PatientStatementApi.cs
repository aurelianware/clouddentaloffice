using System.Security.Claims;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

public sealed record StatementPreviewRequest(int PatientId, DateTime StatementDate, DateTime DueDate, DateTime? LedgerThroughDate);
public sealed record CreateStatementRequest(int PatientId, DateTime StatementDate, DateTime DueDate,
    DateTime? LedgerThroughDate, bool Finalize);
public sealed record VoidStatementRequest(string ReasonCode);
public sealed record SupersedeStatementRequest(Guid ReplacementStatementId);
public sealed record TransitionStatementRequest(PatientStatementStatus Status);
public sealed record PatientStatementSummaryResponse(Guid StatementId, Guid PatientAccountId, DateTime StatementDate,
    DateTime DueDate, PatientStatementStatus Status, decimal AmountDue, string Currency, DateTime CreatedAt);
public sealed record PatientStatementDetailResponse(PatientStatementSummaryResponse Summary, DateTime LedgerThroughDate,
    decimal BalanceForward, decimal NewCharges, decimal InsurancePayments, decimal Adjustments,
    decimal PatientPayments, decimal Credits, decimal Refunds, decimal DebitAdjustments,
    Guid? SupersedesStatementId, Guid? SupersededByStatementId, DateTime? VoidedAt, string? VoidReasonCode,
    IReadOnlyList<PatientStatementLinePreview> Lines);

public static class PatientStatementApi
{
    public static IEndpointRouteBuilder MapPatientStatementApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/patient-statements").RequireAuthorization().WithTags("Patient Statements");
        group.MapPost("/preview", async (StatementPreviewRequest request, ClaimsPrincipal user,
            IPatientStatementService statements, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            return Results.Ok(await statements.PreviewAsync(tenant, request.PatientId, request.StatementDate,
                request.DueDate, request.LedgerThroughDate ?? clock.GetUtcNow().UtcDateTime, cancellationToken));
        });
        group.MapPost("", async (CreateStatementRequest request, ClaimsPrincipal user,
            IPatientStatementService statements, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            var actor = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("oid")?.Value ?? "authenticated-staff";
            var statement = await statements.CreateAsync(tenant, request.PatientId, request.StatementDate,
                request.DueDate, request.LedgerThroughDate ?? clock.GetUtcNow().UtcDateTime,
                request.Finalize, actor, cancellationToken);
            return Results.Created($"/api/patient-statements/{statement.StatementId}", Summary(statement));
        });
        group.MapGet("", async (int? patientId, ClaimsPrincipal user, IPatientStatementService statements,
            CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            return Results.Ok((await statements.ListAsync(tenant, patientId, cancellationToken)).Select(Summary));
        });
        group.MapGet("/{statementId:guid}", async (Guid statementId, ClaimsPrincipal user,
            IPatientStatementService statements, CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            var statement = await statements.GetAsync(tenant, statementId, cancellationToken);
            return statement is null ? Results.NotFound() : Results.Ok(Detail(statement));
        });
        group.MapPost("/{statementId:guid}/finalize", async (Guid statementId, ClaimsPrincipal user,
            IPatientStatementService statements, CancellationToken cancellationToken) =>
            await WithTenant(user, tenant => statements.FinalizeAsync(tenant, statementId, cancellationToken)));
        group.MapPost("/{statementId:guid}/status", async (Guid statementId, TransitionStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CancellationToken cancellationToken) =>
            await WithTenant(user, tenant => statements.TransitionAsync(tenant, statementId, request.Status, cancellationToken)));
        group.MapPost("/{statementId:guid}/void", async (Guid statementId, VoidStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CancellationToken cancellationToken) =>
            await WithTenant(user, tenant => statements.VoidAsync(tenant, statementId, request.ReasonCode, cancellationToken)));
        group.MapPost("/{statementId:guid}/supersede", async (Guid statementId, SupersedeStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CancellationToken cancellationToken) =>
            await WithTenant(user, tenant => statements.SupersedeAsync(tenant, statementId,
                request.ReplacementStatementId, cancellationToken)));
        return endpoints;
    }

    private static async Task<IResult> WithTenant(ClaimsPrincipal user, Func<string, Task<PatientStatement>> action)
    {
        var tenant = PatientAccountApi.TrustedTenantId(user);
        if (tenant is null) return Results.Forbid();
        return Results.Ok(Summary(await action(tenant)));
    }

    private static PatientStatementSummaryResponse Summary(PatientStatement value) => new(value.StatementId,
        value.PatientAccountId, value.StatementDate, value.DueDate, value.Status, value.AmountDue, value.Currency, value.CreatedAt);

    private static PatientStatementDetailResponse Detail(PatientStatement value) => new(Summary(value),
        value.LedgerThroughDate, value.BalanceForward, value.NewCharges, value.InsurancePayments, value.Adjustments,
        value.PatientPayments, value.Credits, value.Refunds, value.DebitAdjustments, value.SupersedesStatementId,
        value.SupersededByStatementId, value.VoidedAt, value.VoidReasonCode,
        value.Lines.OrderBy(x => x.ActivityDate).ThenBy(x => x.StatementLineId).Select(x =>
            new PatientStatementLinePreview(x.LedgerEntryId, x.ActivityDate, x.EntryType,
                x.PatientDescription, x.Amount, x.Currency)).ToList());
}

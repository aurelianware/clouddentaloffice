using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
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

/// <summary>
/// Staff billing HTTP surface for patient statements. Read operations require
/// <see cref="BillingPermissions.View"/>; statement generation and lifecycle
/// mutations (finalize/status/void/supersede) require <see cref="BillingPermissions.Adjust"/>,
/// matching the in-process staff billing service. Patients never reach these routes —
/// they read finalized statements through the identity-bound patient billing surface.
/// </summary>
public static class PatientStatementApi
{
    public static IEndpointRouteBuilder MapPatientStatementApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/patient-statements").WithTags("Patient Statements (Staff)");
        group.MapPost("/preview", async (StatementPreviewRequest request, ClaimsPrincipal user,
            IPatientStatementService statements, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            return Results.Ok(await statements.PreviewAsync(tenant, request.PatientId, request.StatementDate,
                request.DueDate, request.LedgerThroughDate ?? clock.GetUtcNow().UtcDateTime, cancellationToken));
        }).RequireAuthorization(BillingAuthorization.ViewPolicy);
        group.MapPost("", async (CreateStatementRequest request, ClaimsPrincipal user,
            IPatientStatementService statements, CloudDentalDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            var actor = BillingAudit.Actor(user);
            if (tenant is null || actor is null) return Results.Forbid();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var statement = await statements.CreateAsync(tenant, request.PatientId, request.StatementDate,
                request.DueDate, request.LedgerThroughDate ?? clock.GetUtcNow().UtcDateTime,
                request.Finalize, actor, cancellationToken);
            BillingAudit.Add(db, tenant, actor, "StatementGenerated", nameof(PatientStatement),
                statement.StatementId.ToString("N"), request.Finalize ? "finalized" : "draft", clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Created($"/api/patient-statements/{statement.StatementId}", Summary(statement));
        }).RequireAuthorization(BillingAuthorization.AdjustPolicy);
        group.MapGet("", async (int? patientId, ClaimsPrincipal user, IPatientStatementService statements,
            CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            return Results.Ok((await statements.ListAsync(tenant, patientId, cancellationToken)).Select(Summary));
        }).RequireAuthorization(BillingAuthorization.ViewPolicy);
        group.MapGet("/{statementId:guid}", async (Guid statementId, ClaimsPrincipal user,
            IPatientStatementService statements, CancellationToken cancellationToken) =>
        {
            var tenant = PatientAccountApi.TrustedTenantId(user);
            if (tenant is null) return Results.Forbid();
            var statement = await statements.GetAsync(tenant, statementId, cancellationToken);
            return statement is null ? Results.NotFound() : Results.Ok(Detail(statement));
        }).RequireAuthorization(BillingAuthorization.ViewPolicy);
        group.MapPost("/{statementId:guid}/finalize", async (Guid statementId, ClaimsPrincipal user,
            IPatientStatementService statements, CloudDentalDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
            await Audited(user, db, clock, statementId, "StatementFinalized", null,
                tenant => statements.FinalizeAsync(tenant, statementId, cancellationToken), cancellationToken))
            .RequireAuthorization(BillingAuthorization.AdjustPolicy);
        group.MapPost("/{statementId:guid}/status", async (Guid statementId, TransitionStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CloudDentalDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
            await Audited(user, db, clock, statementId, "StatementStatusChanged", request.Status.ToString(),
                tenant => statements.TransitionAsync(tenant, statementId, request.Status, cancellationToken), cancellationToken))
            .RequireAuthorization(BillingAuthorization.AdjustPolicy);
        group.MapPost("/{statementId:guid}/void", async (Guid statementId, VoidStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CloudDentalDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
            await Audited(user, db, clock, statementId, "StatementVoided", request.ReasonCode,
                tenant => statements.VoidAsync(tenant, statementId, request.ReasonCode, cancellationToken), cancellationToken))
            .RequireAuthorization(BillingAuthorization.AdjustPolicy);
        group.MapPost("/{statementId:guid}/supersede", async (Guid statementId, SupersedeStatementRequest request,
            ClaimsPrincipal user, IPatientStatementService statements, CloudDentalDbContext db, TimeProvider clock,
            CancellationToken cancellationToken) =>
            await Audited(user, db, clock, statementId, "StatementSuperseded", request.ReplacementStatementId.ToString("N"),
                tenant => statements.SupersedeAsync(tenant, statementId, request.ReplacementStatementId, cancellationToken),
                cancellationToken))
            .RequireAuthorization(BillingAuthorization.AdjustPolicy);
        return endpoints;
    }

    /// <summary>Runs a statement lifecycle change and its audit entry in one transaction.</summary>
    private static async Task<IResult> Audited(ClaimsPrincipal user, CloudDentalDbContext db, TimeProvider clock,
        Guid statementId, string action, string? reasonCode, Func<string, Task<PatientStatement>> change,
        CancellationToken cancellationToken)
    {
        var tenant = PatientAccountApi.TrustedTenantId(user);
        var actor = BillingAudit.Actor(user);
        if (tenant is null || actor is null) return Results.Forbid();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var statement = await change(tenant);
        BillingAudit.Add(db, tenant, actor, action, nameof(PatientStatement), statementId.ToString("N"), reasonCode,
            clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(Summary(statement));
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

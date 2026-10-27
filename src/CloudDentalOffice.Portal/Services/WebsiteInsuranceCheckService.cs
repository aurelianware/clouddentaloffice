using CloudDentalOffice.Contracts.Events;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Auth;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

/// <summary>Where a website insurance check stands, in the order the front desk works it.</summary>
public enum WebsiteInsuranceCheckStep
{
    /// <summary>Match the visitor to a patient, or create one.</summary>
    MatchPatient,
    /// <summary>The patient has no email the secure link can go to: call them.</summary>
    CallPatient,
    /// <summary>Ready to email the secure link.</summary>
    SendLink,
    /// <summary>The link went out; waiting for the patient.</summary>
    WaitingForPatient,
    /// <summary>The email couldn't be delivered: call the patient.</summary>
    LinkFailed,
    /// <summary>The patient sent their plan: add it under Insurance, which checks eligibility.</summary>
    AddCoverage,
    /// <summary>The patient said they have no dental insurance.</summary>
    NoInsurance,
    /// <summary>The patient has active coverage on file.</summary>
    CoverageOnFile
}

public sealed record WebsiteInsuranceCheckItem(
    Guid Id, string Name, string? Phone, string? Email, string Carrier, string? Plan, bool WantsAppointment,
    DateTime ReceivedAt, int? PatientId, string? PatientName, WebsiteInsuranceCheckStep Step,
    DateTime? LinkSentAt, Guid? SentPlanId);

public interface IWebsiteInsuranceCheckService
{
    Task<IReadOnlyList<WebsiteInsuranceCheckItem>> GetOpenAsync(CancellationToken cancellationToken = default);
    Task MatchPatientAsync(Guid id, int patientId, CancellationToken cancellationToken = default);
    Task<CoverageIntakeRequest> SendLinkAsync(Guid id, CancellationToken cancellationToken = default);
    Task CloseAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// The front desk's list of website "Check My Insurance" requests. Staff match each
/// visitor to a patient and email the secure coverage link; the patient's answer then
/// follows the same path as a link sent for an appointment.
/// </summary>
public sealed class WebsiteInsuranceCheckService(
    CloudDentalDbContext db,
    ICoverageIntakeService intake,
    ITenantProvider tenantProvider,
    TimeProvider time) : IWebsiteInsuranceCheckService
{
    public async Task<IReadOnlyList<WebsiteInsuranceCheckItem>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        RequireStaff();
        var checks = await db.WebsiteInsuranceChecks.AsNoTracking()
            .Where(x => x.ClosedAt == null)
            .OrderBy(x => x.ReceivedAt)
            .ToListAsync(cancellationToken);
        var patientIds = checks.Where(x => x.PatientId != null).Select(x => x.PatientId!.Value).Distinct().ToList();
        var patients = await db.Patients.AsNoTracking()
            .Where(p => patientIds.Contains(p.PatientId))
            .Select(p => new { p.PatientId, p.FirstName, p.LastName, p.Email })
            .ToDictionaryAsync(p => p.PatientId, cancellationToken);
        var today = time.GetUtcNow().UtcDateTime.Date;
        var covered = (await db.PatientInsurances.AsNoTracking()
            .Where(c => patientIds.Contains(c.PatientId) && c.IsActive && (c.TerminationDate == null || c.TerminationDate >= today))
            .Select(c => c.PatientId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var requestIds = checks.Where(x => x.CoverageIntakeRequestId != null).Select(x => x.CoverageIntakeRequestId!.Value).ToList();
        var requests = await db.CoverageIntakeRequests.AsNoTracking()
            .Where(x => requestIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return checks.Select(x =>
        {
            var patient = x.PatientId is { } pid && patients.TryGetValue(pid, out var p) ? p : null;
            var request = x.CoverageIntakeRequestId is { } rid ? requests.GetValueOrDefault(rid) : null;
            var step = StepFor(patient is not null, CoverageIntakeService.IsDeliverable(patient?.Email),
                patient is not null && covered.Contains(patient.PatientId), request);
            return new WebsiteInsuranceCheckItem(x.Id, x.Name, x.Phone, x.Email, x.Carrier, x.Plan,
                x.RequestingAppointment == "Yes", x.ReceivedAt, patient?.PatientId,
                patient is null ? null : $"{patient.FirstName} {patient.LastName}", step, request?.SentAt,
                request?.Answer == CoverageIntakeAnswer.Plan ? request.Id : null);
        }).ToList();
    }

    public static WebsiteInsuranceCheckStep StepFor(bool matched, bool hasEmail, bool hasCoverage, CoverageIntakeRequest? request) =>
        (matched, request) switch
        {
            (false, _) => WebsiteInsuranceCheckStep.MatchPatient,
            _ when hasCoverage => WebsiteInsuranceCheckStep.CoverageOnFile,
            (_, { Answer: CoverageIntakeAnswer.Plan }) => WebsiteInsuranceCheckStep.AddCoverage,
            (_, { Answer: CoverageIntakeAnswer.NoInsurance }) => WebsiteInsuranceCheckStep.NoInsurance,
            (_, { Status: CoverageIntakeStatus.Sent }) => WebsiteInsuranceCheckStep.WaitingForPatient,
            (_, { Status: CoverageIntakeStatus.Failed }) => WebsiteInsuranceCheckStep.LinkFailed,
            _ when !hasEmail => WebsiteInsuranceCheckStep.CallPatient,
            _ => WebsiteInsuranceCheckStep.SendLink
        };

    public async Task MatchPatientAsync(Guid id, int patientId, CancellationToken cancellationToken = default)
    {
        RequireStaff();
        if (!await db.Patients.AnyAsync(p => p.PatientId == patientId, cancellationToken))
            throw new InvalidOperationException("That patient could not be found.");
        var check = await Open(id, cancellationToken);
        if (check.PatientId != patientId)
        {
            check.PatientId = patientId;
            // A link sent to another patient doesn't answer for this one.
            check.CoverageIntakeRequestId = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CoverageIntakeRequest> SendLinkAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireStaff();
        var check = await Open(id, cancellationToken);
        if (check.PatientId is not { } patientId)
            throw new InvalidOperationException("Match the request to a patient first.");
        var request = await intake.SendNowAsync(patientId, cancellationToken: cancellationToken);
        check.CoverageIntakeRequestId = request.Id;
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    public async Task CloseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        RequireStaff();
        var check = await Open(id, cancellationToken);
        check.ClosedAt = time.GetUtcNow().UtcDateTime;
        check.ClosedBy = Truncate(tenantProvider.User?.Identity?.Name, 200);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<WebsiteInsuranceCheck> Open(Guid id, CancellationToken cancellationToken) =>
        await db.WebsiteInsuranceChecks.SingleOrDefaultAsync(x => x.Id == id && x.ClosedAt == null, cancellationToken)
        ?? throw new InvalidOperationException("This request is already closed.");

    private void RequireStaff()
    {
        if (!StaffRoles.CanManagePayers(tenantProvider.User))
            throw new UnauthorizedAccessException("Practice staff access is required.");
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary>
    /// Stores a relayed website check for its practice, once. Called by the coverage intake
    /// consumer with no signed-in user, so the tenant comes from the event, which IntakeService
    /// set from the website's API key.
    /// </summary>
    public static async Task RecordAsync(CloudDentalDbContext db, WebsiteInsuranceCheckEvent evt, DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evt.TenantId) || string.IsNullOrWhiteSpace(evt.Name) || string.IsNullOrWhiteSpace(evt.Carrier))
            throw new CoverageIntakeRejectedException("invalid_insurance_check");
        if (await db.WebsiteInsuranceChecks.IgnoreQueryFilters().AnyAsync(x => x.Id == evt.EventId, cancellationToken)) return;
        db.WebsiteInsuranceChecks.Add(new WebsiteInsuranceCheck
        {
            Id = evt.EventId,
            TenantId = evt.TenantId,
            Name = Truncate(evt.Name.Trim(), 200)!,
            Phone = Truncate(evt.Phone?.Trim(), 30),
            Email = Truncate(evt.Email?.Trim(), 320),
            Carrier = Truncate(evt.Carrier.Trim(), 120)!,
            Plan = Truncate(evt.Plan?.Trim(), 120),
            RequestingAppointment = evt.RequestingAppointment is "Yes" or "No" ? evt.RequestingAppointment : null,
            Source = Truncate(evt.Source, 100),
            Campaign = Truncate(evt.Campaign, 200),
            AttributionId = Truncate(evt.AttributionId, 200),
            SubmittedAt = evt.SubmittedAtUtc == default ? now : DateTime.SpecifyKind(evt.SubmittedAtUtc, DateTimeKind.Utc),
            ReceivedAt = now
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            // Stored by a concurrent delivery of the same event; anything else is retried.
            if (!await db.WebsiteInsuranceChecks.IgnoreQueryFilters().AnyAsync(x => x.Id == evt.EventId, cancellationToken)) throw;
        }
    }
}

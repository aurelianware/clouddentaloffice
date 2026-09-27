using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CloudDentalOffice.Portal.Services;

/// <summary>What staff enter for one coverage. Dates are calendar dates.</summary>
public sealed record CoverageInput
{
    public int InsurancePlanId { get; init; }
    public string MemberId { get; init; } = string.Empty;
    public string? GroupNumber { get; init; }

    /// <summary>1 = primary, 2 = secondary.</summary>
    public int SequenceNumber { get; init; } = 1;

    /// <summary>Self, Spouse, Child or Other.</summary>
    public string RelationshipToSubscriber { get; init; } = CoverageRelationships.Self;

    public string? SubscriberFirstName { get; init; }
    public string? SubscriberLastName { get; init; }
    public DateTime? SubscriberDateOfBirth { get; init; }

    public DateTime EffectiveDate { get; init; } = DateTime.Today;
    public DateTime? TerminationDate { get; init; }
}

public static class CoverageRelationships
{
    public const string Self = "Self";
    public static readonly IReadOnlyList<string> All = [Self, "Spouse", "Child", "Other"];
}

/// <summary>Front-desk message for a coverage that can't be saved; never echoes submitted values.</summary>
public sealed class CoverageValidationException(string message) : Exception(message);

public interface IPatientCoverageService
{
    Task<IReadOnlyList<PatientInsurance>> GetCoveragesAsync(int patientId, CancellationToken cancellationToken = default);
    Task<PatientInsurance> AddCoverageAsync(int patientId, CoverageInput input, CancellationToken cancellationToken = default);
    Task<PatientInsurance> UpdateCoverageAsync(int patientInsuranceId, CoverageInput input, CancellationToken cancellationToken = default);
    Task DeactivateCoverageAsync(int patientInsuranceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A patient's insurance coverage in the caller's practice. Validation matches
/// what an eligibility check needs, so saved coverage can be checked: a member
/// ID, and the policyholder's name and date of birth when the patient is a
/// dependent. A patient has at most one active primary and one active secondary.
/// Logs carry IDs only, never member IDs or names.
/// </summary>
public sealed class PatientCoverageService(
    CloudDentalDbContext db, ITenantProvider tenantProvider, TimeProvider time, ILogger<PatientCoverageService> logger)
    : IPatientCoverageService
{
    public async Task<IReadOnlyList<PatientInsurance>> GetCoveragesAsync(int patientId, CancellationToken cancellationToken = default) =>
        await db.PatientInsurances
            .Include(x => x.InsurancePlan)
            .Where(x => x.PatientId == patientId)
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.SequenceNumber)
            .ThenByDescending(x => x.EffectiveDate)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

    public async Task<PatientInsurance> AddCoverageAsync(int patientId, CoverageInput input, CancellationToken cancellationToken = default)
    {
        // Query filters scope both lookups to the caller's practice.
        if (!await db.Patients.AnyAsync(p => p.PatientId == patientId, cancellationToken))
            throw new CoverageValidationException("This patient was not found.");
        var normalized = await ValidateAsync(patientId, input, existingId: null, cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var coverage = new PatientInsurance
        {
            TenantId = tenantProvider.TenantId,
            PatientId = patientId,
            CreatedDate = now
        };
        Apply(coverage, normalized);
        db.PatientInsurances.Add(coverage);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Added coverage {PatientInsuranceId} (sequence {Sequence}) for patient {PatientId}",
            coverage.PatientInsuranceId, coverage.SequenceNumber, patientId);
        return coverage;
    }

    public async Task<PatientInsurance> UpdateCoverageAsync(int patientInsuranceId, CoverageInput input, CancellationToken cancellationToken = default)
    {
        var coverage = await db.PatientInsurances.SingleOrDefaultAsync(x => x.PatientInsuranceId == patientInsuranceId, cancellationToken)
            ?? throw new CoverageValidationException("This coverage was not found.");
        var normalized = await ValidateAsync(coverage.PatientId, input, patientInsuranceId, cancellationToken, coverage.IsActive);

        Apply(coverage, normalized);
        coverage.ModifiedDate = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated coverage {PatientInsuranceId} for patient {PatientId}", patientInsuranceId, coverage.PatientId);
        return coverage;
    }

    public async Task DeactivateCoverageAsync(int patientInsuranceId, CancellationToken cancellationToken = default)
    {
        var coverage = await db.PatientInsurances.SingleOrDefaultAsync(x => x.PatientInsuranceId == patientInsuranceId, cancellationToken)
            ?? throw new CoverageValidationException("This coverage was not found.");
        var now = time.GetUtcNow().UtcDateTime;
        coverage.IsActive = false;
        coverage.TerminationDate ??= now.Date;
        coverage.ModifiedDate = now;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deactivated coverage {PatientInsuranceId} for patient {PatientId}", patientInsuranceId, coverage.PatientId);
    }

    private async Task<CoverageInput> ValidateAsync(
        int patientId, CoverageInput input, int? existingId, CancellationToken cancellationToken, bool active = true)
    {
        var plan = await db.InsurancePlans.AsNoTracking()
            .SingleOrDefaultAsync(p => p.InsurancePlanId == input.InsurancePlanId, cancellationToken);
        if (plan is null || !plan.IsActive)
            throw new CoverageValidationException("Choose an active insurance plan. Plans are managed under Claims → Payers.");

        // Column limits: MemberId and GroupNumber are 50 characters.
        var memberId = Text(input.MemberId, 50, "Member ID", required: true)!;
        var group = Text(input.GroupNumber, 50, "Group number", required: false);

        if (input.SequenceNumber is not (1 or 2))
            throw new CoverageValidationException("Choose primary or secondary coverage.");

        var relationship = CoverageRelationships.All.FirstOrDefault(r =>
            string.Equals(r, input.RelationshipToSubscriber?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new CoverageValidationException("Choose the patient's relationship to the policyholder.");

        string? subscriberFirst = null, subscriberLast = null;
        DateTime? subscriberDob = null;
        if (relationship != CoverageRelationships.Self)
        {
            const string missing = "The patient isn't the policyholder, so enter the policyholder's first name, last name and date of birth.";
            subscriberFirst = Text(input.SubscriberFirstName, 60, "Policyholder first name", required: false) ?? throw new CoverageValidationException(missing);
            subscriberLast = Text(input.SubscriberLastName, 60, "Policyholder last name", required: false) ?? throw new CoverageValidationException(missing);
            if (input.SubscriberDateOfBirth is not { } dob)
                throw new CoverageValidationException(missing);
            if (dob.Date > DateTime.Today || dob.Year < 1900)
                throw new CoverageValidationException("The policyholder's date of birth is out of range.");
            subscriberDob = dob.Date;
        }

        if (input.TerminationDate is { } end && end.Date < input.EffectiveDate.Date)
            throw new CoverageValidationException("The termination date can't be before the effective date.");

        if (active && await db.PatientInsurances.AnyAsync(x =>
                x.PatientId == patientId && x.IsActive && x.SequenceNumber == input.SequenceNumber &&
                (existingId == null || x.PatientInsuranceId != existingId), cancellationToken))
        {
            var which = input.SequenceNumber == 1 ? "primary" : "secondary";
            throw new CoverageValidationException(
                $"This patient already has active {which} coverage. Deactivate it first, or save this coverage as {(input.SequenceNumber == 1 ? "secondary" : "primary")}.");
        }

        return input with
        {
            MemberId = memberId,
            GroupNumber = group,
            RelationshipToSubscriber = relationship,
            // Self coverage carries no separate policyholder; clear stale values so
            // eligibility never sends someone else's details for the patient.
            SubscriberFirstName = subscriberFirst,
            SubscriberLastName = subscriberLast,
            SubscriberDateOfBirth = subscriberDob,
            EffectiveDate = input.EffectiveDate.Date,
            TerminationDate = input.TerminationDate?.Date
        };
    }

    private static void Apply(PatientInsurance coverage, CoverageInput input)
    {
        coverage.InsurancePlanId = input.InsurancePlanId;
        coverage.MemberId = input.MemberId;
        coverage.GroupNumber = input.GroupNumber;
        coverage.SequenceNumber = input.SequenceNumber;
        coverage.RelationshipToSubscriber = input.RelationshipToSubscriber;
        coverage.SubscriberFirstName = input.SubscriberFirstName;
        coverage.SubscriberLastName = input.SubscriberLastName;
        coverage.SubscriberDateOfBirth = input.SubscriberDateOfBirth;
        coverage.EffectiveDate = input.EffectiveDate;
        coverage.TerminationDate = input.TerminationDate;
        coverage.IsActive = input.TerminationDate is not { } end || end.Date >= DateTime.Today;
    }

    private static string? Text(string? value, int max, string label, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? throw new CoverageValidationException($"{label} is required.") : null;
        var trimmed = value.Trim();
        if (trimmed.Length > max)
            throw new CoverageValidationException($"{label} must be {max} characters or fewer.");
        if (trimmed.Any(char.IsControl))
            throw new CoverageValidationException($"{label} contains unsupported characters.");
        return trimmed;
    }
}

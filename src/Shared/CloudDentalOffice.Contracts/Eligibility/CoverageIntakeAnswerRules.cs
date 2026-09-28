using System.Text.RegularExpressions;

namespace CloudDentalOffice.Contracts.Eligibility;

/// <summary>
/// What a patient-typed dental plan must look like. The public page applies it to
/// the form and the portal applies it again to the relayed answer, so a forged or
/// altered message can't store what the page would have refused.
/// </summary>
public static partial class CoverageIntakeAnswerRules
{
    public static readonly IReadOnlyList<string> Relationships = ["Self", "Spouse", "Child", "Other"];

    /// <summary>Errors keyed by form field (carrier, memberId, …); empty when the plan is acceptable.</summary>
    public static Dictionary<string, string> ValidatePlan(string? carrier, string? memberId, string? groupNumber,
        string? relationship, string? holderFirstName, string? holderLastName, DateOnly? holderDateOfBirth, DateOnly today)
    {
        var errors = new Dictionary<string, string>();
        Require(errors, "carrier", carrier, 120, "Enter your dental insurance company.");
        Require(errors, "memberId", memberId, 50, "Enter the member ID from your dental card.");
        if (!errors.ContainsKey("memberId") && !MemberIdPattern().IsMatch(memberId!.Trim()))
            errors["memberId"] = "Use only the letters, numbers and dashes shown on your card.";
        if (groupNumber?.Trim().Length > 50) errors["groupNumber"] = "The group number is too long.";

        if (relationship is null || !Relationships.Contains(relationship))
        {
            errors["relationship"] = "Choose whose name the plan is in.";
            return errors;
        }
        if (relationship == "Self") return errors;

        Require(errors, "holderFirstName", holderFirstName, 100, "Enter the plan holder's first name.");
        Require(errors, "holderLastName", holderLastName, 100, "Enter the plan holder's last name.");
        if (holderDateOfBirth is not { } dob || dob.Year < 1900 || dob > today)
            errors["holderDob"] = "Enter the plan holder's date of birth.";
        return errors;
    }

    private static void Require(Dictionary<string, string> errors, string field, string? value, int max, string message)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) errors[field] = message;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\- ]*$")]
    private static partial Regex MemberIdPattern();
}

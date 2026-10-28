using System.Text.RegularExpressions;

namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// What a practice billing profile must hold before a claim can carry it. The rules are
/// the ones payers reject a claim for at the front door (837D 5010 loops 1000A and 2010AA).
/// </summary>
public static partial class PracticeBillingProfileRules
{
    public const string GeneralDentistTaxonomy = "1223G0001X";

    /// <summary>Trims the profile and strips punctuation from the numbers, in place.</summary>
    public static void Normalize(PracticeBillingProfile profile)
    {
        profile.LegalName = Clean(profile.LegalName);
        profile.Npi = Digits(profile.Npi);
        profile.TaxId = Digits(profile.TaxId);
        profile.TaxonomyCode = Clean(profile.TaxonomyCode).ToUpperInvariant();
        profile.Address1 = Clean(profile.Address1);
        profile.Address2 = string.IsNullOrWhiteSpace(profile.Address2) ? null : Clean(profile.Address2);
        profile.City = Clean(profile.City);
        profile.State = Clean(profile.State).ToUpperInvariant();
        profile.ZipCode = Digits(profile.ZipCode);
        profile.ContactName = Clean(profile.ContactName);
        profile.Phone = Digits(profile.Phone);
        if (profile.Phone.Length == 11 && profile.Phone[0] == '1')
            profile.Phone = profile.Phone[1..];
    }

    /// <summary>Every problem with the profile, as a sentence staff can act on. Empty when it's ready.</summary>
    public static IReadOnlyList<string> Problems(PracticeBillingProfile? profile)
    {
        if (profile is null)
            return ["Enter the practice's billing details under Practice setup, Claim billing details."];

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.LegalName))
            problems.Add("Enter the practice's legal name.");
        else if (profile.LegalName.Length > 60)
            problems.Add("Shorten the legal name to 60 characters, the most a claim holds.");
        if (!IsValidNpi(profile.Npi))
            problems.Add("Enter the practice's 10-digit organization NPI.");
        if (!Regex.IsMatch(profile.TaxId, "^[0-9]{9}$") || profile.TaxId == "000000000")
            problems.Add("Enter the practice's 9-digit Tax ID (EIN).");
        if (!TaxonomyPattern().IsMatch(profile.TaxonomyCode))
            problems.Add("Enter a 10-character taxonomy code, such as 1223G0001X for general dentistry.");
        if (string.IsNullOrWhiteSpace(profile.Address1))
            problems.Add("Enter the practice's street address.");
        else if (IsPoBox(profile.Address1) || IsPoBox(profile.Address2))
            problems.Add("Use the practice's street address: payers reject a PO box for the billing provider.");
        if (profile.Address1.Length > 55 || profile.Address2?.Length > 55)
            problems.Add("Shorten each address line to 55 characters, the most a claim holds.");
        if (string.IsNullOrWhiteSpace(profile.City))
            problems.Add("Enter the city.");
        else if (profile.City.Length > 30)
            problems.Add("Shorten the city to 30 characters, the most a claim holds.");
        if (!Regex.IsMatch(profile.State, "^[A-Z]{2}$"))
            problems.Add("Enter the two-letter state.");
        if (!Regex.IsMatch(profile.ZipCode, "^[0-9]{9}$"))
            problems.Add("Enter the full 9-digit ZIP+4: claims need all nine digits.");
        if (string.IsNullOrWhiteSpace(profile.ContactName))
            problems.Add("Enter who payers should ask for about a claim.");
        else if (profile.ContactName.Length > 60)
            problems.Add("Shorten the contact name to 60 characters.");
        if (!Regex.IsMatch(profile.Phone, "^[2-9][0-9]{9}$"))
            problems.Add("Enter a 10-digit billing phone number.");
        return problems;
    }

    /// <summary>The NPI check digit: Luhn over the number with the 80840 prefix.</summary>
    public static bool IsValidNpi(string? npi)
    {
        if (npi is null || !Regex.IsMatch(npi, "^[0-9]{10}$"))
            return false;
        var digits = "80840" + npi;
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    private static bool IsPoBox(string? line) =>
        line is not null && PoBoxPattern().IsMatch(line);

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Digits(string? value) =>
        new((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    [GeneratedRegex("^[0-9]{3}[A-Z0-9]{6}X$")]
    private static partial Regex TaxonomyPattern();

    [GeneratedRegex(@"\b(P\.?\s*O\.?\s*BOX|POST\s+OFFICE\s+BOX|LOCK\s*BOX)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PoBoxPattern();
}

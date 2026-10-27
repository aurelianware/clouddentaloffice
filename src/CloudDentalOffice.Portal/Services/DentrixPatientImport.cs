using System.Globalization;
using System.Net.Mail;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using CloudDentalOffice.Portal.Services.Auth;
using CloudDentalOffice.Portal.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;

namespace CloudDentalOffice.Portal.Services;

public sealed record ImportedCoverage(int SequenceNumber, string CarrierName, string? PayerId, string? MemberId,
    string? GroupNumber, string Relationship, string? SubscriberFirstName, string? SubscriberLastName,
    DateTime? SubscriberDateOfBirth, DateTime? EffectiveDate);

public sealed record ImportedPatient(int Row, string ChartNumber, string FirstName, string LastName, string? MiddleName,
    string? PreferredName, DateTime DateOfBirth, string Gender, string? Email, string? PrimaryPhone, string? SecondaryPhone,
    string? Address1, string? Address2, string? City, string? State, string? ZipCode, string Status,
    IReadOnlyList<ImportedCoverage> Coverages);

/// <summary>A problem tied to a CSV row. Identified by row and chart number only, never by patient name.</summary>
public sealed record ImportIssue(int Row, string? ChartNumber, string Message);

public sealed record PatientImportPreview(IReadOnlyList<ImportedPatient> Patients, IReadOnlyList<ImportIssue> Issues,
    IReadOnlyList<string> RecognizedColumns, IReadOnlyList<string> IgnoredColumns);

public sealed record PatientImportResult(int Created, int Updated, int CoveragesAdded, int CoveragesUpdated,
    int CoveragesReplaced, int PlansCreated, IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Reads a Dentrix patient export (Office Manager data export or a saved report as CSV).
/// Dentrix lets the practice choose and order columns, so columns are matched by name,
/// with the common Dentrix spellings, instead of by position. SSNs are never read.
/// </summary>
public static class DentrixPatientCsv
{
    public const int MaxRows = 20000;

    private enum Field
    {
        Chart, First, Last, Middle, Preferred, Birth, Gender, Email, Phone, Phone2,
        Address1, Address2, City, State, Zip, Status,
        Carrier, PayerId, MemberId, Group, Relation, SubscriberName, SubscriberFirst, SubscriberLast, SubscriberBirth, Effective
    }

    private static readonly Dictionary<Field, string[]> PatientAliases = new()
    {
        [Field.Chart] = ["chartnumber", "chartno", "chart", "patientchartnumber", "patientid", "patid"],
        [Field.First] = ["firstname", "patientfirstname", "first", "patfirstname"],
        [Field.Last] = ["lastname", "patientlastname", "last", "patlastname"],
        [Field.Middle] = ["middlename", "middleinitial", "mi", "patientmi"],
        [Field.Preferred] = ["preferredname", "preferred", "nickname"],
        [Field.Birth] = ["birthdate", "dateofbirth", "dob", "birthday", "patientbirthdate", "patientdob"],
        [Field.Gender] = ["gender", "sex", "patientgender"],
        [Field.Email] = ["email", "emailaddress", "patientemail"],
        [Field.Phone] = ["homephone", "phone", "primaryphone", "patientphone"],
        [Field.Phone2] = ["mobilephone", "cellphone", "mobile", "cell", "workphone", "secondaryphone"],
        [Field.Address1] = ["address1", "address", "street", "streetaddress", "addressline1", "street1"],
        [Field.Address2] = ["address2", "addressline2", "street2"],
        [Field.City] = ["city"],
        [Field.State] = ["state", "st"],
        [Field.Zip] = ["zip", "zipcode", "postalcode", "zippostal"],
        [Field.Status] = ["status", "patientstatus"]
    };

    private static readonly Dictionary<Field, string[]> CoverageAliases = new()
    {
        [Field.Carrier] = ["carriername", "carrier", "insurancecarrier", "inscarrier", "inscarriername", "insurancename", "inscompany", "insurancecompany", "insname"],
        [Field.PayerId] = ["payorid", "payerid", "carrierpayorid", "carrierpayerid", "electronicpayorid", "electronicpayerid", "inspayorid", "inspayerid"],
        [Field.MemberId] = ["subscriberid", "subscriberidnumber", "subscribernumber", "memberid", "insuredid", "inssubscriberid", "subscriberidno"],
        [Field.Group] = ["groupnumber", "groupno", "groupplannumber", "group", "insgroupnumber", "groupid"],
        [Field.Relation] = ["relation", "relationship", "relationtosubscriber", "relationshiptosubscriber", "insrelation", "subscriberrelation", "relationtoinsured"],
        [Field.SubscriberName] = ["subscribername", "subscriber", "insuredname", "inssubscriber", "policyholder", "policyholdername"],
        [Field.SubscriberFirst] = ["subscriberfirstname", "insuredfirstname", "policyholderfirstname"],
        [Field.SubscriberLast] = ["subscriberlastname", "insuredlastname", "policyholderlastname"],
        [Field.SubscriberBirth] = ["subscriberbirthdate", "subscriberdob", "subscriberdateofbirth", "insuredbirthdate", "insureddob", "policyholderdob"],
        [Field.Effective] = ["effectivedate", "coveragestart", "eligibilitystart", "insstartdate"]
    };

    private static readonly Dictionary<int, string[]> CoveragePrefixes = new()
    {
        [1] = ["prim", "primary", "pri", "primins", "priins", "primaryins", "primaryinsurance", "ins1", "insurance1", "dental1", "primdental", "primarydental"],
        [2] = ["sec", "secondary", "secins", "secondaryins", "secondaryinsurance", "ins2", "insurance2", "dental2", "secdental", "secondarydental"]
    };

    private static readonly string[] DateFormats = ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "M-d-yyyy", "MM-dd-yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy H:mm"];

    public static string Normalize(string header) =>
        new(header.Trim().TrimStart('﻿', '​').ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public static PatientImportPreview Parse(string csv, DateTime today)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('﻿')))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        var patients = new List<ImportedPatient>();
        var issues = new List<ImportIssue>();
        var charts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<Field, int>? patientColumns = null;
        Dictionary<(int Slot, Field Field), int> coverageColumns = [];
        string[] header = [];
        var dataRows = 0;
        try
        {
            while (!parser.EndOfData)
            {
                var rowNumber = (int)parser.LineNumber;
                var row = parser.ReadFields() ?? [];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (patientColumns is null)
                {
                    // Dentrix reports put a title and print options above the header row.
                    var mapped = MapPatientColumns(row);
                    if (mapped.ContainsKey(Field.First) && mapped.ContainsKey(Field.Last) && mapped.ContainsKey(Field.Birth))
                    {
                        if (!mapped.ContainsKey(Field.Chart))
                            throw new FormatException("The export needs a Chart Number column so a repeat import updates the same patients.");
                        patientColumns = mapped;
                        coverageColumns = MapCoverageColumns(row, mapped.Values.ToHashSet());
                        header = row;
                    }
                    continue;
                }
                if (++dataRows > MaxRows)
                    throw new FormatException($"An export can contain at most {MaxRows:N0} patients. Split it by last name and import each part.");
                ReadRow(row, rowNumber, patientColumns, coverageColumns, charts, patients, issues, today);
            }
        }
        catch (MalformedLineException) { throw new FormatException("The CSV contains an invalid quoted row."); }
        if (patientColumns is null)
            throw new FormatException("Expected a Dentrix patient export with a header row containing First Name, Last Name, Birth Date and Chart Number.");
        if (patients.Count == 0 && issues.Count == 0)
            throw new FormatException("The export has a header row but no patients.");

        var used = patientColumns.Values.Concat(coverageColumns.Values).ToHashSet();
        var recognized = used.Order().Select(i => header[i]).ToList();
        var ignored = Enumerable.Range(0, header.Length).Where(i => !used.Contains(i) && !string.IsNullOrWhiteSpace(header[i]))
            .Select(i => header[i]).ToList();
        return new(patients, issues, recognized, ignored);
    }

    private static Dictionary<Field, int> MapPatientColumns(string[] row)
    {
        var map = new Dictionary<Field, int>();
        var normalized = row.Select(Normalize).ToArray();
        foreach (var (field, aliases) in PatientAliases)
            foreach (var alias in aliases)
            {
                var index = Array.IndexOf(normalized, alias);
                if (index >= 0 && !map.ContainsValue(index)) { map[field] = index; break; }
            }
        return map;
    }

    private static Dictionary<(int, Field), int> MapCoverageColumns(string[] row, HashSet<int> taken)
    {
        var map = new Dictionary<(int, Field), int>();
        var normalized = row.Select(Normalize).ToArray();
        foreach (var (slot, prefixes) in CoveragePrefixes)
            foreach (var (field, aliases) in CoverageAliases)
            {
                var index = prefixes.SelectMany(p => aliases.Select(a => Array.IndexOf(normalized, p + a)))
                    .FirstOrDefault(i => i >= 0 && !taken.Contains(i), -1);
                if (index < 0) continue;
                map[(slot, field)] = index;
                taken.Add(index);
            }
        return map;
    }

    private static void ReadRow(string[] row, int rowNumber, Dictionary<Field, int> columns,
        Dictionary<(int Slot, Field Field), int> coverageColumns, HashSet<string> charts,
        List<ImportedPatient> patients, List<ImportIssue> issues, DateTime today)
    {
        string? Get(int index) => index < row.Length && !string.IsNullOrWhiteSpace(row[index]) ? row[index].Trim() : null;
        string? Value(Field field) => columns.TryGetValue(field, out var index) ? Get(index) : null;
        string? CoverageValue(int slot, Field field) => coverageColumns.TryGetValue((slot, field), out var index) ? Get(index) : null;

        var chart = Value(Field.Chart);
        void Skip(string reason) => issues.Add(new(rowNumber, chart, "Skipped: " + reason));
        void Warn(string message) => issues.Add(new(rowNumber, chart, message));

        if (chart is null || chart.Length > 64 || chart.Any(char.IsControl)) { Skip("missing or invalid chart number."); return; }
        if (!charts.Add(chart)) { Skip("the chart number appears more than once in this file."); return; }
        var first = Value(Field.First);
        var last = Value(Field.Last);
        if (first is null || last is null || first.Length > 100 || last.Length > 100) { Skip("first and last name are required (up to 100 characters)."); return; }
        if (ParseDate(Value(Field.Birth)) is not { } dob || dob > today || dob.Year < 1900) { Skip("missing or invalid birth date."); return; }

        var email = Value(Field.Email);
        if (email is not null && (email.Length > 255 || !MailAddress.TryCreate(email, out _))) { Warn("Email address was not valid and was left blank."); email = null; }
        var state = Value(Field.State)?.ToUpperInvariant();
        if (state is not null && (state.Length != 2 || !state.All(char.IsAsciiLetter))) { Warn("State was not a two-letter code and was left blank."); state = null; }

        var coverages = new List<ImportedCoverage>();
        foreach (var slot in new[] { 1, 2 })
        {
            var carrier = CoverageValue(slot, Field.Carrier);
            var payerId = CoverageValue(slot, Field.PayerId);
            var memberId = CoverageValue(slot, Field.MemberId);
            if (carrier is null && payerId is null && memberId is null) continue;
            var label = slot == 1 ? "Primary" : "Secondary";
            var subscriberFirst = CoverageValue(slot, Field.SubscriberFirst);
            var subscriberLast = CoverageValue(slot, Field.SubscriberLast);
            if ((subscriberFirst is null || subscriberLast is null) && CoverageValue(slot, Field.SubscriberName) is { } name)
                (subscriberLast, subscriberFirst) = SplitName(name);
            var subscriberDob = ParseDate(CoverageValue(slot, Field.SubscriberBirth));
            if (subscriberDob is { } d && (d > today || d.Year < 1900)) subscriberDob = null;
            var relation = CoverageValue(slot, Field.Relation);
            var relationship = MapRelationship(relation);
            // Without a relation column, a different subscriber must not be sent to the payer as the patient.
            if (relation is null && subscriberLast is not null &&
                (!subscriberLast.Equals(last, StringComparison.OrdinalIgnoreCase) || !string.Equals(subscriberFirst, first, StringComparison.OrdinalIgnoreCase)))
                relationship = "Other";
            if (relationship == CoverageRelationships.Self)
            {
                subscriberFirst = subscriberLast = null;
                subscriberDob = null;
            }
            var group = CoverageValue(slot, Field.Group);
            if (group?.Length > 50) { Warn($"{label} group number is longer than 50 characters and was left blank."); group = null; }
            coverages.Add(new(slot, Truncate(carrier ?? payerId ?? "Unknown carrier", 255)!, payerId?.ToUpperInvariant(),
                memberId, group, relationship, Truncate(subscriberFirst, 60), Truncate(subscriberLast, 60), subscriberDob,
                ParseDate(CoverageValue(slot, Field.Effective))));
        }

        patients.Add(new(rowNumber, chart, first, last, Truncate(Value(Field.Middle), 50), Truncate(Value(Field.Preferred), 20),
            dob, MapGender(Value(Field.Gender)), email, Phone(Value(Field.Phone)), Phone(Value(Field.Phone2)),
            Truncate(Value(Field.Address1), 255), Truncate(Value(Field.Address2), 255), Truncate(Value(Field.City), 100),
            state, Truncate(Value(Field.Zip), 10), MapStatus(Value(Field.Status)), coverages));
    }

    internal static DateTime? ParseDate(string? value) =>
        value is not null && DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.Date : null;

    internal static string MapGender(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "M",
        "F" or "FEMALE" => "F",
        null or "" or "U" or "UNKNOWN" => "U",
        _ => "O"
    };

    internal static string MapStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "active" or "patient" => "Active",
        "archived" => "Archived",
        "deceased" => "Deceased",
        _ => "Inactive" // Inactive, Non-Patient, Duplicate, etc.
    };

    internal static string MapRelationship(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "self" or "subscriber" or "patient" or "insured" => CoverageRelationships.Self,
        "spouse" or "husband" or "wife" or "partner" => "Spouse",
        "child" or "dependent" or "dependant" or "son" or "daughter" => "Child",
        _ => "Other"
    };

    /// <summary>Dentrix prints subscriber names as "Last, First MI".</summary>
    internal static (string? Last, string? First) SplitName(string name)
    {
        var comma = name.IndexOf(',');
        if (comma < 0) return (null, null);
        var last = name[..comma].Trim();
        var first = name[(comma + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return (last.Length == 0 ? null : last, first);
    }

    private static string? Phone(string? value) => value is null ? null : Truncate(value, 20);

    private static string? Truncate(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];
}

public sealed class DentrixPatientImportService(
    DbContextOptions<CloudDentalDbContext> options, ITenantProvider tenant, TimeProvider time,
    ILogger<DentrixPatientImportService> logger)
{
    private void RequireStaff()
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId) || !StaffRoles.CanManagePayers(tenant.User))
            throw new UnauthorizedAccessException("Only practice staff can import patients.");
    }

    public DateTime Today => time.GetLocalNow().Date;

    public async Task<PatientImportResult> ImportAsync(PatientImportPreview preview, CancellationToken cancellationToken = default)
    {
        RequireStaff();
        if (preview.Patients.Count is < 1 or > DentrixPatientCsv.MaxRows)
            throw new FormatException("There are no importable patients in this file.");
        var now = time.GetUtcNow().UtcDateTime;
        var today = Today;
        var issues = new List<ImportIssue>();
        int created = 0, updated = 0, added = 0, changed = 0, replaced = 0, plansCreated = 0;

        await using var db = new CloudDentalDbContext(options, tenant);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var links = await db.ImportedRecordLinks
            .Where(x => x.Source == ImportSources.Dentrix && x.EntityType == ImportedEntityTypes.Patient)
            .ToDictionaryAsync(x => x.ExternalId, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var linkedIds = links.Values.Select(x => x.LocalId).ToHashSet();
        var patients = await db.Patients.Include(p => p.Insurances).ToDictionaryAsync(p => p.PatientId, cancellationToken);
        // Patients entered in CDO before the import (online booking, intake) are adopted when
        // exactly one unlinked patient has the same name and birth date.
        var unlinked = patients.Values.Where(p => !linkedIds.Contains(p.PatientId))
            .GroupBy(p => (p.FirstName.ToLowerInvariant(), p.LastName.ToLowerInvariant(), p.DateOfBirth.Date))
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var plans = await db.InsurancePlans.ToListAsync(cancellationToken);

        var pending = new List<(ImportedPatient Source, Patient Patient)>();
        foreach (var source in preview.Patients)
        {
            Patient patient;
            if (links.TryGetValue(source.ChartNumber, out var link) && patients.TryGetValue(link.LocalId, out var linked))
            {
                patient = linked;
                updated++;
            }
            else if (unlinked.Remove((source.FirstName.ToLowerInvariant(), source.LastName.ToLowerInvariant(), source.DateOfBirth), out var match))
            {
                patient = match;
                updated++;
            }
            else
            {
                patient = new Patient { TenantId = tenant.TenantId, CreatedDate = now, CreatedBy = "Dentrix import" };
                db.Patients.Add(patient);
                created++;
            }
            if (patient.PatientId != 0) patient.ModifiedDate = now;
            Apply(patient, source);
            pending.Add((source, patient));
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (source, patient) in pending)
        {
            if (links.TryGetValue(source.ChartNumber, out var link))
            {
                link.LocalId = patient.PatientId;
                link.ImportedAt = now;
            }
            else
            {
                db.ImportedRecordLinks.Add(new ImportedRecordLink
                {
                    TenantId = tenant.TenantId, Source = ImportSources.Dentrix, EntityType = ImportedEntityTypes.Patient,
                    ExternalId = source.ChartNumber, LocalId = patient.PatientId, ImportedAt = now
                });
            }

            foreach (var coverage in source.Coverages)
            {
                var label = coverage.SequenceNumber == 1 ? "Primary" : "Secondary";
                if (coverage.PayerId is null || coverage.PayerId.Length > 10)
                {
                    issues.Add(new(source.Row, source.ChartNumber, $"{label} insurance not imported: the carrier has no electronic payer ID (up to 10 characters). Add it on the patient's insurance tab."));
                    continue;
                }
                if (coverage.MemberId is null || coverage.MemberId.Length > 50)
                {
                    issues.Add(new(source.Row, source.ChartNumber, $"{label} insurance not imported: the subscriber ID is missing or longer than 50 characters."));
                    continue;
                }
                if (coverage.Relationship != CoverageRelationships.Self &&
                    (coverage.SubscriberFirstName is null || coverage.SubscriberLastName is null || coverage.SubscriberDateOfBirth is null))
                {
                    issues.Add(new(source.Row, source.ChartNumber, $"{label} insurance not imported: the patient isn't the subscriber, and the subscriber's name and birth date are needed for eligibility."));
                    continue;
                }

                var plan = plans.FirstOrDefault(p => p.IsActive && p.PayerId.Equals(coverage.PayerId, StringComparison.OrdinalIgnoreCase))
                    ?? plans.FirstOrDefault(p => p.PayerId.Equals(coverage.PayerId, StringComparison.OrdinalIgnoreCase));
                if (plan is null)
                {
                    plan = new InsurancePlan { TenantId = tenant.TenantId, PayerId = coverage.PayerId, PayerName = coverage.CarrierName, CreatedDate = now };
                    db.InsurancePlans.Add(plan);
                    plans.Add(plan);
                    plansCreated++;
                }
                else if (!plan.IsActive)
                {
                    plan.IsActive = true;
                    plan.ModifiedDate = now;
                }

                var input = new CoverageInput
                {
                    InsurancePlanId = plan.InsurancePlanId,
                    MemberId = coverage.MemberId,
                    GroupNumber = coverage.GroupNumber,
                    SequenceNumber = coverage.SequenceNumber,
                    RelationshipToSubscriber = coverage.Relationship,
                    SubscriberFirstName = coverage.SubscriberFirstName,
                    SubscriberLastName = coverage.SubscriberLastName,
                    SubscriberDateOfBirth = coverage.SubscriberDateOfBirth,
                    EffectiveDate = coverage.EffectiveDate ?? today
                };
                var current = patient.Insurances.FirstOrDefault(x => x.IsActive && x.SequenceNumber == coverage.SequenceNumber);
                if (current is not null && (current.InsurancePlanId == plan.InsurancePlanId || current.InsurancePlan == plan) &&
                    string.Equals(current.MemberId, coverage.MemberId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!PatientCoverageService.ChangesWhatIsVerified(current, input with { InsurancePlanId = current.InsurancePlanId })) continue;
                    current.LastVerifiedAt = null;
                    current.LastVerificationState = null;
                    current.GroupNumber = input.GroupNumber;
                    current.RelationshipToSubscriber = input.RelationshipToSubscriber;
                    current.SubscriberFirstName = input.SubscriberFirstName;
                    current.SubscriberLastName = input.SubscriberLastName;
                    current.SubscriberDateOfBirth = input.SubscriberDateOfBirth;
                    current.ModifiedDate = now;
                    changed++;
                    continue;
                }
                if (current is not null)
                {
                    // Dentrix now lists a different plan or subscriber ID in this slot.
                    current.IsActive = false;
                    if (current.TerminationDate is null || current.TerminationDate.Value.Date > today) current.TerminationDate = today;
                    current.ModifiedDate = now;
                    replaced++;
                    // The filtered unique index allows one active coverage per slot, so retire it first.
                    await db.SaveChangesAsync(cancellationToken);
                }
                else added++;
                var insurance = new PatientInsurance
                {
                    TenantId = tenant.TenantId, PatientId = patient.PatientId, InsurancePlan = plan,
                    MemberId = input.MemberId, GroupNumber = input.GroupNumber, SequenceNumber = input.SequenceNumber,
                    RelationshipToSubscriber = input.RelationshipToSubscriber,
                    SubscriberFirstName = input.SubscriberFirstName, SubscriberLastName = input.SubscriberLastName,
                    SubscriberDateOfBirth = input.SubscriberDateOfBirth, EffectiveDate = input.EffectiveDate.Date,
                    IsActive = true, CreatedDate = now
                };
                db.PatientInsurances.Add(insurance);
                patient.Insurances.Add(insurance);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Dentrix patient import for tenant {TenantId}: {Created} created, {Updated} updated, {Added} coverages added, {Changed} updated, {Replaced} replaced, {Plans} plans created",
            tenant.TenantId, created, updated, added, changed, replaced, plansCreated);
        return new(created, updated, added, changed, replaced, plansCreated, issues);
    }

    private static void Apply(Patient patient, ImportedPatient source)
    {
        patient.FirstName = source.FirstName;
        patient.LastName = source.LastName;
        patient.MiddleName = source.MiddleName;
        patient.PreferredName = source.PreferredName;
        patient.DateOfBirth = source.DateOfBirth;
        patient.Gender = source.Gender;
        patient.Status = source.Status;
        // Blank export cells don't erase contact details someone entered in CDO.
        patient.Email = source.Email ?? patient.Email;
        patient.PrimaryPhone = source.PrimaryPhone ?? patient.PrimaryPhone;
        patient.SecondaryPhone = source.SecondaryPhone ?? patient.SecondaryPhone;
        if (source.Address1 is not null)
        {
            patient.Address1 = source.Address1;
            patient.Address2 = source.Address2;
            patient.City = source.City;
            patient.State = source.State;
            patient.ZipCode = source.ZipCode;
        }
        patient.ModifiedBy = patient.PatientId == 0 ? null : "Dentrix import";
    }
}

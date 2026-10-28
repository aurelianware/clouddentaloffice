using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace CloudDentalOffice.Portal.Services;

/// <summary>One insurance row: the patient it belongs to and the coverage. SequenceNumber is 0 when the report has no order column.</summary>
public sealed record ImportedPatientCoverage(int Row, string? ChartNumber, string PatientLastName, string PatientFirstName,
    DateTime PatientDateOfBirth, ImportedCoverage Coverage);

/// <param name="EndedRows">Plans whose coverage or plan order ended before today; history, not imported.</param>
public sealed record InsuranceImportPreview(IReadOnlyList<ImportedPatientCoverage> Rows, IReadOnlyList<ImportIssue> Issues,
    IReadOnlyList<string> RecognizedColumns, IReadOnlyList<string> IgnoredColumns, int EndedRows = 0);

/// <summary>
/// Reads a Dentrix Ascend "Patient Insurance" report (one row per patient plan) exported as CSV.
/// Patients are matched to records already imported, by chart number when present, otherwise by
/// last name, first name and the patient's birth date.
/// </summary>
public static partial class DentrixInsuranceCsv
{
    public const int MaxRows = 40000;

    private enum Field { Chart, Patient, PatientFirst, PatientLast, Birth, Carrier, PayerId, MemberId, Group, Subscriber, SubscriberBirth, Relation, Order, Start, End, CoverageEnd }

    private static readonly Dictionary<Field, string[]> Aliases = new()
    {
        [Field.Chart] = ["chartnumber", "chartno", "chart"],
        [Field.Patient] = ["patient", "patientname"],
        [Field.PatientFirst] = ["patientfirstname", "firstname"],
        [Field.PatientLast] = ["patientlastname", "lastname"],
        [Field.Birth] = ["birthdate", "patientbirthdate", "dateofbirth", "patientdateofbirth", "dob", "patientdob"],
        [Field.Carrier] = ["insurancecarrier", "carrier", "carriername", "insurancecompany"],
        [Field.PayerId] = ["payerid", "payorid", "electronicpayerid"],
        [Field.MemberId] = ["subscriberid", "subscriberidnumber", "memberid", "insuredid"],
        [Field.Group] = ["groupnumber", "groupno", "group", "groupplannumber", "groupid"],
        [Field.Subscriber] = ["subscriber", "subscribername", "policyholder", "insured"],
        [Field.SubscriberBirth] = ["subscriberbirthdate", "subscriberdob", "subscriberdateofbirth", "insuredbirthdate"],
        [Field.Relation] = ["relationship", "relation", "relationtosubscriber", "relationshiptosubscriber", "patientrelationship", "subscriberrelationship"],
        [Field.Order] = ["insuranceplanorder", "coverageorder", "planorder", "priority", "rank", "insurancerank", "insuranceorder", "order", "coveragetype", "sequence", "insurancepriority"],
        [Field.Start] = ["planorderstartdate", "effectivedate", "coveragestartdate", "datecovstart"],
        // Ascend ends a plan order when the order changes and a coverage when the plan ends; either retires the row.
        [Field.End] = ["planorderenddate", "terminationdate", "coverageenddate"],
        [Field.CoverageEnd] = ["datecovend"]
    };

    [GeneratedRegex("^[A-Za-z0-9]{2,10}$")]
    private static partial Regex PayerIdPattern();

    /// <summary>Ascend prints "Not Available: spaces etc." where a carrier has no payer ID.</summary>
    public static bool IsPayerId(string value) => PayerIdPattern().IsMatch(value);

    /// <summary>True when the CSV header looks like an insurance report rather than a patient list.</summary>
    public static bool IsInsuranceReport(string csv)
    {
        using var reader = new StringReader(csv.TrimStart('﻿'));
        for (var i = 0; i < 10 && reader.ReadLine() is { } line; i++)
        {
            var columns = line.Split(',').Select(DentrixPatientCsv.Normalize).ToHashSet();
            if (Aliases[Field.MemberId].Any(columns.Contains) && Aliases[Field.PayerId].Any(columns.Contains)) return true;
        }
        return false;
    }

    public static InsuranceImportPreview Parse(string csv, DateTime today)
    {
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('﻿')))
        { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = true };
        parser.SetDelimiters(",");
        var rows = new List<ImportedPatientCoverage>();
        var issues = new List<ImportIssue>();
        var ended = 0;
        Dictionary<Field, int>? columns = null;
        string[] header = [];
        try
        {
            while (!parser.EndOfData)
            {
                var rowNumber = (int)parser.LineNumber;
                var row = parser.ReadFields() ?? [];
                if (row.All(string.IsNullOrWhiteSpace)) continue;
                if (columns is null)
                {
                    var mapped = Map(row);
                    var hasPatient = mapped.ContainsKey(Field.Patient) || (mapped.ContainsKey(Field.PatientFirst) && mapped.ContainsKey(Field.PatientLast));
                    if (hasPatient && mapped.ContainsKey(Field.Birth) && mapped.ContainsKey(Field.MemberId) && mapped.ContainsKey(Field.PayerId))
                    {
                        columns = mapped;
                        header = row;
                    }
                    continue;
                }
                if (rows.Count + issues.Count >= MaxRows)
                    throw new FormatException($"An insurance report can contain at most {MaxRows:N0} rows.");
                if (!ReadRow(row, rowNumber, columns, rows, issues, today)) ended++;
            }
        }
        catch (MalformedLineException) { throw new FormatException("The CSV contains an invalid quoted row."); }
        if (columns is null)
            throw new FormatException("Expected an insurance report with Patient, Birth Date, Subscriber ID and Payer ID columns.");
        if (rows.Count == 0 && issues.Count == 0 && ended == 0)
            throw new FormatException("The report has a header row but no insurance rows.");
        var used = columns.Values.ToHashSet();
        return new(rows, issues, used.Order().Select(i => header[i]).ToList(),
            Enumerable.Range(0, header.Length).Where(i => !used.Contains(i) && !string.IsNullOrWhiteSpace(header[i])).Select(i => header[i]).ToList(),
            ended);
    }

    private static Dictionary<Field, int> Map(string[] row)
    {
        var normalized = row.Select(DentrixPatientCsv.Normalize).ToArray();
        var map = new Dictionary<Field, int>();
        foreach (var (field, aliases) in Aliases)
        {
            var index = aliases.Select(a => Array.IndexOf(normalized, a)).FirstOrDefault(i => i >= 0 && !map.ContainsValue(i), -1);
            if (index >= 0) map[field] = index;
        }
        return map;
    }

    /// <returns>False when the row is a plan that has already ended.</returns>
    private static bool ReadRow(string[] row, int rowNumber, Dictionary<Field, int> columns,
        List<ImportedPatientCoverage> rows, List<ImportIssue> issues, DateTime today)
    {
        string? Value(Field field) =>
            columns.TryGetValue(field, out var i) && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
        var chart = Value(Field.Chart);
        void Skip(string reason) => issues.Add(new(rowNumber, chart, "Skipped: " + reason));

        string? last = Value(Field.PatientLast), first = Value(Field.PatientFirst);
        if (Value(Field.Patient) is { } name) (last, first) = DentrixPatientCsv.SplitName(name);
        if (last is null || first is null) { Skip("the patient name isn't in \"Last, First\" form."); return true; }
        if (DentrixPatientCsv.ParseDate(Value(Field.Birth)) is not { } dob || dob > today || dob.Year < 1900) { Skip("missing or invalid patient birth date."); return true; }
        if (DentrixPatientCsv.ParseDate(Value(Field.End)) is { } end && end < today ||
            DentrixPatientCsv.ParseDate(Value(Field.CoverageEnd)) is { } coverageEnd && coverageEnd < today) return false;

        var payerId = Value(Field.PayerId);
        if (payerId is not null && !IsPayerId(payerId)) payerId = null;
        string? subscriberLast = null, subscriberFirst = null;
        if (Value(Field.Subscriber) is { } subscriber) (subscriberLast, subscriberFirst) = DentrixPatientCsv.SplitName(subscriber);
        var isPatient = subscriberLast is null ||
            (subscriberLast.Equals(last, StringComparison.OrdinalIgnoreCase) && SameFirstName(subscriberFirst, first));
        var relationship = Value(Field.Relation) is { } relation
            ? DentrixPatientCsv.MapRelationship(relation)
            : isPatient ? CoverageRelationships.Self : "Other";
        DateTime? subscriberDob = null;
        if (relationship == CoverageRelationships.Self) subscriberLast = subscriberFirst = null;
        else if (DentrixPatientCsv.ParseDate(Value(Field.SubscriberBirth)) is { } d && d <= today && d.Year >= 1900) subscriberDob = d;

        var group = Value(Field.Group);
        if (group?.Length > 50) group = null;
        var carrier = Value(Field.Carrier) ?? payerId ?? "Unknown carrier";
        rows.Add(new(rowNumber, chart, last, first, dob, new ImportedCoverage(MapOrder(Value(Field.Order)),
            carrier.Length <= 255 ? carrier : carrier[..255], payerId?.ToUpperInvariant(), Value(Field.MemberId), group, relationship,
            subscriberFirst, subscriberLast, subscriberDob, DentrixPatientCsv.ParseDate(Value(Field.Start)))));
        return true;
    }

    internal static int MapOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "primary" or "pri" or "prim" or "1st" or "first" => 1,
        "2" or "secondary" or "sec" or "2nd" or "second" => 2,
        _ => 0
    };

    /// <summary>Reports add middle names to the first name ("Mary Ann" vs "Mary"), so the first word is compared.</summary>
    internal static bool SameFirstName(string? a, string? b) =>
        a is not null && b is not null && FirstWord(a).Equals(FirstWord(b), StringComparison.OrdinalIgnoreCase);

    internal static string FirstWord(string name) => name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
}

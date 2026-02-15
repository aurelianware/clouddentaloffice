using CloudDentalOffice.Contracts.Claims;

namespace CloudDentalOffice.EdiCommon.Generators;

/// <summary>
/// Generates X12 837D (Dental) claim transactions.
/// Clean-room implementation based on ASC X12 005010X224A2 specification.
/// </summary>
public class Claim837DGenerator
{
    private readonly char _es = '*';  // element separator
    private readonly char _st = '~';  // segment terminator
    private readonly char _ss = ':';  // sub-element separator

    public string Generate(ClaimDto claim, ProviderInfo provider, SubmitterInfo submitter)
    {
        var lines = new List<string>();
        var controlNumber = GenerateControlNumber();

        // ISA - Interchange Control Header
        lines.Add(BuildIsa(submitter, controlNumber));

        // GS - Functional Group Header
        lines.Add(BuildGs(submitter, controlNumber));

        // ST - Transaction Set Header
        lines.Add($"ST{_es}837{_es}{controlNumber.Substring(0, 4)}{_es}005010X224A2{_st}");

        // BHT - Beginning of Hierarchical Transaction
        lines.Add($"BHT{_es}0019{_es}00{_es}{controlNumber}{_es}{DateTime.UtcNow:yyyyMMdd}{_es}{DateTime.UtcNow:HHmm}{_es}CH{_st}");

        // 1000A - Submitter
        lines.Add($"NM1{_es}41{_es}2{_es}{submitter.OrganizationName}{_es}{_es}{_es}{_es}{_es}46{_es}{submitter.Etin}{_st}");
        lines.Add($"PER{_es}IC{_es}{submitter.ContactName}{_es}TE{_es}{submitter.ContactPhone}{_st}");

        // 1000B - Receiver
        lines.Add($"NM1{_es}40{_es}2{_es}{claim.PayerName ?? "UNKNOWN"}{_es}{_es}{_es}{_es}{_es}46{_es}{claim.PayerId}{_st}");

        // 2000A - Billing Provider HL
        lines.Add($"HL{_es}1{_es}{_es}20{_es}1{_st}");
        lines.Add($"NM1{_es}85{_es}1{_es}{provider.LastName}{_es}{provider.FirstName}{_es}{_es}{_es}{_es}XX{_es}{provider.Npi}{_st}");
        lines.Add($"N3{_es}{provider.Address.Line1}{_st}");
        lines.Add($"N4{_es}{provider.Address.City}{_es}{provider.Address.State}{_es}{provider.Address.ZipCode}{_st}");
        lines.Add($"REF{_es}EI{_es}{provider.TaxId}{_st}");

        // 2000B - Subscriber HL
        lines.Add($"HL{_es}2{_es}1{_es}22{_es}0{_st}");
        lines.Add($"SBR{_es}P{_es}18{_es}{claim.GroupNumber ?? ""}{_es}{_es}{_es}{_es}{_es}{_es}CI{_st}");

        // 2010BA - Subscriber Name
        lines.Add($"NM1{_es}IL{_es}1{_es}{_es}{_es}{_es}{_es}{_es}MI{_es}{claim.SubscriberId}{_st}");

        // 2010BB - Payer Name
        lines.Add($"NM1{_es}PR{_es}2{_es}{claim.PayerName ?? "UNKNOWN"}{_es}{_es}{_es}{_es}{_es}PI{_es}{claim.PayerId}{_st}");

        // 2300 - Claim Information
        lines.Add($"CLM{_es}{claim.Id}{_es}{claim.TotalCharge:F2}{_es}{_es}{_es}11{_ss}B{_ss}1{_es}Y{_es}A{_es}Y{_es}Y{_st}");
        lines.Add($"DTP{_es}472{_es}D8{_es}{claim.ServiceDate:yyyyMMdd}{_st}");

        // 2400 - Service Lines
        foreach (var line in claim.Lines)
        {
            lines.Add($"LX{_es}{line.LineNumber}{_st}");

            var sv3 = $"SV3{_es}AD{_ss}{line.CdtCode}{_es}{line.Charge:F2}{_es}UN{_es}1";
            if (!string.IsNullOrEmpty(line.ToothNumber))
                sv3 += $"{_es}{_es}{_es}{_es}{line.ToothNumber}";
            if (!string.IsNullOrEmpty(line.Surface))
                sv3 += $"{_es}{line.Surface}";
            lines.Add(sv3 + _st);

            lines.Add($"DTP{_es}472{_es}D8{_es}{claim.ServiceDate:yyyyMMdd}{_st}");
        }

        // SE - Transaction Set Trailer
        var segmentCount = lines.Count(l => !l.StartsWith("ISA") && !l.StartsWith("GS")) + 1;
        lines.Add($"SE{_es}{segmentCount}{_es}{controlNumber.Substring(0, 4)}{_st}");

        // GE - Functional Group Trailer
        lines.Add($"GE{_es}1{_es}{controlNumber.Substring(0, 9)}{_st}");

        // IEA - Interchange Control Trailer
        lines.Add($"IEA{_es}1{_es}{controlNumber.Substring(0, 9).PadLeft(9, '0')}{_st}");

        return string.Join("\n", lines);
    }

    private string BuildIsa(SubmitterInfo submitter, string controlNumber)
    {
        return $"ISA{_es}00{_es}          {_es}00{_es}          {_es}ZZ{_es}{submitter.Etin.PadRight(15)}{_es}ZZ{_es}{("RECEIVER").PadRight(15)}{_es}{DateTime.UtcNow:yyMMdd}{_es}{DateTime.UtcNow:HHmm}{_es}{_ss}{_es}00501{_es}{controlNumber.Substring(0, 9).PadLeft(9, '0')}{_es}0{_es}P{_es}{_ss}{_st}";
    }

    private string BuildGs(SubmitterInfo submitter, string controlNumber)
    {
        return $"GS{_es}HC{_es}{submitter.Etin}{_es}RECEIVER{_es}{DateTime.UtcNow:yyyyMMdd}{_es}{DateTime.UtcNow:HHmm}{_es}{controlNumber.Substring(0, 9)}{_es}X{_es}005010X224A2{_st}";
    }

    private static string GenerateControlNumber() =>
        DateTime.UtcNow.Ticks.ToString().Substring(0, 9);
}

public record ProviderInfo
{
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public string Npi { get; init; } = string.Empty;
    public string TaxId { get; init; } = string.Empty;
    public ProviderAddress Address { get; init; } = new();
}

public record ProviderAddress
{
    public string Line1 { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string ZipCode { get; init; } = string.Empty;
}

public record SubmitterInfo
{
    public string OrganizationName { get; init; } = string.Empty;
    public string Etin { get; init; } = string.Empty;
    public string ContactName { get; init; } = string.Empty;
    public string ContactPhone { get; init; } = string.Empty;
}

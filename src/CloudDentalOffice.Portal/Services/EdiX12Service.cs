using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace CloudDentalOffice.Portal.Services;

/// <summary>
/// Generates HIPAA 5010 X12 837D (Dental) transactions from claim data
/// </summary>
public interface IEdiX12Service
{
    Task<string> Generate837DTransactionAsync(Claim claim);
}

public class EdiX12Service : IEdiX12Service
{
    private readonly CloudDentalDbContext _context;
    private readonly ILogger<EdiX12Service> _logger;
    private const string ELEMENT_SEPARATOR = "*";
    private const string SEGMENT_TERMINATOR = "~\n";
    private const string COMPOSITE_SEPARATOR = ">";

    public EdiX12Service(CloudDentalDbContext context, ILogger<EdiX12Service> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<string> Generate837DTransactionAsync(Claim claim)
    {
        // Load all required relationships
        var fullClaim = await _context.Claims
            .Include(c => c.Patient)
            .Include(c => c.Provider)
            .Include(c => c.PatientInsurance)
                .ThenInclude(pi => pi!.InsurancePlan)
            .Include(c => c.Procedures)
            .FirstOrDefaultAsync(c => c.ClaimId == claim.ClaimId);

        if (fullClaim == null)
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} not found");

        if (fullClaim.Provider == null)
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} has no provider");

        if (fullClaim.Patient == null)
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} has no patient");

        if (fullClaim.PatientInsurance?.InsurancePlan == null)
            throw new InvalidOperationException($"Claim {claim.ClaimNumber} has no insurance plan");

        // The practice bills the claim; its profile is read for the claim's own practice.
        var practice = await _context.PracticeBillingProfiles.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(p => p.TenantId == fullClaim.TenantId);
        var practiceProblems = PracticeBillingProfileRules.Problems(practice);
        if (practiceProblems.Count > 0)
            throw new InvalidOperationException(
                $"Claim {claim.ClaimNumber} can't be built until the practice's billing details are complete: {string.Join(" ", practiceProblems)}");

        if (!PracticeBillingProfileRules.IsValidNpi(fullClaim.Provider.NPI))
            throw new InvalidOperationException(
                $"Claim {claim.ClaimNumber} needs a valid 10-digit NPI for {fullClaim.Provider.FullName} under Providers.");

        var x12 = new StringBuilder();

        // Generate unique control numbers
        var interchangeControlNumber = GenerateControlNumber();
        var groupControlNumber = GenerateControlNumber();
        var transactionControlNumber = GenerateControlNumber();
        var submitterIdentifier = practice!.TaxId;
        var receiverIdentifier = fullClaim.PatientInsurance.InsurancePlan.EdiPayerId ?? fullClaim.PatientInsurance.InsurancePlan.PayerId;

        // ISA - Interchange Control Header
        x12.Append(BuildISASegment(submitterIdentifier, receiverIdentifier, interchangeControlNumber));

        // GS - Functional Group Header
        x12.Append(BuildGSSegment(submitterIdentifier, receiverIdentifier, groupControlNumber));

        // ST - Transaction Set Header
        x12.Append($"ST{ELEMENT_SEPARATOR}837{ELEMENT_SEPARATOR}{transactionControlNumber}{ELEMENT_SEPARATOR}005010X224A2{SEGMENT_TERMINATOR}");

        // BHT - Beginning of Hierarchical Transaction
        var bhtDate = DateTime.Now.ToString("yyyyMMdd");
        var bhtTime = DateTime.Now.ToString("HHmm");
        x12.Append($"BHT{ELEMENT_SEPARATOR}0019{ELEMENT_SEPARATOR}00{ELEMENT_SEPARATOR}{fullClaim.ClaimNumber}{ELEMENT_SEPARATOR}{bhtDate}{ELEMENT_SEPARATOR}{bhtTime}{ELEMENT_SEPARATOR}CH{SEGMENT_TERMINATOR}");

        // 1000A Loop - Submitter (Dental Office)
        x12.Append($"NM1{ELEMENT_SEPARATOR}41{ELEMENT_SEPARATOR}2{ELEMENT_SEPARATOR}{Value(practice.LegalName)}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}46{ELEMENT_SEPARATOR}{submitterIdentifier}{SEGMENT_TERMINATOR}");
        x12.Append($"PER{ELEMENT_SEPARATOR}IC{ELEMENT_SEPARATOR}{Value(practice.ContactName)}{ELEMENT_SEPARATOR}TE{ELEMENT_SEPARATOR}{practice.Phone}{SEGMENT_TERMINATOR}");

        // 1000B Loop - Receiver (Payer)
        x12.Append($"NM1{ELEMENT_SEPARATOR}40{ELEMENT_SEPARATOR}2{ELEMENT_SEPARATOR}{fullClaim.PatientInsurance.InsurancePlan.PayerName}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}46{ELEMENT_SEPARATOR}{receiverIdentifier}{SEGMENT_TERMINATOR}");

        // 2000A Loop - Billing Provider Hierarchical Level
        x12.Append($"HL{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}20{ELEMENT_SEPARATOR}1{SEGMENT_TERMINATOR}");
        x12.Append($"PRV{ELEMENT_SEPARATOR}BI{ELEMENT_SEPARATOR}PXC{ELEMENT_SEPARATOR}{practice.TaxonomyCode}{SEGMENT_TERMINATOR}");

        // 2010AA Loop - Billing Provider (the practice: organization NPI, street address, EIN)
        x12.Append($"NM1{ELEMENT_SEPARATOR}85{ELEMENT_SEPARATOR}2{ELEMENT_SEPARATOR}{Value(practice.LegalName)}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}XX{ELEMENT_SEPARATOR}{practice.Npi}{SEGMENT_TERMINATOR}");
        var billingStreet = string.IsNullOrEmpty(practice.Address2)
            ? Value(practice.Address1)
            : $"{Value(practice.Address1)}{ELEMENT_SEPARATOR}{Value(practice.Address2)}";
        x12.Append($"N3{ELEMENT_SEPARATOR}{billingStreet}{SEGMENT_TERMINATOR}");
        x12.Append($"N4{ELEMENT_SEPARATOR}{Value(practice.City)}{ELEMENT_SEPARATOR}{practice.State}{ELEMENT_SEPARATOR}{practice.ZipCode}{SEGMENT_TERMINATOR}");
        x12.Append($"REF{ELEMENT_SEPARATOR}EI{ELEMENT_SEPARATOR}{practice.TaxId}{SEGMENT_TERMINATOR}");

        // 2000B Loop - Subscriber Hierarchical Level
        x12.Append($"HL{ELEMENT_SEPARATOR}2{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}22{ELEMENT_SEPARATOR}0{SEGMENT_TERMINATOR}");
        x12.Append($"SBR{ELEMENT_SEPARATOR}P{ELEMENT_SEPARATOR}18{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}MB{SEGMENT_TERMINATOR}");

        // 2010BA Loop - Subscriber Name (Assuming patient is subscriber for now)
        var patientName = $"{fullClaim.Patient.LastName}{ELEMENT_SEPARATOR}{fullClaim.Patient.FirstName}";
        var memberId = fullClaim.PatientInsurance.MemberId ?? fullClaim.Patient.PatientId.ToString();
        x12.Append($"NM1{ELEMENT_SEPARATOR}IL{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{patientName}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}MI{ELEMENT_SEPARATOR}{memberId}{SEGMENT_TERMINATOR}");
        x12.Append($"N3{ELEMENT_SEPARATOR}{fullClaim.Patient.Address1 ?? "UNKNOWN"}{SEGMENT_TERMINATOR}");
        x12.Append($"N4{ELEMENT_SEPARATOR}{fullClaim.Patient.City ?? "UNKNOWN"}{ELEMENT_SEPARATOR}{fullClaim.Patient.State ?? "CA"}{ELEMENT_SEPARATOR}{fullClaim.Patient.ZipCode ?? "00000"}{SEGMENT_TERMINATOR}");
        x12.Append($"DMG{ELEMENT_SEPARATOR}D8{ELEMENT_SEPARATOR}{fullClaim.Patient.DateOfBirth:yyyyMMdd}{ELEMENT_SEPARATOR}{(fullClaim.Patient.Gender == "Male" ? "M" : "F")}{SEGMENT_TERMINATOR}");

        // 2010BB Loop - Payer Name
        x12.Append($"NM1{ELEMENT_SEPARATOR}PR{ELEMENT_SEPARATOR}2{ELEMENT_SEPARATOR}{fullClaim.PatientInsurance.InsurancePlan.PayerName}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}PI{ELEMENT_SEPARATOR}{receiverIdentifier}{SEGMENT_TERMINATOR}");

        // 2300 Loop - Claim Information
        var claimAmount = fullClaim.TotalChargeAmount.ToString("F2");
        var placeOfService = "11"; // Office
        x12.Append($"CLM{ELEMENT_SEPARATOR}{fullClaim.ClaimNumber}{ELEMENT_SEPARATOR}{claimAmount}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{placeOfService}:B:1{ELEMENT_SEPARATOR}Y{ELEMENT_SEPARATOR}A{ELEMENT_SEPARATOR}Y{ELEMENT_SEPARATOR}Y{SEGMENT_TERMINATOR}");
        var serviceDateTo = fullClaim.ServiceDateTo ?? fullClaim.ServiceDateFrom;
        x12.Append($"DTP{ELEMENT_SEPARATOR}472{ELEMENT_SEPARATOR}RD8{ELEMENT_SEPARATOR}{fullClaim.ServiceDateFrom:yyyyMMdd}-{serviceDateTo:yyyyMMdd}{SEGMENT_TERMINATOR}");

        // 2310B Loop - Rendering Provider (the dentist: individual NPI)
        var providerName = $"{Value(fullClaim.Provider.LastName)}{ELEMENT_SEPARATOR}{Value(fullClaim.Provider.FirstName)}";
        var providerNPI = fullClaim.Provider.NPI;
        x12.Append($"NM1{ELEMENT_SEPARATOR}82{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{providerName}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}XX{ELEMENT_SEPARATOR}{providerNPI}{SEGMENT_TERMINATOR}");

        // 2400 Loops - Service Lines
        int lineNumber = 1;
        foreach (var procedure in fullClaim.Procedures)
        {
            var procedureAmount = procedure.ChargeAmount.ToString("F2");
            x12.Append($"LX{ELEMENT_SEPARATOR}{lineNumber}{SEGMENT_TERMINATOR}");
            x12.Append($"SV3{ELEMENT_SEPARATOR}AD:{procedure.CDTCode}{ELEMENT_SEPARATOR}{procedureAmount}{ELEMENT_SEPARATOR}UN{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{ELEMENT_SEPARATOR}{procedure.ToothNumber ?? ""}{SEGMENT_TERMINATOR}");
            x12.Append($"DTP{ELEMENT_SEPARATOR}472{ELEMENT_SEPARATOR}D8{ELEMENT_SEPARATOR}{procedure.ServiceDate:yyyyMMdd}{SEGMENT_TERMINATOR}");
            lineNumber++;
        }

        // SE - Transaction Set Trailer: counts ST through SE itself, leaving out ISA and GS.
        var segmentCount = x12.ToString().Count(c => c == '~') - 1;
        x12.Append($"SE{ELEMENT_SEPARATOR}{segmentCount}{ELEMENT_SEPARATOR}{transactionControlNumber}{SEGMENT_TERMINATOR}");

        // GE - Functional Group Trailer
        x12.Append($"GE{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{groupControlNumber}{SEGMENT_TERMINATOR}");

        // IEA - Interchange Control Trailer
        x12.Append($"IEA{ELEMENT_SEPARATOR}1{ELEMENT_SEPARATOR}{interchangeControlNumber}{SEGMENT_TERMINATOR}");

        _logger.LogInformation("Generated 837D transaction for claim {ClaimNumber}, {SegmentCount} segments",
            fullClaim.ClaimNumber, segmentCount);

        return x12.ToString();
    }

    private string BuildISASegment(string senderId, string receiverId, string controlNumber)
    {
        var now = DateTime.Now;
        var date = now.ToString("yyMMdd");
        var time = now.ToString("HHmm");

        return $"ISA{ELEMENT_SEPARATOR}00{ELEMENT_SEPARATOR}          {ELEMENT_SEPARATOR}00{ELEMENT_SEPARATOR}          " +
               $"{ELEMENT_SEPARATOR}ZZ{ELEMENT_SEPARATOR}{senderId.PadRight(15)}" +
               $"{ELEMENT_SEPARATOR}ZZ{ELEMENT_SEPARATOR}{receiverId.PadRight(15)}" +
               $"{ELEMENT_SEPARATOR}{date}{ELEMENT_SEPARATOR}{time}" +
               $"{ELEMENT_SEPARATOR}^{ELEMENT_SEPARATOR}00501{ELEMENT_SEPARATOR}{controlNumber.PadLeft(9, '0')}" +
               $"{ELEMENT_SEPARATOR}0{ELEMENT_SEPARATOR}P{ELEMENT_SEPARATOR}:{SEGMENT_TERMINATOR}";
    }

    private string BuildGSSegment(string senderId, string receiverId, string controlNumber)
    {
        var now = DateTime.Now;
        var date = now.ToString("yyyyMMdd");
        var time = now.ToString("HHmm");

        return $"GS{ELEMENT_SEPARATOR}HC{ELEMENT_SEPARATOR}{senderId}{ELEMENT_SEPARATOR}{receiverId}" +
               $"{ELEMENT_SEPARATOR}{date}{ELEMENT_SEPARATOR}{time}" +
               $"{ELEMENT_SEPARATOR}{controlNumber}{ELEMENT_SEPARATOR}X{ELEMENT_SEPARATOR}005010X224A2{SEGMENT_TERMINATOR}";
    }

    /// <summary>Upper-cases a free-text element and drops the characters X12 uses as delimiters.</summary>
    private static string Value(string text) =>
        new(text.ToUpperInvariant().Where(c => c is not ('*' or '~' or ':' or '^' or '>')).ToArray());

    private string GenerateControlNumber()
    {
        // Generate a unique 9-digit control number
        return Random.Shared.Next(100000000, 999999999).ToString();
    }
}

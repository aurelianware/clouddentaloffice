namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// The practice as it appears on claims: the billing provider (837D loop 2010AA)
/// and the submitter contact (loop 1000A). One per practice. Claims aren't built
/// until every required field is filled in and valid.
/// </summary>
public class PracticeBillingProfile : ITenantEntity
{
    public int Id { get; set; }

    /// <summary>No default: always written for the signed-in practice.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The name the IRS has for the Tax ID, as payers have it on file.</summary>
    public string LegalName { get; set; } = string.Empty;

    /// <summary>The practice's organization (Type 2) NPI.</summary>
    public string Npi { get; set; } = string.Empty;

    /// <summary>The practice's EIN, nine digits with no dash.</summary>
    public string TaxId { get; set; } = string.Empty;

    /// <summary>Billing provider taxonomy, e.g. 1223G0001X (general practice dentistry).</summary>
    public string TaxonomyCode { get; set; } = PracticeBillingProfileRules.GeneralDentistTaxonomy;

    /// <summary>A street address: claims can't use a PO box for the billing provider.</summary>
    public string Address1 { get; set; } = string.Empty;
    public string? Address2 { get; set; }
    public string City { get; set; } = string.Empty;

    /// <summary>Two-letter state code.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Nine digits (ZIP+4), with no dash.</summary>
    public string ZipCode { get; set; } = string.Empty;

    /// <summary>Who payers call about a claim.</summary>
    public string ContactName { get; set; } = string.Empty;

    /// <summary>Ten digits, with no punctuation.</summary>
    public string Phone { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

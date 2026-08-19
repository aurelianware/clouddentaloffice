using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CloudDentalOffice.Portal.Models;

public enum PatientAccountStatus { Active, Inactive, Collections, Closed }

public enum PatientLedgerEntryType
{
    Charge,
    InsurancePayment,
    PatientPayment,
    ContractualAdjustment,
    WriteOff,
    Refund,
    Credit,
    DebitAdjustment,
    Transfer
}

public enum PatientLedgerSourceType
{
    Procedure,
    Encounter,
    Claim,
    Era,
    StaffAdjustment,
    PatientPayment,
    Refund,
    Transfer,
    SystemReversal
}

/// <summary>A validated currency amount. Ledger persistence uses its decimal amount and ISO currency.</summary>
public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency = "USD")
    {
        if (decimal.Round(amount, 2) != amount) throw new ArgumentOutOfRangeException(nameof(amount), "Money supports two fractional digits.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3 || !currency.Trim().All(char.IsLetter))
            throw new ArgumentException("Currency must be a three-letter ISO code.", nameof(currency));
        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }
}

[Table("PatientAccounts")]
public sealed class PatientAccount : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public int PatientId { get; set; }
    public PatientAccountStatus Status { get; set; } = PatientAccountStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ICollection<PatientLedgerEntry> LedgerEntries { get; set; } = [];
}

[Table("PatientLedgerEntries")]
public sealed class PatientLedgerEntry : ITenantEntity
{
    [Key] public Guid LedgerEntryId { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PatientAccountId { get; set; }
    public PatientLedgerEntryType EntryType { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    public DateTime EffectiveDate { get; set; }
    public PatientLedgerSourceType SourceType { get; set; }
    [Required, MaxLength(128)] public string SourceId { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string DescriptionCode { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    [Required, MaxLength(100)] public string CreatedBy { get; set; } = string.Empty;
    public Guid? ReversalOfEntryId { get; set; }
    public PatientAccount PatientAccount { get; set; } = null!;
    public PatientLedgerEntry? ReversalOfEntry { get; set; }

    [NotMapped] public Money Money => new(Amount, Currency);
}

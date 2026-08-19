using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CloudDentalOffice.Portal.Models;

public enum PatientStatementStatus { Draft, Ready, Sent, PartiallyPaid, Paid, Superseded, Voided }

[Table("PatientStatements")]
public sealed class PatientStatement : ITenantEntity
{
    [Key] public Guid StatementId { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PatientAccountId { get; set; }
    public DateTime StatementDate { get; set; }
    public DateTime DueDate { get; set; }
    public PatientStatementStatus Status { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal BalanceForward { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal NewCharges { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal InsurancePayments { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Adjustments { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal PatientPayments { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Credits { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Refunds { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal DebitAdjustments { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal AmountDue { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    /// <summary>Inclusive ledger CreatedAt cutoff captured by this snapshot.</summary>
    public DateTime LedgerThroughDate { get; set; }
    public DateTime CreatedAt { get; set; }
    [Required, MaxLength(100)] public string CreatedBy { get; set; } = string.Empty;
    public DateTime StatusUpdatedAt { get; set; }
    public Guid? SupersedesStatementId { get; set; }
    public Guid? SupersededByStatementId { get; set; }
    public DateTime? VoidedAt { get; set; }
    [MaxLength(64)] public string? VoidReasonCode { get; set; }
    public PatientAccount PatientAccount { get; set; } = null!;
    public ICollection<PatientStatementLine> Lines { get; set; } = [];
}

[Table("PatientStatementLines")]
public sealed class PatientStatementLine : ITenantEntity
{
    [Key] public Guid StatementLineId { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid StatementId { get; set; }
    public Guid LedgerEntryId { get; set; }
    public DateTime ActivityDate { get; set; }
    public PatientLedgerEntryType EntryType { get; set; }
    [Required, MaxLength(80)] public string PatientDescription { get; set; } = string.Empty;
    /// <summary>Signed effect on amount due, snapshotted from the ledger.</summary>
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    public PatientStatement Statement { get; set; } = null!;
}

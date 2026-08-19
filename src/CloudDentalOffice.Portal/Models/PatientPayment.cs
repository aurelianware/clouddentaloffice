using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CloudDentalOffice.Portal.Models;

public enum PaymentProcessorProvider { Stripe, Office, External }
public enum PaymentProcessorEnvironment { Sandbox, Production }
public enum PaymentStatus { Pending, Succeeded, Failed, Cancelled }
public enum PatientPaymentMethod { Card, BankAccount, DigitalWallet, Cash, Check, External, Other }
public enum PaymentProcessorEventStatus { Received, Processed, Failed, Conflict }
public enum PaymentProcessorOnboardingStatus { NotStarted, Pending, Enabled, Restricted, Disabled }
public enum PatientPaymentSelection { FullBalance, StatementBalance, Partial }
public enum PatientPaymentAttemptStatus { Pending, SessionCreated, Failed, Completed, Cancelled, ReviewRequired }

[Table("PatientPayments")]
public sealed class PatientPayment : ITenantEntity
{
    [Key] public Guid PaymentId { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PatientAccountId { get; set; }
    public Guid? StatementId { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    public DateTime PaymentDate { get; set; }
    public PatientPaymentMethod Method { get; set; }
    public PaymentProcessorProvider Processor { get; set; }
    [MaxLength(128)] public string? ExternalSessionId { get; set; }
    [MaxLength(128)] public string? ExternalPaymentId { get; set; }
    [Required, MaxLength(128)] public string InternalPaymentReference { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public Guid? LedgerEntryId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    [Required, MaxLength(100)] public string CreatedBy { get; set; } = "system";
    public Guid? ReversalLedgerEntryId { get; set; }
    public DateTime? ReversedAt { get; set; }
    [MaxLength(100)] public string? ReversedBy { get; set; }
    public PatientAccount PatientAccount { get; set; } = null!;
    public ICollection<PatientPaymentAllocation> Allocations { get; set; } = [];
}

[Table("PatientPaymentAllocations")]
public sealed class PatientPaymentAllocation : ITenantEntity
{
    [Key] public Guid PaymentAllocationId { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PaymentId { get; set; }
    public Guid LedgerEntryId { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    public DateTime CreatedAt { get; set; }
    [Required, MaxLength(100)] public string CreatedBy { get; set; } = string.Empty;
    public DateTime? UnappliedAt { get; set; }
    [MaxLength(100)] public string? UnappliedBy { get; set; }
    [MaxLength(64)] public string? UnapplyReasonCode { get; set; }
    public PatientPayment Payment { get; set; } = null!;
}

[Table("FinancialAuditEvents")]
public sealed class FinancialAuditEvent : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string Action { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string EntityType { get; set; } = string.Empty;
    [Required, MaxLength(128)] public string EntityId { get; set; } = string.Empty;
    [Required, MaxLength(100)] public string Actor { get; set; } = string.Empty;
    [MaxLength(64)] public string? ReasonCode { get; set; }
    public DateTime CreatedAt { get; set; }
}

[Table("PaymentProcessorConfigurations")]
public sealed class PaymentProcessorConfiguration : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public PaymentProcessorProvider Provider { get; set; }
    public bool Enabled { get; set; }
    public PaymentProcessorEnvironment Environment { get; set; }
    [MaxLength(256)] public string? CredentialReference { get; set; }
    [MaxLength(128)] public string? ConnectedMerchantReference { get; set; }
    public PaymentProcessorOnboardingStatus OnboardingStatus { get; set; }
    public bool ChargesEnabled { get; set; }
    public bool PayoutsEnabled { get; set; }
    public bool DetailsSubmitted { get; set; }
    [MaxLength(128)] public string? LastStatusCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

[Table("PaymentProcessorEvents")]
public sealed class PaymentProcessorEvent : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public PaymentProcessorProvider Processor { get; set; }
    [Required, MaxLength(128)] public string ExternalEventId { get; set; } = string.Empty;
    [MaxLength(128)] public string? ExternalPaymentId { get; set; }
    public Guid? PaymentId { get; set; }
    public PaymentProcessorEventStatus Status { get; set; }
    [MaxLength(64)] public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

[Table("PatientPaymentAttempts")]
public sealed class PatientPaymentAttempt : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public Guid PatientAccountId { get; set; }
    public Guid? StatementId { get; set; }
    public Guid? PaymentId { get; set; }
    public PatientPaymentSelection Selection { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Amount { get; set; }
    [Required, MaxLength(3)] public string Currency { get; set; } = "USD";
    [Required, MaxLength(128)] public string PaymentReference { get; set; } = string.Empty;
    public PatientPaymentAttemptStatus Status { get; set; }
    [MaxLength(128)] public string? StripeCheckoutSessionId { get; set; }
    [MaxLength(128)] public string? StripePaymentIntentId { get; set; }
    [MaxLength(128)] public string? ConnectedAccountId { get; set; }
    [MaxLength(64)] public string? FailureCode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

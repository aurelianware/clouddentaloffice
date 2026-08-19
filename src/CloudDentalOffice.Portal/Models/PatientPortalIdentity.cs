using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CloudDentalOffice.Portal.Models;

[Table("PatientPortalIdentities")]
public sealed class PatientPortalIdentity : ITenantEntity
{
    [Key] public Guid Id { get; set; }
    [Required, MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    public int PatientId { get; set; }
    [Required, MaxLength(500)] public string Issuer { get; set; } = string.Empty;
    [Required, MaxLength(500)] public string Subject { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

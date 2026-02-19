using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CloudDentalOffice.Portal.Models;

public class User : ITenantEntity
{
    [Key]
    public int Id { get; set; }

    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Foreign key to Organization
    /// </summary>
    public int? OrganizationId { get; set; }

    /// <summary>
    /// Azure AD Object ID (unique identifier from Azure AD)
    /// </summary>
    public string? AzureAdObjectId { get; set; }

    /// <summary>
    /// Azure AD UPN (User Principal Name)
    /// </summary>
    public string? AzureAdUpn { get; set; }

    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Password hash for local authentication (fallback)
    /// Null if user only uses Azure AD
    /// </summary>
    public string? PasswordHash { get; set; }

    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    
    public string Role { get; set; } = "Staff"; // Admin, Dentist, Staff

    /// <summary>
    /// Whether this user can invite other users to the organization
    /// </summary>
    public bool CanInviteUsers { get; set; } = false;

    /// <summary>
    /// Last login timestamp
    /// </summary>
    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// User creation timestamp
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the user account is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    // Navigation properties
    [ForeignKey("OrganizationId")]
    public virtual Organization? Organization { get; set; }
}

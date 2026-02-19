using System.ComponentModel.DataAnnotations;

namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// Represents an organization (dental practice) in the system.
/// Maps to Azure AD tenant for authentication.
/// </summary>
public class Organization
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Our internal tenant identifier
    /// </summary>
    [Required]
    public string TenantId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Azure AD Tenant ID (Directory ID)
    /// </summary>
    public string? AzureAdTenantId { get; set; }

    /// <summary>
    /// Organization name (e.g., "Smile Dental Clinic")
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Domain for the organization (e.g., "smiledental.com")
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Subscription plan (hobby, pro, enterprise)
    /// </summary>
    [Required]
    public string Plan { get; set; } = "trial";

    /// <summary>
    /// Stripe customer ID for billing
    /// </summary>
    public string? StripeCustomerId { get; set; }

    /// <summary>
    /// Stripe subscription ID
    /// </summary>
    public string? StripeSubscriptionId { get; set; }

    /// <summary>
    /// Whether the organization is active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Date the organization was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Trial expiration date (if applicable)
    /// </summary>
    public DateTime? TrialExpiresAt { get; set; }

    /// <summary>
    /// Organization settings as JSON
    /// </summary>
    public string? Settings { get; set; }

    // Navigation properties
    public virtual ICollection<User> Users { get; set; } = new List<User>();
}

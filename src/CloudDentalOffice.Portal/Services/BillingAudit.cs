using System.Security.Claims;
using CloudDentalOffice.Portal.Data;
using CloudDentalOffice.Portal.Models;

namespace CloudDentalOffice.Portal.Services;

/// <summary>Financial audit trail entries for billing actions taken outside <see cref="StaffPatientBillingService"/>.</summary>
public static class BillingAudit
{
    /// <summary>The authenticated actor recorded on audit entries, or null when the caller has no bounded identity.</summary>
    public static string? Actor(ClaimsPrincipal user)
    {
        var actor = user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
            user.FindFirst("oid")?.Value ?? user.Identity?.Name;
        return string.IsNullOrWhiteSpace(actor) || actor.Trim().Length > 100 ? null : actor.Trim();
    }

    public static void Add(CloudDentalDbContext db, string tenantId, string actor, string action, string entityType,
        string entityId, string? reasonCode, DateTime now) =>
        db.FinancialAuditEvents.Add(new FinancialAuditEvent
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Action = action, EntityType = entityType, EntityId = entityId,
            Actor = actor, ReasonCode = reasonCode?.Trim() is { Length: > 0 } reason ? reason[..Math.Min(reason.Length, 64)] : null,
            CreatedAt = now
        });
}

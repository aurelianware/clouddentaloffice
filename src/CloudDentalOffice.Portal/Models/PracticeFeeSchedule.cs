using System.ComponentModel.DataAnnotations;

namespace CloudDentalOffice.Portal.Models;

/// <summary>A practice-owned version of an exported fee schedule, never a global price list.</summary>
public sealed class PracticeFeeSchedule : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    public bool IsPracticeDefault { get; set; }
    public string EntriesJson { get; set; } = "[]";
    public DateTime ImportedAt { get; set; }
}

public sealed record FeeScheduleEntry(string Code, string Description, decimal Fee,
    bool NoWriteOff, bool PaidInFull, bool Active);
public sealed record FeeSchedulePreview(string Name, IReadOnlyList<FeeScheduleEntry> Entries);

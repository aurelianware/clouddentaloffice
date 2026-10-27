using System.ComponentModel.DataAnnotations;

namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// Remembers which CDO record an external practice-management record became,
/// so a repeat import updates that record instead of creating a duplicate.
/// </summary>
public sealed class ImportedRecordLink : ITenantEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(32)] public string Source { get; set; } = string.Empty;
    [MaxLength(32)] public string EntityType { get; set; } = string.Empty;
    [MaxLength(64)] public string ExternalId { get; set; } = string.Empty;
    public int LocalId { get; set; }
    public DateTime ImportedAt { get; set; }
}

public static class ImportSources
{
    public const string Dentrix = "Dentrix";
}

public static class ImportedEntityTypes
{
    public const string Patient = "Patient";
}

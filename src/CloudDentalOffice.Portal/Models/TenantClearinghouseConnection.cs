namespace CloudDentalOffice.Portal.Models;

/// <summary>
/// A practice's connection to a clearinghouse (today only Stedi). Holds the
/// Key Vault secret NAME for the practice's API key, never the key itself.
/// One row per tenant and provider.
/// </summary>
public class TenantClearinghouseConnection : ITenantEntity
{
    public const string StediProvider = "Stedi";

    public int Id { get; set; }

    /// <summary>No default: a row must always be written for an explicit tenant.</summary>
    public string TenantId { get; set; } = string.Empty;

    public string Provider { get; set; } = StediProvider;

    public ClearinghouseConnectionMode Mode { get; set; } = ClearinghouseConnectionMode.Integrated;

    public ClearinghouseConnectionStatus Status { get; set; } = ClearinghouseConnectionStatus.Pending;

    /// <summary>The practice's Stedi account or organization identifier, when known. Not a secret.</summary>
    public string? StediAccountId { get; set; }

    /// <summary>
    /// Key Vault secret name holding the practice's Stedi API key. Required for
    /// <see cref="ClearinghouseConnectionMode.Integrated"/> and must be
    /// <c>stedi-apikey-{TenantId}</c>; unused for Shared mode.
    /// </summary>
    public string? KeyReference { get; set; }

    /// <summary>Which path this practice's eligibility checks take.</summary>
    public EligibilityGatewayKind EligibilityGateway { get; set; } = EligibilityGatewayKind.CloudHealthOffice;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set when the key in Key Vault is replaced; part of the credential cache key.</summary>
    public DateTimeOffset? RotatedAt { get; set; }
}

public enum ClearinghouseConnectionMode
{
    /// <summary>The practice's own production Stedi account (published-app install).</summary>
    Integrated,

    /// <summary>Aurelianware's Stedi account. Pilot only; requires the shared-account feature flag.</summary>
    Shared
}

public enum ClearinghouseConnectionStatus { Pending, Active, Suspended }

public enum EligibilityGatewayKind
{
    /// <summary>CDO → CloudHealthOffice provider eligibility API → CHO's clearinghouse account.</summary>
    CloudHealthOffice,

    /// <summary>CDO → Stedi directly, with the practice's own (or the flagged shared) key.</summary>
    Stedi
}

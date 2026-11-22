namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Operator limits of the time-bound individual grant policy, bound to <c>Gateway:Governance:IndividualGrants</c>.
/// There are no compiled-in ceilings: a per-class value is an optional operator limit (<c>null</c> means no class-specific
/// limit) and must be at least 1 day when set. The overall maximum validity of a grant always comes from the required
/// <c>Gateway:Governance:Grants:MaxDuration</c> setting, so the effective maximum of a class is the minimum over the
/// configured limits only.
/// </summary>
public sealed class IndividualGrantOptions
{
    public const string SectionName = "Gateway:Governance:IndividualGrants";

    /// <summary>Regular data (no sensitive columns, no PII tag). <c>null</c> = no class-specific limit.</summary>
    [Range(1, int.MaxValue)]
    public int? RegularMaxDays { get; set; }

    /// <summary>Sensitive / PII tables, or clear text on a sensitive column. <c>null</c> = no class-specific limit.</summary>
    [Range(1, int.MaxValue)]
    public int? SensitiveMaxDays { get; set; }

    /// <summary>Individual <c>Unmasked</c> access profile. <c>null</c> = no class-specific limit.</summary>
    [Range(1, int.MaxValue)]
    public int? UnmaskedMaxDays { get; set; }

    /// <summary>Individual privileged functional right (just-in-time elevation). <c>null</c> = no class-specific limit.</summary>
    [Range(1, int.MaxValue)]
    public int? PrivilegedMaxDays { get; set; }

    /// <summary>Regular data for customer-realm users. <c>null</c> = no class-specific limit.</summary>
    [Range(1, int.MaxValue)]
    public int? CustomerRegularMaxDays { get; set; }

    /// <summary>Customer realms: individual <c>Unmasked</c> access is disabled unless explicitly enabled.</summary>
    public bool AllowCustomerUnmasked { get; set; }

    /// <summary>Customer realms: individual privileged functional rights are disabled unless explicitly enabled.</summary>
    public bool AllowCustomerPrivileged { get; set; }

    /// <summary>After this many renewals the approver is asked to convert the grant into a role/group mapping.</summary>
    [Range(1, 10)]
    public int RoleConversionHintAfterRenewals { get; set; } = 2;

    /// <summary>From this many previous renewals onwards a campaign decision is required instead of a direct extension.</summary>
    [Range(1, 10)]
    public int CampaignRequiredAfterRenewals { get; set; } = 3;
}

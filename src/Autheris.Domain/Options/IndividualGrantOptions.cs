namespace Autheris.Domain.Options;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// WP-SC-16: realm profile of the time-bound individual grant policy, bound to <c>Gateway:Governance:IndividualGrants</c>.
/// Every value can only SHORTEN the built-in ceiling (<see cref="IndividualGrantCeilings"/>); the effective value is always
/// <c>min(configured, ceiling)</c> and a value above the ceiling is rejected at startup.
/// </summary>
public sealed class IndividualGrantOptions
{
    public const string SectionName = "Gateway:Governance:IndividualGrants";

    /// <summary>Regular data (no sensitive columns, no PII tag).</summary>
    [Range(1, IndividualGrantCeilings.RegularDays)]
    public int RegularMaxDays { get; set; } = IndividualGrantCeilings.RegularDays;

    /// <summary>Sensitive / PII tables, or clear text on a sensitive column.</summary>
    [Range(1, IndividualGrantCeilings.SensitiveDays)]
    public int SensitiveMaxDays { get; set; } = IndividualGrantCeilings.SensitiveDays;

    /// <summary>Individual <c>Unmasked</c> access profile.</summary>
    [Range(1, IndividualGrantCeilings.UnmaskedDays)]
    public int UnmaskedMaxDays { get; set; } = IndividualGrantCeilings.UnmaskedDays;

    /// <summary>Individual privileged functional right (just-in-time elevation).</summary>
    [Range(1, IndividualGrantCeilings.PrivilegedDays)]
    public int PrivilegedMaxDays { get; set; } = IndividualGrantCeilings.PrivilegedDays;

    /// <summary>Break-glass exception.</summary>
    [Range(1, IndividualGrantCeilings.BreakGlassHours)]
    public int BreakGlassMaxHours { get; set; } = IndividualGrantCeilings.BreakGlassHours;

    /// <summary>Service principal (direct grantee).</summary>
    [Range(1, IndividualGrantCeilings.ServicePrincipalDays)]
    public int ServicePrincipalMaxDays { get; set; } = IndividualGrantCeilings.ServicePrincipalDays;

    /// <summary>Regular data for customer-realm users.</summary>
    [Range(1, IndividualGrantCeilings.CustomerRegularDays)]
    public int CustomerRegularMaxDays { get; set; } = IndividualGrantCeilings.CustomerRegularDays;

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

/// <summary>Built-in maxima (best-practice defaults); realm profiles can only shorten them.</summary>
public static class IndividualGrantCeilings
{
    public const int RegularDays = 180;
    public const int SensitiveDays = 90;
    public const int UnmaskedDays = 30;
    public const int PrivilegedDays = 7;
    public const int BreakGlassHours = 72;
    public const int ServicePrincipalDays = 365;
    public const int CustomerRegularDays = 90;
}

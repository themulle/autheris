namespace Autheris.Domain.Options.Security;

using System.Collections.Generic;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

public sealed class IdentityRealmsOptions
{
    public const string SectionName = "Authentication:Realms";

    public Dictionary<string, IdentityRealmConfigOptions> Realms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? DefaultRealm { get; set; }

    public ScimOptions Scim { get; set; } = new();
}

public sealed class IdentityRealmConfigOptions
{
    public IdentityRealmKind Kind { get; set; } = IdentityRealmKind.EntraWorkforce;
    public List<string> Issuers { get; set; } = [];
    public string? MetadataAddress { get; set; }
    public List<string> Audiences { get; set; } = [];
    public List<string>? AllowedClientIds { get; set; }
    public string UserKeyClaim { get; set; } = "oid";
    public string UpnClaim { get; set; } = "upn";
    public string? FixedTenantId { get; set; }
    public string? TenantClaimName { get; set; }
    public Dictionary<string, string>? TenantClaimMapping { get; set; }
    public RealmGroupSource GroupSource { get; set; } = RealmGroupSource.Resolver;
    public List<string> ResolverChain { get; set; } = ["scim-store"];
    public RealmProvisioningMode Provisioning { get; set; } = RealmProvisioningMode.Scim;
    public ScimRealmSecretOptions? Scim { get; set; }
    public List<string> AllowedGatewayRoles { get; set; } = [];
    public bool Delegable { get; set; }
}

public sealed class ScimRealmSecretOptions
{
    public const int MinMaxSyncAgeMinutes = 10;
    public const int MaxMaxSyncAgeMinutes = 10080;
    public const int DefaultMaxSyncAgeMinutes = 180;

    /// <summary>Primary bearer secret for inbound SCIM 2.0 provisioning requests to this realm (minimum 32 characters).</summary>
    public string? PrimarySecret { get; set; }

    /// <summary>Optional secondary bearer secret accepted during zero-downtime SCIM token rotation.</summary>
    public string? SecondarySecret { get; set; }

    /// <summary>Whether SCIM identity correlation may fall back to matching UPN or userName when stronger keys are absent. Default false.</summary>
    public bool AllowUpnCorrelation { get; set; }

    /// <summary>Whether an authenticated caller in a SCIM-provisioned realm must match a provisioned SCIM user. Default true.</summary>
    public bool RequireCorrelation { get; set; } = true;

    /// <summary>Maximum age in minutes since the last authenticated SCIM contact before the realm's SCIM sync is considered stale (10..10080, default 180).</summary>
    public int MaxSyncAgeMinutes { get; set; } = DefaultMaxSyncAgeMinutes;

    /// <summary>Behavior when SCIM sync is older than MaxSyncAgeMinutes: Degrade (default, denies data access) or DropScimGroups (uses token groups only when no SCIM-only group carries a deny).</summary>
    public ScimStaleSyncBehavior StaleSyncBehavior { get; set; } = ScimStaleSyncBehavior.Degrade;

    /// <summary>Optional per-realm rate limit override in requests per minute for inbound SCIM calls (1..60000). When null, Authentication:Realms:Scim:RequestsPerMinute applies.</summary>
    public int? RequestsPerMinute { get; set; }
}

public sealed class ScimOptions
{
    public const int DefaultRequestsPerMinute = 600;
    public const int MinRequestsPerMinute = 1;
    public const int MaxRequestsPerMinute = 60_000;

    /// <summary>CIDR ranges or IP addresses permitted to call SCIM 2.0 endpoints. Mandatory outside Development when any realm uses SCIM provisioning.</summary>
    public List<string> AllowedIpRanges { get; set; } = [];

    /// <summary>Default per-realm rate limit in requests per minute for inbound SCIM calls (1..60000, default 600).</summary>
    public int RequestsPerMinute { get; set; } = DefaultRequestsPerMinute;

    /// <summary>Authentication:Realms:Scim:MassChangeProtection. Throttle on SCIM-originated user deprovisioning (06-FU-02).</summary>
    public ScimMassChangeProtectionOptions MassChangeProtection { get; set; } = new();
}

/// <summary>
/// Rate guard against a compromised or buggy IdP mass-deactivating users through SCIM. Counts distinct users locked by SCIM per
/// tenant and realm in a sliding window (shared lock table, cluster-wide); beyond the limit SCIM deprovisioning answers 429.
/// Admin and manual locks are never counted or blocked. <c>Enabled=false</c> is an explicit opt-out.
/// </summary>
public sealed class ScimMassChangeProtectionOptions
{
    public const int MaxOperationsLowerBound = 1;
    public const int MaxOperationsUpperBound = 100_000;
    public const int WindowMinutesLowerBound = 1;
    public const int WindowMinutesUpperBound = 1440;

    public bool Enabled { get; set; } = true;
    public int MaxDestructiveOperations { get; set; } = 100;
    public int WindowMinutes { get; set; } = 60;
}

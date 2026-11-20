namespace Autheris.Domain.Options.Security;

using System.Collections.Generic;
using Autheris.Domain.Identity;
using Autheris.Domain.Security;

/// <summary>Multi-realm identity provider and SCIM provisioning configuration (<c>Authentication:Realms</c>).</summary>
public sealed class IdentityRealmsOptions
{
    public const string SectionName = "Authentication:Realms";

    /// <summary>Named identity realm configurations keyed by realm identifier.</summary>
    public Dictionary<string, IdentityRealmConfigOptions> Realms { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional default realm name used when a request does not specify a realm.</summary>
    public string? DefaultRealm { get; set; }

    /// <summary>Global SCIM 2.0 provisioning rate-limit and IP-allowlist settings (<c>Authentication:Realms:Scim</c>).</summary>
    public ScimOptions Scim { get; set; } = new();
}

/// <summary>Configuration of a single identity realm.</summary>
public sealed class IdentityRealmConfigOptions
{
    /// <summary>Identity provider kind for this realm (EntraWorkforce, EntraExternalId, GenericOidc, etc.).</summary>
    public IdentityRealmKind Kind { get; set; } = IdentityRealmKind.EntraWorkforce;

    /// <summary>Accepted OIDC token issuer URIs for this realm.</summary>
    public List<string> Issuers { get; set; } = [];

    /// <summary>OIDC discovery metadata address for fetching signing keys.</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>Accepted JWT audience values for tokens issued by this realm.</summary>
    public List<string> Audiences { get; set; } = [];

    /// <summary>Optional allowlist of OAuth client IDs (<c>azp</c>/<c>appid</c>) permitted to authenticate in this realm.</summary>
    public List<string>? AllowedClientIds { get; set; }

    /// <summary>JWT claim name used as the primary user key (default <c>oid</c>).</summary>
    public string UserKeyClaim { get; set; } = "oid";

    /// <summary>JWT claim name used to extract the user principal name (default <c>upn</c>).</summary>
    public string UpnClaim { get; set; } = "upn";

    /// <summary>Optional fixed tenant ID assigned to all principals authenticating through this realm.</summary>
    public string? FixedTenantId { get; set; }

    /// <summary>Optional JWT claim name from which the caller's tenant ID is extracted.</summary>
    public string? TenantClaimName { get; set; }

    /// <summary>Optional mapping from raw tenant claim values to canonical gateway tenant IDs.</summary>
    public Dictionary<string, string>? TenantClaimMapping { get; set; }

    /// <summary>Source used to resolve group memberships for callers in this realm.</summary>
    public RealmGroupSource GroupSource { get; set; } = RealmGroupSource.Resolver;

    /// <summary>Ordered list of group resolver identifiers consulted when <see cref="GroupSource"/> uses resolvers.</summary>
    public List<string> ResolverChain { get; set; } = ["scim-store"];

    /// <summary>User and group provisioning mode for this realm.</summary>
    public RealmProvisioningMode Provisioning { get; set; } = RealmProvisioningMode.Scim;

    /// <summary>Per-realm SCIM 2.0 bearer secrets and correlation settings.</summary>
    public ScimRealmSecretOptions? Scim { get; set; }

    /// <summary>Canonical gateway roles that callers in this realm are permitted to hold.</summary>
    public List<string> AllowedGatewayRoles { get; set; } = [];

    /// <summary>When true, principals in this realm may participate in on-behalf-of delegation.</summary>
    public bool Delegable { get; set; }

    /// <summary>Exact OIDC subjects of workload identities and the gateway roles each one receives (GenericOidc realms only; configuration is the only way to define a mapping).</summary>
    public List<SubjectRoleMapping> SubjectRoles { get; set; } = [];

    /// <summary>Whether a subject mapping may grant ClusterAdmin. Default false; ClusterAdmin must also be listed in AllowedGatewayRoles and the realm needs a FixedTenantId.</summary>
    public bool AllowPrivilegedSubjectRoles { get; set; }

    /// <summary>Static signing keys of the realm read from a local JWKS file (mutually exclusive with MetadataAddress). Null when keys come from MetadataAddress.</summary>
    public StaticJwksSource? Jwks { get; set; }

    /// <summary>Delegated scopes (<c>scp</c> or RFC 9068 <c>scope</c>) that only permit reading on GenericOidc realms. Null applies the default (Agent.Read, App.Read); an empty list disables scope-based read-only marking and logs a startup warning.</summary>
    public List<string>? ReadOnlyScopes { get; set; }

    public static readonly IReadOnlyList<string> DefaultReadOnlyScopes = ["Agent.Read", "App.Read"];

    /// <summary>Configured <see cref="ReadOnlyScopes"/> or the default list (Agent.Read, App.Read).</summary>
    public IReadOnlyList<string> GetEffectiveReadOnlyScopes() => ReadOnlyScopes ?? DefaultReadOnlyScopes;
}

/// <summary>One workload subject and its gateway roles (list entry, because subjects contain colons and cannot be configuration keys).</summary>
public sealed class SubjectRoleMapping
{
    /// <summary>The exact value of the token's single sub claim (ordinal, case-sensitive, no pattern).</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Canonical GatewayRole names granted to the subject, a subset of the realm's AllowedGatewayRoles.</summary>
    public List<string> Roles { get; set; } = [];
}

/// <summary>Static JWKS key source of a realm (file only).</summary>
public sealed class StaticJwksSource
{
    public const int DefaultReloadIntervalMinutes = 5;
    public const int MinReloadIntervalMinutes = 1;
    public const int MaxReloadIntervalMinutes = 60;

    /// <summary>Absolute path of the JWKS file (at most 256 KiB, at most 16 public keys).</summary>
    public string? File { get; set; }

    /// <summary>Minutes between re-reads of the file (1..60, default 5). A failed re-read keeps the last good key set.</summary>
    public int ReloadIntervalMinutes { get; set; } = DefaultReloadIntervalMinutes;
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

    /// <summary>Enables SCIM mass-deprovisioning protection (set false only as an explicit opt-out).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum number of destructive SCIM user-lock operations allowed within <see cref="WindowMinutes"/>.</summary>
    public int MaxDestructiveOperations { get; set; } = 100;

    /// <summary>Sliding window duration in minutes over which destructive SCIM operations are counted.</summary>
    public int WindowMinutes { get; set; } = 60;
}

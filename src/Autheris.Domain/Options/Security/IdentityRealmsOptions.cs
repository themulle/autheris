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
    public string? PrimarySecret { get; set; }
    public string? SecondarySecret { get; set; }
}

public sealed class ScimOptions
{
    public List<string> AllowedIpRanges { get; set; } = [];

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

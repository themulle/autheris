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
}

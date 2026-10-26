namespace Autheris.Domain.Identity;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using Autheris.Domain.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Security;

public enum IdentityRealmKind
{
    EntraWorkforce = 1,
    EntraExternalId = 2,
    AzureAdB2C = 3,
    GenericOidc = 4,
    Adfs = 5,
    Kerberos = 6,
    ForwardAuth = 7,
    Basic = 8,
    Pat = 9,
    Customer = 10
}

public enum RealmGroupSource
{
    Resolver = 1, // Authoritative Role-Resolver pipeline; token groups dropped
    Token = 2,    // Inbound JWT 'groups' claim (legacy/explicit only)
    None = 3      // No groups recognized
}

public enum RealmProvisioningMode
{
    Scim = 1,     // Inbound push via SCIM 2.0
    Jit = 2,      // Just-in-time creation upon initial authenticated request
    None = 3      // Pre-seeded or external only
}

public enum TenantBindingKind
{
    Fixed = 1,
    ClaimMapped = 2,

    /// <summary>DT-A-5: the realm names no tenant; token tenant claims are ignored and the configured default tenant applies.</summary>
    Unbound = 3
}

public sealed record TenantBindingSpecification(
    TenantBindingKind Kind,
    TenantId? FixedTenantId = null,
    string? ClaimName = null,
    IReadOnlyDictionary<string, TenantId>? Mapping = null)
{
    public static TenantBindingSpecification Fixed(TenantId tenantId) =>
        new(TenantBindingKind.Fixed, FixedTenantId: tenantId);

    public static TenantBindingSpecification ClaimMapped(string claimName, IReadOnlyDictionary<string, TenantId> mapping) =>
        new(TenantBindingKind.ClaimMapped, ClaimName: claimName, Mapping: mapping);

    public static TenantBindingSpecification Unbound() => new(TenantBindingKind.Unbound);

    /// <summary>
    /// DT-A-5: the realm's tenant statement as a <see cref="ClaimTenant"/>. <c>Unbound</c> and a ClaimMapped realm whose claim is absent
    /// say nothing (Absent: the default tenant applies); a present but unmapped claim, a reserved tenant and a malformed binding are
    /// Invalid (fail closed, no value echo). Additive: <see cref="ResolveTenant"/> is unchanged until the ingress cutover.
    /// </summary>
    public ClaimTenant ResolveClaim(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        switch (Kind)
        {
            case TenantBindingKind.Unbound:
                return ClaimTenant.Absent;
            case TenantBindingKind.Fixed:
                return FixedTenantId is { } fixedTenant && !TenantId.IsReserved(fixedTenant)
                    ? ClaimTenant.Valid(fixedTenant)
                    : ClaimTenant.Invalid;
            case TenantBindingKind.ClaimMapped when !string.IsNullOrWhiteSpace(ClaimName) && Mapping is not null:
                var raw = principal.FindFirst(ClaimName)?.Value;
                if (raw is null)
                {
                    return ClaimTenant.Absent;
                }

                return Mapping.TryGetValue(raw, out var mapped) && mapped.Value is not null && !TenantId.IsReserved(mapped)
                    ? ClaimTenant.Valid(mapped)
                    : ClaimTenant.Invalid;
            default:
                return ClaimTenant.Invalid;
        }
    }

    public TenantId ResolveTenant(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (Kind == TenantBindingKind.Fixed && FixedTenantId.HasValue)
        {
            return FixedTenantId.Value;
        }

        if (Kind == TenantBindingKind.ClaimMapped && !string.IsNullOrWhiteSpace(ClaimName) && Mapping != null)
        {
            var val = principal.FindFirst(ClaimName)?.Value;
            if (val != null && Mapping.TryGetValue(val, out var matchedTenant))
            {
                return matchedTenant;
            }
            throw new SecurityException($"Claim '{ClaimName}' value '{val}' not authorized in realm tenant mapping.");
        }

        return TenantId.LegacySingleTenant;
    }
}

public sealed record IdentityRealm(
    string Id,
    IdentityRealmKind Kind,
    IReadOnlyList<string> Issuers,
    string? MetadataAddress,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string>? AllowedClientIds,
    string UserKeyClaim,
    string UpnClaim,
    TenantBindingSpecification TenantBinding,
    RealmGroupSource GroupSource,
    IReadOnlyList<string> ResolverChain,
    RealmProvisioningMode Provisioning,
    IReadOnlySet<GatewayRole> AllowedGatewayRoles,
    bool Delegable = false,
    bool IsImplicitLegacy = false
)
{
    public string RealmId => Id;

    public IdentityRealm(
        string id,
        IdentityRealmKind kind,
        TenantBindingSpecification tenantBinding,
        RealmGroupSource groupSource = RealmGroupSource.Resolver,
        RealmProvisioningMode provisioning = RealmProvisioningMode.Scim,
        IReadOnlyList<string>? issuers = null,
        string? metadataAddress = null,
        IReadOnlyList<string>? audiences = null,
        IReadOnlyList<string>? allowedClientIds = null,
        string userKeyClaim = "oid",
        string upnClaim = "upn",
        IReadOnlyList<string>? resolverChain = null,
        IReadOnlySet<GatewayRole>? allowedGatewayRoles = null,
        bool delegable = false,
        bool isImplicitLegacy = false)
        : this(
            id,
            kind,
            issuers ?? [],
            metadataAddress,
            audiences ?? [],
            allowedClientIds,
            userKeyClaim,
            upnClaim,
            tenantBinding,
            groupSource,
            resolverChain ?? ["scim-store"],
            provisioning,
            allowedGatewayRoles ?? new HashSet<GatewayRole>(),
            delegable,
            isImplicitLegacy)
    {
    }
}

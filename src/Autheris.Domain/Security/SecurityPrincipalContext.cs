namespace Autheris.Domain.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;

/// <summary>
/// Architecture Phase 1: Canonical, immutable security context associated with an individual request.
/// Eliminates ad-hoc claim lookups, header spoofing, and diverging identity normalization.
/// </summary>
public sealed record SecurityPrincipalContext
{
    public const string ItemKey = "SecurityPrincipalContext";

    /// <summary>The one item key of the resolved request tenant (set only when a tenant is resolved).</summary>
    public const string TenantIdItemKey = "TenantId";

    public required Sid UserSid { get; init; }

    /// <summary>
    /// DT-A-4: the request tenant; unresolved is representable. A caller that needs a tenant reads <see cref="TenantId"/>, which throws
    /// <see cref="Autheris.Domain.Exceptions.TenantUnresolvedException"/> (or <see cref="Autheris.Domain.Exceptions.TenantClaimInvalidException"/>
    /// for an invalid claim) instead of handing out a sentinel.
    /// </summary>
    public TenantId? ResolvedTenant { get; init; }

    /// <summary>DT-A-4: the tenant claim of the principal is invalid, reserved or conflicting (S2/S5); the request is rejected by the middleware gate.</summary>
    public bool TenantClaimInvalid { get; init; }

    /// <summary>DT-SC-05: informational only (telemetry): <c>claim|realm|default|header</c>, null when unresolved. Never read by a decision.</summary>
    public string? TenantSource { get; init; }

    public TenantId TenantId
    {
        get => ResolvedTenant is { } tenant && tenant.Value is not null
            ? tenant
            : TenantClaimInvalid
                ? throw new Autheris.Domain.Exceptions.TenantClaimInvalidException()
                : throw new Autheris.Domain.Exceptions.TenantUnresolvedException();
        init => ResolvedTenant = value;
    }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        // The generated printer would read TenantId (which throws when unresolved) and the claims; print safe members only.
        builder.Append("UserSid = ").Append(UserSid).Append(", Resolved = ").Append(ResolvedTenant is not null)
            .Append(", Scheme = ").Append(AuthenticationScheme);
        return true;
    }

    /// <summary>
    /// The effective group SIDs: token groups UNION SCIM groups as resolved by the identity pipeline. Never contains plugin-enriched
    /// SIDs (see <see cref="ControlPlaneEnrichment"/>); empty when <see cref="GroupResolutionStatus"/> is Degraded.
    /// </summary>
    public required IReadOnlySet<Sid> GroupSids { get; init; }

    private readonly Autheris.Domain.Identity.GroupResolutionStatus? _groupResolutionStatus;

    /// <summary>
    /// Set only from the identity pipeline result. The default of every other construction path is Degraded for an authenticated caller,
    /// so an unprocessed principal is never read as "member of no group".
    /// </summary>
    public Autheris.Domain.Identity.GroupResolutionStatus GroupResolutionStatus
    {
        get => _groupResolutionStatus ?? (IsAuthenticated
            ? Autheris.Domain.Identity.GroupResolutionStatus.Degraded
            : Autheris.Domain.Identity.GroupResolutionStatus.Complete);
        init => _groupResolutionStatus = value;
    }

    /// <summary>The instant after which the group resolution is stale and must be repeated (long-lived transports); null when unknown.</summary>
    public DateTimeOffset? GroupsExpireAt { get; init; }

    /// <summary>Plugin enrichment output; read by control-plane authorization only, never by a data decision.</summary>
    public ControlPlaneEnrichment? ControlPlaneEnrichment { get; init; }

    public required IReadOnlySet<string> TenantRoles { get; init; }
    public required IReadOnlySet<string> ClusterRoles { get; init; }
    public required string AuthenticationScheme { get; init; }
    public bool IsAuthenticated { get; init; } = true;
    public Autheris.Domain.Identity.IdentityRealmKind? RealmKind { get; init; }

    /// <summary>
    /// M-A2: the tenant was selected by a ClusterAdmin through the tenant header (not taken from the token claim). Such a tenant is only
    /// looked up in the tenant registry: a header can never register a tenant or mark a collision.
    /// </summary>
    public bool TenantSelectedByHeader { get; init; }
    public System.Net.IPAddress? ClientIp { get; init; }
    public CallerContext? Caller { get; init; }
    public IEnumerable<System.Security.Claims.Claim>? Claims { get; init; }
    public System.Security.Claims.ClaimsPrincipal? Principal { get; init; }

    /// <summary>Cluster-level administrator authorized to perform cross-tenant operations.</summary>
    public bool IsClusterAdmin => ClusterRoles.Any(r => !r.Contains(':', StringComparison.Ordinal) && string.Equals(r, "ClusterAdmin", StringComparison.Ordinal));

    /// <summary>Returns true if the principal possesses any of the specified roles in either tenant or cluster scope.</summary>
    public bool HasAnyRole(params string[] roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => TenantRoles.Contains(r) || ClusterRoles.Contains(r));
    }

    /// <summary>Returns true if the principal possesses any of the specified roles in either tenant or cluster scope.</summary>
    public bool HasAnyRole(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return roles.Any(r => TenantRoles.Contains(r) || ClusterRoles.Contains(r));
    }

    /// <summary>Checks whether the principal has a specific role in tenant or cluster scope.</summary>
    public bool HasRole(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return TenantRoles.Contains(role) || ClusterRoles.Contains(role);
    }

    /// <summary>K-K10: Checks whether the principal satisfies the required GatewayRole with hierarchy semantics.</summary>
    public bool HasRole(GatewayRole role)
    {
        if (TenantClaimInvalid)
        {
            return false;
        }

        // Tenant-scoped-only roles (data products) are conferred by the request tenant's roles alone: never by ClusterAdmin or a global role.
        if (role.IsTenantScopedOnly())
        {
            foreach (var r in TenantRoles)
            {
                if (TryMatchTenantRole(r, out var tenantRole) && tenantRole == role)
                {
                    return true;
                }
            }

            return false;
        }

        // Cluster roles evaluate the role implication matrix at global scope; colon-prefixed roles are tenant-scoped and skipped here.
        foreach (var r in ClusterRoles)
        {
            if (r.Contains(':', StringComparison.Ordinal)) continue;
            if (GatewayRoleExtensions.TryParseGlobalRole(r, out var parsed))
            {
                if (parsed == GatewayRole.ClusterAdmin &&
                    string.Equals(AuthenticationScheme, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (parsed.Implies(role))
                {
                    return true;
                }
            }
        }

        // Global administrative role (ClusterAdmin) can NEVER be satisfied by tenant-scoped roles
        if (role == GatewayRole.ClusterAdmin)
        {
            return false;
        }

        // Tenant-scoped roles can satisfy tenant/governance roles when unprefixed or matching ResolvedTenant (never '*').
        foreach (var r in TenantRoles)
        {
            if (TryMatchTenantRole(r, out var parsed))
            {
                if (parsed == GatewayRole.ClusterAdmin)
                {
                    continue;
                }

                if (parsed.Implies(role))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryMatchTenantRole(string rawRole, out GatewayRole parsed)
    {
        if (!rawRole.Contains(':', StringComparison.Ordinal))
        {
            return GatewayRoleExtensions.TryParseGlobalRole(rawRole, out parsed);
        }

        if (ResolvedTenant is not { Value: not null } tenant || TenantId.IsReserved(tenant))
        {
            parsed = default;
            return false;
        }

        if (Principal is not null)
        {
            var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(Principal);
            if (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid ||
                (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid &&
                 !string.Equals(claim.Tenant.Value, tenant.Value, StringComparison.OrdinalIgnoreCase)))
            {
                parsed = default;
                return false;
            }
        }

        if (!GatewayRoleExtensions.TryParseTenantScopedRole(rawRole, out var roleTenant, out parsed) ||
            roleTenant == "*" ||
            parsed == GatewayRole.ClusterAdmin)
        {
            parsed = default;
            return false;
        }

        return string.Equals(roleTenant, tenant.Value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>K-K10: Checks whether the principal satisfies any of the required GatewayRoles.</summary>
    public bool HasAnyRole(params GatewayRole[] roles) => HasAnyRole((IEnumerable<GatewayRole>)roles);

    /// <summary>K-K10: Checks whether the principal satisfies any of the required GatewayRoles.</summary>
    public bool HasAnyRole(IEnumerable<GatewayRole> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        foreach (var required in roles)
        {
            if (HasRole(required))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Creates an anonymous security context: no tenant (an unauthenticated principal never selects one) and empty permissions.</summary>
    public static SecurityPrincipalContext Anonymous(CallerContext? caller = null) => new()
    {
        UserSid = new Sid("ANONYMOUS"),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        AuthenticationScheme = "Anonymous",
        IsAuthenticated = false,
        Caller = caller
    };

    /// <summary>
    /// Constructs a canonical SecurityPrincipalContext from a ClaimsPrincipal, partitioning roles and extracting SIDs and tenant.
    /// </summary>
    public static SecurityPrincipalContext FromPrincipal(
        System.Security.Claims.ClaimsPrincipal principal,
        TenantId? overrideTenant = null,
        string? authenticationScheme = null,
        System.Net.IPAddress? clientIp = null,
        CallerContext? caller = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var groupStatus = Autheris.Domain.Identity.GroupResolutionClaims.Read(principal, now);
        var groupsExpireAt = Autheris.Domain.Identity.GroupResolutionClaims.ReadExpiresAt(principal);
        var userSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "ANONYMOUS");
        var groupSids = groupStatus == Autheris.Domain.Identity.GroupResolutionStatus.Complete
            ? principal.GetGroupSids()
            : new HashSet<Sid>();
        var rawRoles = principal.GetUserRoles();
        var clusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool clusterAdminAllowed = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        bool hasAllowedRoles = Autheris.Domain.Identity.RoleResolutionClaims.TryReadAllowedGatewayRoles(principal, out var allowedGatewayRoles);

        foreach (var r in rawRoles)
        {
            if (hasAllowedRoles &&
                GatewayRoleExtensions.TryParseGatewayRoleValue(r, out _, out var parsedRole) &&
                !allowedGatewayRoles.Contains(parsedRole))
            {
                continue;
            }

            if (ClusterAdminPolicy.IsClusterAdminRole(r))
            {
                if (clusterAdminAllowed)
                {
                    clusterRoles.Add("ClusterAdmin");
                }
            }
            else
            {
                tenantRoles.Add(r);
            }
        }

        var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        var claimInvalid = overrideTenant is null && claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Invalid;
        TenantId? tenantId = overrideTenant ?? (claim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? (TenantId?)claim.Tenant : null);

        return new SecurityPrincipalContext
        {
            UserSid = userSid,
            ResolvedTenant = tenantId,
            TenantClaimInvalid = claimInvalid,
            TenantSource = tenantId is null ? null : principal.FindFirst(Autheris.Domain.Tenancy.TenantSourceClaim.ClaimType)?.Value,
            GroupSids = groupSids,
            GroupResolutionStatus = groupStatus,
            GroupsExpireAt = groupsExpireAt,
            TenantRoles = tenantRoles,
            ClusterRoles = clusterRoles,
            AuthenticationScheme = authenticationScheme ?? principal.Identity?.AuthenticationType ?? "Token",
            IsAuthenticated = principal.Identity?.IsAuthenticated ?? true,
            RealmKind = ReadPipelineRealmKind(principal),
            ClientIp = clientIp,
            Caller = caller,
            Claims = principal.Claims,
            Principal = principal
        };
    }

    public static Autheris.Domain.Identity.IdentityRealmKind? ReadPipelineRealmKind(System.Security.Claims.ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        foreach (var claim in principal.Claims)
        {
            if (string.Equals(claim.Type, "autheris:realm_kind", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(claim.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal) &&
                Enum.TryParse<Autheris.Domain.Identity.IdentityRealmKind>(claim.Value, ignoreCase: true, out var kind))
            {
                return kind;
            }
        }

        return null;
    }
}

/// <summary>
/// Output of an authentication plugin (roles and canonical group SIDs). Control-plane authorization reads it; no data grant, deny, row
/// filter, mask, write grant, usage accounting, Envoy decision or MCP session does.
/// </summary>
public sealed record ControlPlaneEnrichment(IReadOnlySet<string> Roles, IReadOnlySet<Sid> GroupSids)
{
    public static readonly ControlPlaneEnrichment Empty = new(new HashSet<string>(), new HashSet<Sid>());
}

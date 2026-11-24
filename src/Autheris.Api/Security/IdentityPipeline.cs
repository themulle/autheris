namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The one identity pipeline of HTTP, WebSocket and MCP ingress. The result is an unauthenticated principal when any stage fails, otherwise
/// a principal with one identity whose group, status and marker claims carry the issuer <see cref="IdentityClaimIssuers.Pipeline"/>.
/// </summary>
public interface IIdentityPipeline
{
    ValueTask<ClaimsPrincipal> ProcessAsync(ClaimsPrincipal raw, string scheme, X509Certificate2? clientCert, CancellationToken ct);
}

/// <summary>
/// Fixed order: strip inbound identity-controlled claims, normalize, reject privileged subjects, decide realm and tenant, resolve groups,
/// resolve roles and functional rights. The only caller of <see cref="ClaimsNormalizer.Normalize"/>.
/// </summary>
public sealed class IdentityPipeline : IIdentityPipeline
{
    private static readonly Meter Meter = new("Autheris.Core");

    private static readonly Counter<long> PrivilegedRejected = Meter.CreateCounter<long>(
        "autheris_identity_privileged_subject_rejected_total",
        description: "Authentications rejected because the subject is a privileged built-in identity.");

    // Inbound claims that only the pipeline may produce; stripped on every identity of the principal before anything is emitted.
    private static readonly HashSet<string> StrippedClaimTypes = new(
        GroupResolutionClaims.InboundGroupClaimTypes.Concat(
        [
            "autheris:group",
            "autheris:realm",
            "autheris:realm_kind",
            "autheris:subject_kind",
            RequireFunctionalRightHandler.FunctionalRightClaimType,
            RoleResolutionClaims.ClaimType,
            RoleResolutionClaims.AllowedGatewayRolesClaimType,
            GroupResolutionClaims.StatusClaimType,
            GroupResolutionClaims.MarkerClaimType,
            GroupResolutionClaims.ExpiresAtClaimType
        ]),
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> UntrustedWindowsSidClaimTypes = new(
        [
            ClaimTypes.PrimarySid,
            "primarysid",
            "http://schemas.microsoft.com/ws/2008/06/identity/claims/primarysid",
            "objectSid",
            "onprem_sid"
        ],
        StringComparer.OrdinalIgnoreCase);

    // A principal without a registered realm or tenant default gets no default tenant (unit tests and tooling).
    private static readonly TenancyOptions NoDefaultTenancy = new();

    private readonly IIdentityRealmRegistry? _realmRegistry;
    private readonly IGroupSidResolver _groupResolver;
    private readonly IRoleResolver? _roleResolver;
    private readonly IFunctionalRightMapper? _functionalRightMapper;
    private readonly ITenantResolver _tenantResolver;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<IdentityPipeline> _logger;
    private readonly TimeProvider _time;
    private readonly IAuditWriter? _auditWriter;

    public IdentityPipeline(
        IIdentityRealmRegistry? realmRegistry,
        IGroupSidResolver groupResolver,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? functionalRightMapper = null,
        ITenantResolver? tenantResolver = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<IdentityPipeline>? logger = null,
        TimeProvider? timeProvider = null,
        IAuditWriter? auditWriter = null)
    {
        _auditWriter = auditWriter;
        _time = timeProvider ?? TimeProvider.System;
        _realmRegistry = realmRegistry;
        _groupResolver = groupResolver ?? throw new ArgumentNullException(nameof(groupResolver));
        _roleResolver = roleResolver;
        _functionalRightMapper = functionalRightMapper;
        _logger = logger ?? NullLogger<IdentityPipeline>.Instance;
        _options = options;
        _tenantResolver = tenantResolver ?? new TenantResolver(static () => NoDefaultTenancy, realmRegistry, NullLogger<TenantResolver>.Instance);
    }

    public async ValueTask<ClaimsPrincipal> ProcessAsync(ClaimsPrincipal raw, string scheme, X509Certificate2? clientCert, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Identity is not ClaimsIdentity { IsAuthenticated: true })
        {
            return raw;
        }

        // Already processed by this pipeline (marker minted with the pipeline issuer): returned unchanged unless its bounded group snapshot has expired.
        // A marker of any other issuer is a forgery; it is stripped below and the principal is processed again.
        if (GroupResolutionClaims.HasPipelineMarker(raw))
        {
            return GroupResolutionClaims.IsExpired(raw, _time.GetUtcNow())
                ? ExpirePipelineGroups(raw)
                : raw;
        }

        try
        {
            return await RunAsync(raw, scheme, clientCert, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Provider exception text can carry directory or token details: only the type name is logged.
            _logger.LogWarning("The identity pipeline failed ({ExceptionType}); the principal is not authenticated.", ex.GetType().Name);
            return Unauthenticated();
        }
    }

    private static ClaimsPrincipal ExpirePipelineGroups(ClaimsPrincipal principal)
    {
        var clone = principal.Clone();
        foreach (var identity in clone.Identities)
        {
            var toRemove = identity.Claims
                .Where(static c =>
                    GroupResolutionClaims.InboundGroupClaimTypes.Contains(c.Type, StringComparer.OrdinalIgnoreCase)
                    || c.Type == GroupResolutionClaims.StatusClaimType
                    || string.Equals(c.Type, RoleResolutionClaims.ClaimType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var claim in toRemove)
            {
                identity.RemoveClaim(claim);
            }
        }

        if (clone.Identity is ClaimsIdentity primary)
        {
            primary.AddClaim(GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded));
            primary.AddClaim(new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        }

        return clone;
    }

    private async ValueTask<ClaimsPrincipal> RunAsync(ClaimsPrincipal raw, string scheme, X509Certificate2? clientCert, CancellationToken ct)
    {
        var rawIssuer = raw.FindFirst("iss")?.Value;
        var rawAuthType = (raw.Identity as ClaimsIdentity)?.AuthenticationType;
        IdentityRealm? realm = ResolveScimRealm(raw, scheme)
            ?? (!string.IsNullOrWhiteSpace(rawIssuer) ? _realmRegistry?.ResolveRealmByIssuer(rawIssuer) : null)
            ?? _realmRegistry?.ResolveRealmForScheme(rawIssuer, rawAuthType)
            ?? _realmRegistry?.GetDefaultRealm();
        var realmId = realm?.RealmId ?? "workforce-entra";

        // Test host only (compile-gated): a TestAuth principal is not an identity-provider token, so its tenant and SID claims stand.
#if AUTHERIS_TEST_AUTH
        var tenantByClaims = string.Equals(rawAuthType, TestAuthHandler.SchemeName, StringComparison.Ordinal);
#else
        const bool tenantByClaims = false;
#endif

        if (IsPrivilegedSubject(raw))
        {
            PrivilegedRejected.Add(1, new KeyValuePair<string, object?>("scheme", FixedScheme(scheme)));
            _logger.LogWarning("Authentication rejected: privileged subject (scheme {Scheme}).", FixedScheme(scheme));
            return Unauthenticated();
        }

        // Non-AD/external/OIDC realms and app-only service principals must never accept inbound Windows SID claims from tokens.
        var isServicePrincipal = !tenantByClaims && !IsScimScheme(scheme) && clientCert is null
            && Autheris.Application.Catalog.External.AppOnlyPrincipal.Is(raw);
        var stripUntrustedWindowsSids = !tenantByClaims && !IsScimScheme(scheme)
            && ((realm != null && IsNonWindowsRealm(realm)) || isServicePrincipal);
        var normalizationInput = stripUntrustedWindowsSids ? StripInboundWindowsSidClaims(raw) : raw;

        // The group resolver reads the provider claims of the normalized principal; the emitted identity never carries them.
        var normalized = ClaimsNormalizer.Normalize(normalizationInput, clientCert);

        if (IsPrivilegedSubject(normalized))
        {
            PrivilegedRejected.Add(1, new KeyValuePair<string, object?>("scheme", FixedScheme(scheme)));
            _logger.LogWarning("Authentication rejected: privileged subject (scheme {Scheme}).", FixedScheme(scheme));
            return Unauthenticated();
        }

        var identity = Collapse(normalized);

        if (realm != null && !tenantByClaims)
        {
            // An unregistered fallback id is not stamped: the tenant resolver treats a non-blank realm claim as an authoritative realm.
            identity.AddClaim(new Claim("autheris:realm", realmId));
            identity.AddClaim(new Claim("autheris:realm_kind", realm.Kind.ToString(), ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        }

        if (isServicePrincipal)
        {
            identity.AddClaim(new Claim("autheris:subject_kind", "sp", ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        }

        var degradedReason = ClientAllowlistFailure(realm, identity);

        // DT-A-3 / DT-SC-05 / CRIT-01: the one tenant decision of the ingress.
        var resolution = TenantStamping.Apply(identity, _tenantResolver, realm != null && !tenantByClaims);
        if (realm == null && !tenantByClaims)
        {
            identity.AddClaim(new Claim("autheris:realm", realmId));
        }

        var groups = new HashSet<Sid>();
        DateTimeOffset? expiresAt = null;
        var source = "pipeline";
        if (degradedReason is null)
        {
#if AUTHERIS_TEST_AUTH
            // Test host only (compile-gated): the test handler states the groups itself, like the tenant.
            var outcome = tenantByClaims
                ? TestAuthGroups(normalized)
                : await ResolveGroupsAsync(normalized, realm, resolution.Tenant, scheme, ct).ConfigureAwait(false);
#else
            var outcome = await ResolveGroupsAsync(normalized, realm, resolution.Tenant, scheme, ct).ConfigureAwait(false);
#endif
            source = outcome.Source;
            expiresAt = outcome.ExpiresAt;
            if (outcome.Status == GroupResolutionStatus.Complete)
            {
                groups.UnionWith(outcome.GroupSids);
            }
            else
            {
                degradedReason = "group resolution incomplete";
            }
        }

        var isForwardAuth = string.Equals(scheme, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase)
            || normalized.Identities.Any(i =>
                i.IsAuthenticated &&
                string.Equals(i.AuthenticationType, ClusterAdminPolicy.ForwardAuthScheme, StringComparison.OrdinalIgnoreCase));

        if (degradedReason is null && realm is { GroupSource: RealmGroupSource.Resolver } && !IsScimScheme(scheme))
        {
            degradedReason = await AddRolesAndRightsAsync(identity, normalized, realm, realmId, resolution.Tenant, groups, isForwardAuth, ct).ConfigureAwait(false);
        }

        if (!tenantByClaims && !IsScimScheme(scheme))
        {
            ApplyRealmRoleOrigin(identity, realm, isForwardAuth);
        }

        if (!tenantByClaims && !IsScimScheme(scheme) && clientCert is null && !BindRealmSubjectSid(identity, realm, realmId, isServicePrincipal))
        {
            _logger.LogWarning("Authentication rejected: missing required user key claim for realm {RealmId}.", realmId);
            return Unauthenticated();
        }

        var degraded = degradedReason is not null;
        if (degraded)
        {
            groups.Clear();
            MarkDegraded(identity, realmId, degradedReason!);
        }

        IdentityGroupMetrics.RecordResolution(source, degraded);
        foreach (var sid in groups)
        {
            identity.AddClaim(IdentityClaimIssuers.CreateGroupSidClaim(sid.Value));
        }

        identity.AddClaim(GroupResolutionClaims.CreateStatus(degraded ? GroupResolutionStatus.Degraded : GroupResolutionStatus.Complete));
        if (expiresAt is { } expiry)
        {
            identity.AddClaim(GroupResolutionClaims.CreateExpiresAt(expiry));
        }

        identity.AddClaim(GroupResolutionClaims.CreateMarker());
        return new ClaimsPrincipal(identity);
    }

    private static bool IsScimScheme(string scheme) => string.Equals(scheme, GatewayAuthSchemes.Scim, StringComparison.Ordinal);

    // The SCIM machine identity is bound to the realm of its route and secret (minted by the SCIM handler, whose identity alone carries the
    // SCIM authentication type); it is never moved to the default realm, and it never receives resolver roles (the provisioner role stands alone).
    private IdentityRealm? ResolveScimRealm(ClaimsPrincipal raw, string scheme)
    {
        if (!IsScimScheme(scheme)
            || !string.Equals(raw.Identity?.AuthenticationType, GatewayAuthSchemes.Scim, StringComparison.Ordinal))
        {
            return null;
        }

        var realmId = raw.FindFirst("autheris:realm")?.Value;
        return string.IsNullOrWhiteSpace(realmId) ? null : _realmRegistry?.GetRealm(realmId);
    }

    private static bool IsPrivilegedSubject(ClaimsPrincipal principal)
    {
        var sid = principal.GetUserSid();
        if (sid is { } userSid && SidFormat.IsPrivileged(userSid))
        {
            return true;
        }

        var sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return !string.IsNullOrWhiteSpace(sub) && SidFormat.IsPrivileged(new Sid(sub.Trim()));
    }

    // One identity for the whole principal; the stripped claim types of every source identity are left out.
    private static ClaimsIdentity Collapse(ClaimsPrincipal principal)
    {
        var primary = (ClaimsIdentity)principal.Identity!;
        var collapsed = new ClaimsIdentity(primary.AuthenticationType, primary.NameClaimType, primary.RoleClaimType);
        foreach (var source in principal.Identities)
        {
            foreach (var claim in source.Claims)
            {
                if (!StrippedClaimTypes.Contains(claim.Type))
                {
                    collapsed.AddClaim(claim);
                }
            }
        }

        return collapsed;
    }

    // GHSA-hw85-rqjh-cwxx: realm client allowlist.
    private static string? ClientAllowlistFailure(IdentityRealm? realm, ClaimsIdentity identity)
    {
        if (realm?.AllowedClientIds is not { Count: > 0 } allowed)
        {
            return null;
        }

        var clientId = identity.FindFirst("azp")?.Value ?? identity.FindFirst("appid")?.Value ?? identity.FindFirst("client_id")?.Value;
        return string.IsNullOrWhiteSpace(clientId) || !allowed.Contains(clientId, StringComparer.OrdinalIgnoreCase)
            ? "client not in realm allowed clients"
            : null;
    }

#if AUTHERIS_TEST_AUTH
    private GroupResolution TestAuthGroups(ClaimsPrincipal normalized)
    {
        var sids = new HashSet<Sid>();
        foreach (var claim in normalized.FindAll(c => c.Type == ClaimTypes.GroupSid && c.Issuer == IdentityClaimIssuers.Pipeline))
        {
            if (SidFormat.IsCanonical(claim.Value))
            {
                sids.Add(new Sid(claim.Value));
            }
        }

        return new GroupResolution(sids, GroupResolutionStatus.Complete, "test-auth", _time.GetUtcNow().AddMinutes(1));
    }
#endif

    private async ValueTask<GroupResolution> ResolveGroupsAsync(
        ClaimsPrincipal normalized, IdentityRealm? realm, TenantId? tenant, string scheme, CancellationToken ct)
    {
        var fallbackExpiry = _time.GetUtcNow().AddMinutes(1);
        if (string.Equals(scheme, GatewayAuthSchemes.Scim, StringComparison.Ordinal))
        {
            // The SCIM machine scheme is path-bound and carries no user groups.
            return new GroupResolution(new HashSet<Sid>(), GroupResolutionStatus.Complete, "scim", fallbackExpiry);
        }

        if (realm is null)
        {
            return new GroupResolution(new HashSet<Sid>(), GroupResolutionStatus.Degraded, "no-realm", fallbackExpiry);
        }

        // ForwardAuth: the configured group source decides between the trusted header (SIDs minted by the handler) and the realm chain.
        if (string.Equals(scheme, GatewayAuthSchemes.ForwardAuth, StringComparison.Ordinal) && _options is not null)
        {
            var source = _options.Value.Authentication.ForwardAuth.GroupSource == ForwardAuthGroupSource.Header
                ? RealmGroupSource.Token
                : RealmGroupSource.Resolver;
            realm = realm with { GroupSource = source };
        }

        if (realm.GroupSource == RealmGroupSource.Resolver && tenant is not { Value: not null })
        {
            return new GroupResolution(new HashSet<Sid>(), GroupResolutionStatus.Degraded, "no-tenant", fallbackExpiry);
        }

        return await _groupResolver.ResolveAsync(normalized, realm, tenant ?? default, scheme, ct).ConfigureAwait(false);
    }

    private async ValueTask<string?> AddRolesAndRightsAsync(
        ClaimsIdentity identity, ClaimsPrincipal normalized, IdentityRealm realm, string realmId, TenantId? tenant, HashSet<Sid> groups, bool isForwardAuth, CancellationToken ct)
    {
        // Roles come from the realm's own provider chain (Resolver realms only); the ForwardAuth GroupSource override above applies to groups, never to roles.
        if (_roleResolver is null || tenant is not { Value: not null } tenantId)
        {
            return _roleResolver is null ? null : "tenant unresolved";
        }

        // GOV-08: without a user key there is nothing to resolve; an empty key would share one cache entry between all such callers.
        var userKey = RealmUserKeyResolver.Resolve(identity, realm) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userKey))
        {
            return "no user key";
        }

        var resolvedIdentity = new ResolvedIdentity(
            userKey,
            identity.FindFirst("upn")?.Value ?? identity.FindFirst(ClaimTypes.Upn)?.Value,
            identity.FindFirst("name")?.Value ?? identity.FindFirst(ClaimTypes.Name)?.Value,
            identity.FindFirst("email")?.Value ?? identity.FindFirst(ClaimTypes.Email)?.Value,
            tenantId,
            realmId,
            [.. normalized.Claims]);

        var rolesResolution = await _roleResolver.ResolveRolesAsync(resolvedIdentity, ct).ConfigureAwait(false);
        if (rolesResolution.IsDegraded)
        {
            return "role provider unavailable";
        }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in rolesResolution.Roles)
        {
            if (GatewayRoleExtensions.TryParseGatewayRoleValue(role, out _, out var parsedRole))
            {
                if (isForwardAuth && parsedRole == GatewayRole.ClusterAdmin)
                {
                    _logger.LogWarning("ForwardAuth identity dropped resolved ClusterAdmin role from realm role resolver for realm {RealmId}.", realmId);
                    continue;
                }

                if (!realm.AllowedGatewayRoles.Contains(parsedRole))
                {
                    _logger.LogWarning("Identity pipeline dropped resolved gateway role outside AllowedGatewayRoles for realm {RealmId}.", realmId);
                    continue;
                }
            }

            if (roles.Add(role))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }
        }

        // Workload subject mapping: additive only, audited on every authentication (role cache hits included) before any mapped role is applied.
        foreach (var mappedRole in await ResolveMappedRolesAsync(identity, realm, realmId, tenantId, ct).ConfigureAwait(false))
        {
            if (isForwardAuth && ClusterAdminPolicy.IsClusterAdminRole(mappedRole))
            {
                _logger.LogWarning("ForwardAuth identity dropped mapped ClusterAdmin role for realm {RealmId}.", realmId);
                continue;
            }

            if (roles.Add(mappedRole))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, mappedRole));
            }
        }

        // PERS-09: functional-right claims come only from FunctionalRightMapper (never an IT or governance capacity, realm role bounds);
        // a mapper failure yields no functional-right claims (fail closed). The mapper receives canonical group SIDs and roles.
        if (_functionalRightMapper != null)
        {
            var groupsAndRoles = new HashSet<string>(groups.Count + roles.Count, StringComparer.Ordinal);
            foreach (var group in groups)
            {
                groupsAndRoles.Add(group.Value);
            }

            groupsAndRoles.UnionWith(roles);
            try
            {
                var rights = await _functionalRightMapper.GetRightsForGroupsAndRolesAsync(tenantId, realmId, groupsAndRoles, ct).ConfigureAwait(false);
                foreach (var right in rights)
                {
                    identity.AddClaim(new Claim(RequireFunctionalRightHandler.FunctionalRightClaimType, right.Name));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Functional right mapping failed for realm {RealmId}; no functional-right claims are issued (fail-closed).", realmId);
            }
        }

        return null;
    }

    // The mapped role names of the identity (canonical GatewayRole names), or none. Without a required audit record nothing is applied.
    private async ValueTask<IReadOnlyList<string>> ResolveMappedRolesAsync(
        ClaimsIdentity identity, IdentityRealm realm, string realmId, TenantId tenantId, CancellationToken ct)
    {
        var mapped = SubjectRoleMapper.Resolve(realm, identity);
        if (mapped.Count == 0)
        {
            return [];
        }

        var names = new string[mapped.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = mapped[i].ToString();
        }

        if (_auditWriter is null)
        {
            _logger.LogWarning("Workload role mapping for realm {RealmId} is not applied: no audit writer (fail-closed).", realmId);
            return [];
        }

        var subject = identity.FindFirst("sub")?.Value ?? string.Empty;
        try
        {
            await _auditWriter.RecordRequiredAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.WorkloadRoleMappingApplied,
                ActorSid = new Sid("IDENTITY_PIPELINE"),
                Decision = "ALLOW",
                TargetTable = "IDENTITY_REALMS",
                TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    realm_id = realmId,
                    subject = SanitizeSubject(subject),
                    roles = names
                })
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Workload role mapping for realm {RealmId} is not applied: the audit record could not be written ({ExceptionType}; fail-closed).", realmId, ex.GetType().Name);
            return [];
        }

        return names;
    }

    // Subject values come from a validated token but stay untrusted text: control characters are replaced, the length is capped.
    private static string SanitizeSubject(string subject)
    {
        var length = Math.Min(subject.Length, SubjectRoleMapper.MaxSubjectLength);
        return string.Create(length, subject, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
    }

    private void MarkDegraded(ClaimsIdentity identity, string realmId, string reason)
    {
        identity.AddClaim(new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        Autheris.Domain.Diagnostics.GatewayDiagnostics.RoleResolutionDegradedCounter.Add(1, new KeyValuePair<string, object?>("realm", realmId));
        _logger.LogWarning("Group and role resolution is degraded for realm {RealmId} ({Reason}); data access is denied for this request (fail-closed).", realmId, reason);
    }

    private static string FixedScheme(string scheme) =>
        scheme is "Bearer" or "BearerAdfs" or "Basic" or "BasicSession" or "ForwardAuth" or "Negotiate" or "Scim" or "WebSocket" ? scheme : "other";

    private static bool IsNonWindowsRealm(IdentityRealm realm) =>
        realm.Kind is IdentityRealmKind.GenericOidc
            or IdentityRealmKind.Customer
            or IdentityRealmKind.Pat
            or IdentityRealmKind.EntraExternalId
            or IdentityRealmKind.AzureAdB2C;

    private static ClaimsPrincipal StripInboundWindowsSidClaims(ClaimsPrincipal raw)
    {
        var hasUntrustedSid = raw.Claims.Any(static c => UntrustedWindowsSidClaimTypes.Contains(c.Type));
        if (!hasUntrustedSid)
        {
            return raw;
        }

        var clone = raw.Clone();
        foreach (var source in clone.Identities)
        {
            var toRemove = source.Claims
                .Where(static c => UntrustedWindowsSidClaimTypes.Contains(c.Type))
                .ToList();
            foreach (var claim in toRemove)
            {
                source.RemoveClaim(claim);
            }
        }

        return clone;
    }

    private static bool BindRealmSubjectSid(ClaimsIdentity identity, IdentityRealm? realm, string realmId, bool isServicePrincipal)
    {
        if (!isServicePrincipal && realm is { Kind: IdentityRealmKind.Basic or IdentityRealmKind.ForwardAuth }
            && identity.FindFirst(ClaimTypes.PrimarySid)?.Value is { } localSid && !string.IsNullOrWhiteSpace(localSid))
        {
            ReplacePrimarySidClaim(identity, localSid.Trim());
            return true;
        }

        if (!isServicePrincipal && (realm is null || !IsNonWindowsRealm(realm))
            && identity.FindFirst(ClaimTypes.PrimarySid)?.Value is { } winSid && SidFormat.IsWindowsAdDomainSid(winSid))
        {
            ReplacePrimarySidClaim(identity, winSid.Trim());
            return true;
        }

        var userKey = RealmUserKeyResolver.Resolve(identity, realm)
            ?? (isServicePrincipal ? Autheris.Application.Catalog.External.AppOnlyPrincipal.GetClientId(identity) : null);
        if (string.IsNullOrWhiteSpace(userKey)) return false;

        ReplacePrimarySidClaim(identity, $"{realmId}|{userKey}");
        return true;
    }

    private static void ReplacePrimarySidClaim(ClaimsIdentity identity, string value)
    {
        foreach (var existing in identity.FindAll(ClaimTypes.PrimarySid).ToList()) identity.RemoveClaim(existing);
        identity.AddClaim(IdentityClaimIssuers.CreatePrimarySidClaim(value));
    }

    private static void ApplyRealmRoleOrigin(ClaimsIdentity identity, IdentityRealm? realm, bool isForwardAuth)
    {
        var effectiveAllowed = realm is not null
            ? new HashSet<GatewayRole>(realm.AllowedGatewayRoles)
            : [];

        if (isForwardAuth)
        {
            effectiveAllowed.Remove(GatewayRole.ClusterAdmin);
        }

        identity.AddClaim(RoleResolutionClaims.CreateAllowedGatewayRolesClaim(effectiveAllowed));

        var toRemove = new List<Claim>();
        foreach (var claim in identity.Claims)
        {
            if (!IsRoleOriginClaimType(claim.Type, identity.RoleClaimType))
            {
                continue;
            }

            if (GatewayRoleExtensions.TryParseGatewayRoleValue(claim.Value, out _, out var parsedRole) &&
                !effectiveAllowed.Contains(parsedRole))
            {
                toRemove.Add(claim);
            }
        }

        foreach (var claim in toRemove)
        {
            identity.RemoveClaim(claim);
        }
    }

    private static bool IsRoleOriginClaimType(string claimType, string roleClaimType) =>
        string.Equals(claimType, roleClaimType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, "role", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, "roles", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claimType, "gateway_role", StringComparison.OrdinalIgnoreCase);
    private static ClaimsPrincipal Unauthenticated() => new(new ClaimsIdentity());
}

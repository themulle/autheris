namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
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
            RoleResolutionClaims.ClaimType,
            GroupResolutionClaims.StatusClaimType,
            GroupResolutionClaims.MarkerClaimType,
            GroupResolutionClaims.ExpiresAtClaimType
        ]),
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

    public IdentityPipeline(
        IIdentityRealmRegistry? realmRegistry,
        IGroupSidResolver groupResolver,
        IRoleResolver? roleResolver = null,
        IFunctionalRightMapper? functionalRightMapper = null,
        ITenantResolver? tenantResolver = null,
        IOptions<GatewayOptions>? options = null,
        ILogger<IdentityPipeline>? logger = null)
    {
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

        // Already processed by this pipeline (marker minted with the pipeline issuer): returned unchanged. A marker of any other issuer
        // is a forgery; it is stripped below and the principal is processed again.
        if (GroupResolutionClaims.HasPipelineMarker(raw))
        {
            return raw;
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
            _logger.LogWarning(ex, "The identity pipeline failed; the principal is not authenticated.");
            return Unauthenticated();
        }
    }

    private async ValueTask<ClaimsPrincipal> RunAsync(ClaimsPrincipal raw, string scheme, X509Certificate2? clientCert, CancellationToken ct)
    {
        // The group resolver reads the provider claims of the normalized principal; the emitted identity never carries them.
        var normalized = ClaimsNormalizer.Normalize(raw, clientCert);

        if (IsPrivilegedSubject(normalized))
        {
            PrivilegedRejected.Add(1, new KeyValuePair<string, object?>("scheme", FixedScheme(scheme)));
            _logger.LogWarning("Authentication rejected: privileged subject (scheme {Scheme}).", FixedScheme(scheme));
            return Unauthenticated();
        }

        var identity = Collapse(normalized);

        var issuer = identity.FindFirst("iss")?.Value;
        IdentityRealm? realm = (!string.IsNullOrWhiteSpace(issuer) ? _realmRegistry?.ResolveRealmByIssuer(issuer) : null)
            ?? _realmRegistry?.ResolveRealmForScheme(issuer, identity.AuthenticationType)
            ?? _realmRegistry?.GetDefaultRealm();
        var realmId = realm?.RealmId ?? "workforce-entra";

        // Test host only (compile-gated): a TestAuth principal is not an identity-provider token, so its tenant claims are the tenant statement.
#if AUTHERIS_TEST_AUTH
        var tenantByClaims = identity.AuthenticationType == TestAuthHandler.SchemeName;
#else
        const bool tenantByClaims = false;
#endif
        if (realm != null && !tenantByClaims)
        {
            // An unregistered fallback id is not stamped: the tenant resolver treats a non-blank realm claim as an authoritative realm.
            identity.AddClaim(new Claim("autheris:realm", realmId));
        }

        var degradedReason = ClientAllowlistFailure(realm, identity);

        // DT-A-3 / DT-SC-05 / CRIT-01: the one tenant decision of the ingress.
        var resolution = TenantStamping.Apply(identity, _tenantResolver, realm != null && !tenantByClaims);
        if (realm == null || tenantByClaims)
        {
            identity.AddClaim(new Claim("autheris:realm", realmId));
        }

        var groups = new HashSet<Sid>();
        DateTimeOffset? expiresAt = null;
        var source = "pipeline";
        if (degradedReason is null)
        {
            var outcome = await ResolveGroupsAsync(normalized, realm, resolution.Tenant, scheme, ct).ConfigureAwait(false);
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

        if (degradedReason is null && realm is { GroupSource: RealmGroupSource.Resolver })
        {
            degradedReason = await AddRolesAndRightsAsync(identity, normalized, realm, realmId, resolution.Tenant, groups, ct).ConfigureAwait(false);
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

    private async ValueTask<GroupResolution> ResolveGroupsAsync(
        ClaimsPrincipal normalized, IdentityRealm? realm, TenantId? tenant, string scheme, CancellationToken ct)
    {
        var fallbackExpiry = DateTimeOffset.UtcNow.AddMinutes(1);
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
        ClaimsIdentity identity, ClaimsPrincipal normalized, IdentityRealm realm, string realmId, TenantId? tenant, HashSet<Sid> groups, CancellationToken ct)
    {
        if (_roleResolver is null || tenant is not { Value: not null } tenantId)
        {
            return _roleResolver is null ? null : "tenant unresolved";
        }

        // GOV-08: without a user key there is nothing to resolve; an empty key would share one cache entry between all such callers.
        var userKey = identity.FindFirst("oid")?.Value
            ?? identity.FindFirst("sub")?.Value
            ?? normalized.GetUserSid()?.Value
            ?? string.Empty;
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
            normalized.Claims.ToList());

        var rolesResolution = await _roleResolver.ResolveRolesAsync(resolvedIdentity, ct).ConfigureAwait(false);
        if (rolesResolution.IsDegraded)
        {
            return "role provider unavailable";
        }

        foreach (var role in rolesResolution.Roles)
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }

        // PERS-09: functional-right claims come only from FunctionalRightMapper (four-eyes for privileged rights, customer-realm ceiling);
        // a mapper failure yields no functional-right claims (fail closed). The mapper receives canonical group SIDs and roles.
        if (_functionalRightMapper != null)
        {
            var groupsAndRoles = groups.Select(g => g.Value).Concat(rolesResolution.Roles).Distinct().ToList();
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

    private void MarkDegraded(ClaimsIdentity identity, string realmId, string reason)
    {
        identity.AddClaim(new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        Autheris.Domain.Diagnostics.GatewayDiagnostics.RoleResolutionDegradedCounter.Add(1, new KeyValuePair<string, object?>("realm", realmId));
        _logger.LogWarning("Group and role resolution is degraded for realm {RealmId} ({Reason}); data access is denied for this request (fail-closed).", realmId, reason);
    }

    private static string FixedScheme(string scheme) =>
        scheme is "Bearer" or "BearerAdfs" or "Basic" or "BasicSession" or "ForwardAuth" or "Negotiate" or "Scim" or "WebSocket" ? scheme : "other";

    private static ClaimsPrincipal Unauthenticated() => new(new ClaimsIdentity());
}

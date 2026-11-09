namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// One table of group sources, selected by realm group source and authentication scheme:
/// Entra token (<c>groups</c> object ids or SIDs, overage through Graph), AD FS token (<c>groupsid</c>), Negotiate (PAC on a Windows host,
/// otherwise the realm chain), Basic and ForwardAuth (SIDs minted by the trusted handler), Resolver realms (the realm provider chain).
/// Every failure is <see cref="GroupResolutionStatus.Degraded"/> with an empty set; groups are never truncated.
/// </summary>
public sealed class GroupSidResolver : IGroupSidResolver
{
    private const string TokenIdClaimGroups = "groups";
    private static readonly string[] EntraGroupClaimTypes = [TokenIdClaimGroups, "http://schemas.microsoft.com/ws/2008/06/identity/claims/groups"];
    private static readonly string[] AdfsGroupClaimTypes = ["groupsid", ClaimTypes.GroupSid];

    private readonly IRoleResolver? _roleResolver;
    private readonly IReadOnlyList<IRoleProvider> _providers;
    private readonly ILogger<GroupSidResolver>? _logger;
    private readonly TimeProvider _time;
    private readonly bool _isWindowsHost;
    private readonly int _ttlMinutes;
    private readonly int _maxGroups;
    private readonly string? _adfsAuthority;

    public GroupSidResolver(
        IRoleResolver? roleResolver,
        IEnumerable<IRoleProvider> providers,
        IOptions<GatewayOptions> options,
        ILogger<GroupSidResolver>? logger = null,
        TimeProvider? timeProvider = null,
        bool? isWindowsHost = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        _roleResolver = roleResolver;
        _providers = providers.ToList();
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _isWindowsHost = isWindowsHost ?? OperatingSystem.IsWindows();
        var auth = options.Value.Authentication;
        _ttlMinutes = auth.GroupCacheTtlMinutes is >= 1 and <= 60 ? auth.GroupCacheTtlMinutes : 5;
        _maxGroups = auth.MaxGroupSidsPerCaller is >= 1 and <= 4096 ? auth.MaxGroupSidsPerCaller : 1024;
        _adfsAuthority = auth.Adfs.Enabled && !string.IsNullOrWhiteSpace(auth.Adfs.Authority) ? auth.Adfs.Authority.TrimEnd('/') : null;
    }

    public async ValueTask<GroupResolution> ResolveAsync(
        ClaimsPrincipal normalized, IdentityRealm realm, TenantId tenant, string scheme, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(normalized);
        ArgumentNullException.ThrowIfNull(realm);

        var now = _time.GetUtcNow();
        var expiresAt = now.AddMinutes(_ttlMinutes);

        if (realm.GroupSource == RealmGroupSource.None)
        {
            return new GroupResolution(new HashSet<Sid>(), GroupResolutionStatus.Complete, "none", expiresAt);
        }

        try
        {
            var outcome = realm.GroupSource == RealmGroupSource.Resolver
                ? await ResolveFromChainAsync(normalized, realm, tenant, ct).ConfigureAwait(false)
                : await ResolveFromSchemeAsync(normalized, realm, tenant, scheme, ct).ConfigureAwait(false);

            if (outcome.Sids.Count > _maxGroups || outcome.Degraded)
            {
                _logger?.LogWarning("Group resolution degraded for source {Source}.", outcome.Source);
                return Degraded(outcome.Source, expiresAt);
            }

            if (outcome.ChainExpiresAt is { } chainExpiry && chainExpiry < expiresAt)
            {
                expiresAt = chainExpiry;
            }

            if (outcome.IsToken && TokenExpiry(normalized) is { } tokenExpiry && tokenExpiry < expiresAt)
            {
                expiresAt = tokenExpiry;
            }

            return new GroupResolution(outcome.Sids, GroupResolutionStatus.Complete, outcome.Source, expiresAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            _logger?.LogWarning("Group resolution failed; the result is degraded.");
            return Degraded("error", expiresAt);
        }
    }

    private static GroupResolution Degraded(string source, DateTimeOffset expiresAt) =>
        new(new HashSet<Sid>(), GroupResolutionStatus.Degraded, source, expiresAt);

    private async ValueTask<Outcome> ResolveFromSchemeAsync(ClaimsPrincipal principal, IdentityRealm realm, TenantId tenant, string scheme, CancellationToken ct)
    {
        switch (scheme)
        {
            case "Bearer" or "BearerAdfs":
                return IsAdfsToken(principal, scheme)
                    ? FromClaims(principal, AdfsGroupClaimTypes, requirePipelineIssuer: false, IdentityGroupMetrics.SourceAdfs, "adfs", isToken: true)
                    : await ResolveEntraAsync(principal, realm, tenant, ct).ConfigureAwait(false);
            case "Negotiate":
                return _isWindowsHost
                    ? FromClaims(principal, [ClaimTypes.GroupSid], requirePipelineIssuer: false, IdentityGroupMetrics.SourceNegotiate, "negotiate-pac", isToken: false)
                    : await ResolveFromChainAsync(principal, realm, tenant, ct).ConfigureAwait(false);
            case "Basic" or "BasicSession":
                return FromPipelineClaims(principal, IdentityGroupMetrics.SourceBasic, "basic");
            case "ForwardAuth":
                return FromPipelineClaims(principal, IdentityGroupMetrics.SourceForwardAuth, "forwardauth");
            default:
                // Unknown scheme: unknown groups, never "no groups".
                return Outcome.Failed("unknown-scheme");
        }
    }

    private bool IsAdfsToken(ClaimsPrincipal principal, string scheme)
    {
        if (scheme == "BearerAdfs")
        {
            return true;
        }

        var issuer = principal.FindFirst("iss")?.Value;
        return _adfsAuthority is not null && issuer is not null
               && (string.Equals(issuer.TrimEnd('/'), _adfsAuthority, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(issuer.TrimEnd('/'), _adfsAuthority + "/services/trust", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Group SIDs minted by a trusted handler (Basic users, ForwardAuth header from a trusted peer) carry the pipeline issuer.</summary>
    private Outcome FromPipelineClaims(ClaimsPrincipal principal, string metricSource, string label) =>
        FromClaims(principal, [ClaimTypes.GroupSid], requirePipelineIssuer: true, metricSource, label, isToken: false);

    private Outcome FromClaims(
        ClaimsPrincipal principal, string[] claimTypes, bool requirePipelineIssuer, string metricSource, string label, bool isToken)
    {
        var sids = new HashSet<Sid>();
        var rejected = 0;
        var incomplete = false;
        foreach (var claim in principal.FindAll(c => Array.Exists(claimTypes, t => string.Equals(t, c.Type, StringComparison.Ordinal))
                                                      && string.Equals(c.Issuer, IdentityClaimIssuers.Pipeline, StringComparison.Ordinal) == requirePipelineIssuer))
        {
            var value = claim.Value?.Trim();
            if (value is null || !SidFormat.IsCanonical(value))
            {
                // A dropped value makes the set incomplete (it could have been a group-scoped restriction).
                rejected++;
                incomplete = true;
            }
            else if (!sids.Add(new Sid(value)))
            {
                rejected++;
            }
        }

        RecordRejected(metricSource, rejected);
        return new Outcome(sids, label, incomplete, isToken, null);
    }

    private void RecordRejected(string source, int count)
    {
        IdentityGroupMetrics.RecordRejected(source, count);
        if (count > 0)
        {
            _logger?.LogWarning("Dropped {Count} group values that were not unique canonical SIDs from source {Source}.", count, source);
        }
    }

    private async ValueTask<Outcome> ResolveEntraAsync(ClaimsPrincipal principal, IdentityRealm realm, TenantId tenant, CancellationToken ct)
    {
        var sids = new HashSet<Sid>();
        var degraded = false;
        var rejected = 0;
        foreach (var claim in principal.FindAll(c => Array.Exists(EntraGroupClaimTypes, t => string.Equals(t, c.Type, StringComparison.Ordinal))))
        {
            var value = claim.Value?.Trim();
            if (value is not null && SidFormat.IsCanonical(value))
            {
                if (!sids.Add(new Sid(value)))
                {
                    rejected++;
                }
            }
            else if (value is not null && Guid.TryParseExact(value, "D", out var objectId) && SidFormat.TryFromEntraObjectId(objectId, out var cloudSid))
            {
                if (!sids.Add(cloudSid))
                {
                    rejected++;
                }
            }
            else
            {
                rejected++;
                degraded = true;
            }
        }

        RecordRejected(IdentityGroupMetrics.SourceEntra, rejected);
        var source = "entra";

        if (HasGroupOverage(principal))
        {
            // The group list of the token is incomplete: Graph answers for the oid of the validated token only.
            source = "entra+graph";
            var graph = _providers.FirstOrDefault(p => string.Equals(p.ProviderId, GraphRoleProvider.Id, StringComparison.OrdinalIgnoreCase));
            var identity = BuildIdentity(principal, realm, tenant);
            if (graph is null || identity is null)
            {
                degraded = true;
            }
            else
            {
                var result = await graph.GetRolesAsync(identity, realm, ct).ConfigureAwait(false);
                if (result is null or { Failed: true })
                {
                    degraded = true;
                }
                else
                {
                    foreach (var value in result.Groups)
                    {
                        if (SidFormat.IsCanonical(value))
                        {
                            sids.Add(new Sid(value));
                        }
                        else
                        {
                            degraded = true;
                        }
                    }
                }
            }
        }

        return new Outcome(sids, source, degraded, true, null);
    }

    private async ValueTask<Outcome> ResolveFromChainAsync(ClaimsPrincipal principal, IdentityRealm realm, TenantId tenant, CancellationToken ct)
    {
        var identity = BuildIdentity(principal, realm, tenant);
        if (_roleResolver is null || identity is null)
        {
            return Outcome.Failed("resolver");
        }

        var resolution = await _roleResolver.ResolveAsync(identity, realm, ct).ConfigureAwait(false);
        var sids = new HashSet<Sid>();
        var degraded = resolution.IsDegraded;
        var rejected = 0;
        foreach (var group in resolution.Groups)
        {
            if (SidFormat.IsCanonical(group))
            {
                sids.Add(new Sid(group));
            }
            else
            {
                rejected++;
                degraded = true;
            }
        }

        RecordRejected(IdentityGroupMetrics.SourceResolver, rejected);
        return new Outcome(sids, "resolver", degraded, false, resolution.ExpiresAt);
    }

    /// <summary>The user key comes from the validated subject only (oid, sub, user SID), never from upn, name or email.</summary>
    private static ResolvedIdentity? BuildIdentity(ClaimsPrincipal principal, IdentityRealm realm, TenantId tenant)
    {
        var userKey = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(userKey) || userKey.Length > 256)
        {
            return null;
        }

        return new ResolvedIdentity(
            userKey,
            principal.FindFirst("upn")?.Value ?? principal.FindFirst(ClaimTypes.Upn)?.Value,
            principal.FindFirst("name")?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value,
            principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value,
            tenant,
            realm.Id,
            principal.Claims.ToList());
    }

    /// <summary>
    /// Entra group overage: with too many groups the token carries <c>hasgroups</c> (implicit flow) or a <c>_claim_names</c> entry for
    /// <c>groups</c> instead of the full list, so the group claims are incomplete (also when a partial <c>groups</c> claim is present).
    /// </summary>
    public static bool HasGroupOverage(ClaimsPrincipal principal) =>
        principal.HasClaim(c => string.Equals(c.Type, "hasgroups", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(c.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase)) ||
        principal.HasClaim(c => string.Equals(c.Type, "_claim_names", StringComparison.OrdinalIgnoreCase)
                                && c.Value != null
                                && c.Value.Contains("groups", StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? TokenExpiry(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst("exp")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
        && exp > 0 && exp < DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(exp)
            : null;

    private sealed record Outcome(HashSet<Sid> Sids, string Source, bool Degraded, bool IsToken, DateTimeOffset? ChainExpiresAt)
    {
        public static Outcome Failed(string source) => new([], source, true, false, null);
    }
}

namespace Autheris.Application.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Providers;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;

/// <summary>
/// Maps an external approver through the default identity realm's SCIM directory (object SID, user key, external id, user name,
/// UPN). An e-mail address is never an identifier, an ambiguous or inactive user is unmapped, and the resulting principal carries only
/// directory group membership, never roles asserted by the transport.
/// </summary>
public sealed class RealmExternalApproverResolver(
    IScimUserStore? userStore = null,
    IScimGroupStore? groupStore = null,
    IIdentityRealmRegistry? realmRegistry = null,
    TimeProvider? timeProvider = null) : IExternalApproverResolver
{
    private static readonly TimeSpan GroupResolutionLifetime = TimeSpan.FromMinutes(5);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<SecurityPrincipalContext?> ResolveAsync(TenantId tenant, AssertedApprover asserted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asserted);
        if (userStore is null || groupStore is null)
        {
            return null;
        }

        if (!TryResolveTargetRealm(asserted.Subject, out var realm, out var realmId, out var lookupSubject))
        {
            return null;
        }

        var user = await FindUserAsync(tenant, realmId, lookupSubject, ct).ConfigureAwait(false);
        if (user is null || !user.Active || string.IsNullOrWhiteSpace(user.UserKey))
        {
            return null;
        }

        var groups = await groupStore.GetEffectiveGroupSidsForUserAsync(tenant, realmId, user.UserKey, ct: ct).ConfigureAwait(false);
        if (groups is null || groups.MaxDepthExceeded || groups.HasNonCanonicalSid ||
            !TryProjectGroups(groups, realm, realmId, out var projectedGroups))
        {
            return null;
        }

        var allowResolvedObjectSid = !string.IsNullOrWhiteSpace(user.ResolvedObjectSid) &&
            (realm is null
                ? SidFormat.IsCanonical(user.ResolvedObjectSid)
                : SidFormat.IsValidGroupIdentifierForRealm(
                    user.ResolvedObjectSid, realm.Kind, realmId, SidFormat.HasTenantBoundEntraIssuers(realm)));
        var primarySid = allowResolvedObjectSid ? user.ResolvedObjectSid!.Trim() : $"{realmId}|{user.UserKey}";

        var claims = new List<Claim>
        {
            IdentityClaimIssuers.CreatePrimarySidClaim(primarySid),
            new("autheris:realm", realmId),
            new(ClaimTypes.NameIdentifier, user.UserKey),
            new("tenant_id", tenant.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
            GroupResolutionClaims.CreateExpiresAt(_time.GetUtcNow().Add(GroupResolutionLifetime))
        };
        if (realm is not null) claims.Add(new("autheris:realm_kind", realm.Kind.ToString(), ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        if (!string.IsNullOrWhiteSpace(user.ExternalId)) claims.Add(new(ClaimTypes.NameIdentifier, user.ExternalId.Trim()));
        if (!string.IsNullOrWhiteSpace(user.EntraObjectId)) claims.Add(new(ClaimTypes.NameIdentifier, user.EntraObjectId.Trim()));
        foreach (var group in projectedGroups) claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(group));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "ExternalWorkflow", ClaimTypes.NameIdentifier, ClaimTypes.Role));
        return SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: tenant, timeProvider: _time);
    }

    private bool TryResolveTargetRealm(string? rawSubject, out IdentityRealm? realm, out string realmId, out string lookupSubject)
    {
        realm = null;
        realmId = string.Empty;
        lookupSubject = string.Empty;
        if (string.IsNullOrWhiteSpace(rawSubject)) return false;

        var subject = rawSubject.Trim();
        var sep = subject.IndexOf('|', StringComparison.Ordinal);
        if (sep > 0)
        {
            var prefix = subject[..sep].Trim();
            var bare = subject[(sep + 1)..].Trim();
            if (prefix.Length == 0 || bare.Length == 0) return false;
            if (realmRegistry is not null)
            {
                if (!realmRegistry.TryGetRealm(prefix, out var resolved) || resolved is null) return false;
                realm = resolved;
                realmId = resolved.Id;
            }
            else
            {
                realmId = prefix;
            }

            lookupSubject = bare;
            return true;
        }

        if (realmRegistry is null)
        {
            realmId = "default";
            lookupSubject = subject;
            return true;
        }

        realm = realmRegistry.GetDefaultRealm() ?? (realmRegistry.GetAllRealms() is { Count: 1 } single ? single[0] : null);
        if (realm is null) return false;
        realmId = realm.Id;
        lookupSubject = subject;
        return true;
    }

    private static bool TryProjectGroups(
        EffectiveScimGroupsResult groups, IdentityRealm? realm, string realmId, out HashSet<string> projected)
    {
        if (realm is not null) return ScimStoreRoleProvider.TryProjectRealmBoundGroupSids(groups, realm, out projected);

        projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in groups.GroupObjectSids)
        {
            if (!SidFormat.IsValidGroupIdentifierForRealm(raw, null, realmId)) return false;
            projected.Add(raw);
        }

        if (groups.GroupIds is not null)
        {
            foreach (var groupId in groups.GroupIds)
            {
                if (!SidFormat.TryFormatRealmGroupKey(realmId, groupId, out var qualified)) return false;
                projected.Add(qualified);
            }
        }

        return true;
    }

    /// <summary>Looks the subject up under every key kind; the result is a user only when all matches are the same user, so an ambiguous subject stays unmapped.</summary>
    private async Task<ScimUser?> FindUserAsync(TenantId tenant, string realmId, string candidate, CancellationToken ct)
    {
        ScimUser? found = null;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            ScimUser?[] matches =
            [
                await userStore!.GetUserByObjectSidAsync(tenant, realmId, candidate, ct).ConfigureAwait(false),
                await userStore!.GetUserAsync(tenant, realmId, candidate, ct).ConfigureAwait(false),
                await userStore!.GetUserByExternalIdAsync(tenant, realmId, candidate, ct).ConfigureAwait(false),
                await userStore!.GetUserByUserNameAsync(tenant, realmId, candidate, ct).ConfigureAwait(false),
                await userStore!.GetUserByUpnAsync(tenant, realmId, candidate, ct).ConfigureAwait(false)
            ];
            foreach (var match in matches)
            {
                if (match is null) continue;
                if (found is not null && !string.Equals(found.UserKey, match.UserKey, StringComparison.Ordinal)) return null;
                found = match;
            }
        }

        return found;
    }
}

namespace Autheris.Application.Identity.Providers;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

public sealed class ScimStoreRoleProvider : IRoleProvider
{
    public const string Id = "scim-store";
    public string ProviderId => Id;

    private readonly IIdentityStoreRepository _repository;

    public ScimStoreRoleProvider(IIdentityStoreRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<ProviderRoleResult> GetRolesAsync(
        ResolvedIdentity identity,
        IdentityRealm realm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(realm);

        try
        {
            var effective = await _repository.GetEffectiveGroupSidsForUserAsync(
                identity.TenantId, realm.Id, identity.UserKey, maxDepth: 16, ct: cancellationToken).ConfigureAwait(false);

            if (effective.MaxDepthExceeded || effective.HasNonCanonicalSid
                || !TryProjectRealmBoundGroupSids(effective, realm, out var groupSet))
            {
                return ProviderRoleResult.FailedResult;
            }

            return new ProviderRoleResult(
                groupSet,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Fail closed on store failure
            return ProviderRoleResult.FailedResult;
        }
    }

    internal static bool TryProjectRealmBoundGroupSids(
        EffectiveScimGroupsResult effective, IdentityRealm realm, out HashSet<string> result)
    {
        result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (SidFormat.UsesWorkforceAdSids(realm.Kind))
        {
            foreach (var rawSid in effective.GroupObjectSids)
            {
                if (!SidFormat.IsGroupSid(rawSid)
                    || (SidFormat.IsEntraCloudSid(rawSid) && !SidFormat.HasTenantBoundEntraIssuers(realm))
                    || (SidFormat.IsRealmGroupKey(rawSid) && !SidFormat.MatchesRealmGroupKey(rawSid, realm.Id)))
                    return false;
                result.Add(rawSid);
            }
            return true;
        }

        // Non-workforce realms bind SCIM groups strictly by (realm_id, group_id) and never emit AD or Entra cloud SIDs.
        foreach (var rawSid in effective.GroupObjectSids)
        {
            if (SidFormat.IsCanonical(rawSid) || !SidFormat.MatchesRealmGroupKey(rawSid, realm.Id)) return false;
            result.Add(rawSid);
        }

        if (effective.GroupIds is not null)
        {
            foreach (var groupId in effective.GroupIds)
            {
                if (string.IsNullOrWhiteSpace(groupId)) return false;
                result.Add(SidFormat.FormatRealmGroupKey(realm.Id, groupId));
            }
        }
        return true;
    }
}

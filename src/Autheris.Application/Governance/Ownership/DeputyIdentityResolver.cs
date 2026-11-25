namespace Autheris.Application.Governance.Ownership;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

/// <summary>A person resolved to an Autheris identity: realm, SCIM user key and the directory identifier (object SID or Entra object id).</summary>
public sealed record ResolvedDeputyIdentity(string RealmId, string UserKey, string? DirectoryId)
{
    /// <summary>The canonical stored reference <c>realm|userKey</c>.</summary>
    public string Reference => RealmId + "|" + UserKey;

    public string Key => DeputyKey.Qualified(RealmId, UserKey);

    public string? AltKey => string.IsNullOrWhiteSpace(DirectoryId) ? null : DeputyKey.Qualified(RealmId, DirectoryId);
}

/// <summary>Why an identity could not be resolved.</summary>
public enum DeputyIdentityFailure
{
    None = 0,
    Unresolvable = 1,
    Ambiguous = 2
}

/// <summary>Resolves the identifiers an external system sends (realm-qualified identifiers, SCIM user ids, SIDs, Entra object ids, UPNs).</summary>
public interface IDeputyIdentityResolver
{
    Task<(ResolvedDeputyIdentity? Identity, DeputyIdentityFailure Failure)> ResolveAsync(TenantId tenant, string? identifier, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDeputyIdentityResolver"/> over the SCIM user store. A realm-qualified identifier (<c>realm|value</c>) is looked up in that
/// realm only; a bare identifier is looked up in every configured realm and must match exactly one active user. Inactive, locked
/// (deprovisioned), unknown or ambiguous identities never resolve; without a user store nothing resolves (fail closed).
/// </summary>
public sealed class ScimDeputyIdentityResolver(IScimUserStore? users = null, IIdentityRealmRegistry? realms = null, IScimProvisioningStore? locks = null)
    : IDeputyIdentityResolver
{
    public async Task<(ResolvedDeputyIdentity? Identity, DeputyIdentityFailure Failure)> ResolveAsync(
        TenantId tenant, string? identifier, CancellationToken ct = default)
    {
        if (users is null || string.IsNullOrWhiteSpace(identifier) || identifier.Length > DeputyKey.MaxLength ||
            identifier.Any(char.IsControl) || string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return (null, DeputyIdentityFailure.Unresolvable);
        }

        var value = identifier.Trim();
        IEnumerable<string> realmIds;
        if (DeputyKey.TrySplit(value, out var realmPrefix, out var bare))
        {
            if (realms is not null && !realms.TryGetRealm(realmPrefix, out _))
            {
                return (null, DeputyIdentityFailure.Unresolvable);
            }

            realmIds = [realmPrefix];
            value = bare;
        }
        else
        {
            realmIds = realms?.GetAllRealms().Select(r => r.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        }

        var found = new Dictionary<(string Realm, string Key), ScimUser>();
        try
        {
            foreach (var realmId in realmIds)
            {
                foreach (var user in await CandidatesAsync(tenant, realmId, value, ct).ConfigureAwait(false))
                {
                    if (user is { Active: true } && !string.IsNullOrWhiteSpace(user.UserKey))
                    {
                        found[(realmId, user.UserKey)] = user;
                    }
                }
            }

            if (found.Count != 1)
            {
                return (null, found.Count == 0 ? DeputyIdentityFailure.Unresolvable : DeputyIdentityFailure.Ambiguous);
            }

            var ((realm, key), match) = found.First();
            if (locks is not null && await locks.IsSubjectLockedAsync(tenant, realm, key, ct).ConfigureAwait(false))
            {
                return (null, DeputyIdentityFailure.Unresolvable);
            }

            var directoryId = !string.IsNullOrWhiteSpace(match.ObjectSid) ? match.ObjectSid!.Trim()
                : !string.IsNullOrWhiteSpace(match.EntraObjectId) ? match.EntraObjectId!.Trim()
                : null;
            return (new ResolvedDeputyIdentity(realm, key, directoryId), DeputyIdentityFailure.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An identity that cannot be established never resolves.
            return (null, DeputyIdentityFailure.Unresolvable);
        }
    }

    private async Task<IReadOnlyList<ScimUser>> CandidatesAsync(TenantId tenant, string realmId, string value, CancellationToken ct)
    {
        var result = new List<ScimUser>();
        void Add(ScimUser? user)
        {
            if (user is not null)
            {
                result.Add(user);
            }
        }

        Add(await users!.GetUserAsync(tenant, realmId, value, ct).ConfigureAwait(false));
        Add(await users.GetUserByObjectSidAsync(tenant, realmId, value, ct).ConfigureAwait(false));
        Add(await users.GetUserByEntraObjectIdAsync(tenant, realmId, value, ct).ConfigureAwait(false));
        Add(await users.GetUserByUpnAsync(tenant, realmId, value, ct).ConfigureAwait(false));
        return result;
    }
}

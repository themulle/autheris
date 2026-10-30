namespace Autheris.Application.Governance;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

/// <summary>Outcome of the identity check of a principal the dbt import wants to make a data owner (I393B-SC-04).</summary>
public enum OwnerIdentityStatus
{
    /// <summary>No identity realm is configured: nothing can be verified (the audit records <c>identity_verified=false</c>).</summary>
    NotConfigured,

    /// <summary>The principal exists and is active in the identity store.</summary>
    Verified,

    /// <summary>A realm is configured but the principal is unknown or inactive: reject.</summary>
    NotFoundOrInactive,

    /// <summary>A realm is configured but the lookup failed or no store is available: reject (fail closed).</summary>
    Unavailable
}

public interface IDataOwnerIdentityVerifier
{
    Task<OwnerIdentityStatus> VerifyAsync(TenantId tenant, string sid, CancellationToken ct = default);
}

/// <summary>
/// Verifies a principal against the SCIM identity store of every configured realm. A configured realm without a store or any
/// lookup exception is <see cref="OwnerIdentityStatus.Unavailable"/>; the caller rejects the import then.
/// </summary>
public sealed class ScimDataOwnerIdentityVerifier(IIdentityRealmRegistry? realmRegistry = null, IIdentityStoreRepository? store = null) : IDataOwnerIdentityVerifier
{
    public async Task<OwnerIdentityStatus> VerifyAsync(TenantId tenant, string sid, CancellationToken ct = default)
    {
        var realms = realmRegistry?.GetAllRealms().ToList() ?? [];
        if (realms.Count == 0)
        {
            return OwnerIdentityStatus.NotConfigured;
        }

        if (store is null)
        {
            return OwnerIdentityStatus.Unavailable;
        }

        try
        {
            foreach (var realm in realms)
            {
                var user = await store.GetUserAsync(tenant, realm.Id, sid, ct).ConfigureAwait(false)
                           ?? await store.GetUserByExternalIdAsync(tenant, realm.Id, sid, ct).ConfigureAwait(false);
                if (user is { Active: true })
                {
                    return OwnerIdentityStatus.Verified;
                }
            }

            return OwnerIdentityStatus.NotFoundOrInactive;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return OwnerIdentityStatus.Unavailable;
        }
    }
}

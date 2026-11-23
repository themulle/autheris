namespace Autheris.Application.Policy;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// The effective tenant access policy (internal audience, protection baseline inputs), read once at <see cref="ReadAt"/> for a batch of
/// access decisions (<c>IAccessDecisionPoint.DecideManyAsync</c>) instead of once per decision. Like <see cref="ConsentBatchPrefetch"/>
/// it answers only for its own tenant and within <see cref="ConsentBatchPrefetch.MaxAge"/> of <see cref="ReadAt"/>; any other decision
/// reads the policy from the provider as before. It lives for one batch only.
/// </summary>
public sealed class TenantPolicyPrefetch
{
    private readonly TenantAccessPolicy _policy;

    private TenantPolicyPrefetch(TenantId tenant, DateTimeOffset readAt, TenantAccessPolicy policy)
    {
        Tenant = tenant;
        ReadAt = readAt;
        _policy = policy;
    }

    public TenantId Tenant { get; }

    public DateTimeOffset ReadAt { get; }

    /// <summary>Reads the effective policy of <paramref name="tenant"/> at <paramref name="readAt"/>.</summary>
    public static async Task<TenantPolicyPrefetch> LoadAsync(
        IAccessPolicySettingsProvider provider,
        TenantId tenant,
        DateTimeOffset readAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var policy = await provider.GetEffectiveAsync(tenant, ct).ConfigureAwait(false);
        return new TenantPolicyPrefetch(tenant, readAt, policy);
    }

    /// <summary>The prefetched policy when it was read for <paramref name="tenant"/> and is still fresh at <paramref name="now"/>.</summary>
    public bool TryGet(TenantId tenant, DateTimeOffset now, out TenantAccessPolicy policy)
    {
        if (tenant == Tenant && now >= ReadAt && now - ReadAt <= ConsentBatchPrefetch.MaxAge)
        {
            policy = _policy;
            return true;
        }

        policy = null!;
        return false;
    }
}

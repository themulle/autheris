namespace Autheris.Application.Governance.Services;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.State;

/// <summary>
/// <see cref="IAccessPlanStore"/> over <see cref="IDistributedClusterStateProvider"/>: plans live in the shared store
/// with a TTL, confirmation tokens are consumed once cluster-wide via the atomic increment (first caller sees 1).
/// An unreachable store surfaces as <see cref="ClusterStateUnavailableException"/> (fail closed).
/// </summary>
public sealed class ClusterAccessPlanStore : IAccessPlanStore
{
    private const string PlanKeyPrefix = "accessplan:plan:";
    private const string TokenKeyPrefix = "accessplan:token:";
    private const string AdminCountKeyPrefix = "accessplan:admincount:";
    private const int MaxPlanIdLength = 128;

    private readonly IDistributedClusterStateProvider _state;
    private readonly TimeProvider _time;

    public ClusterAccessPlanStore(IDistributedClusterStateProvider state, TimeProvider? timeProvider = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<bool> TrySavePlanAsync(StoredAccessPlan plan, TimeSpan ttl, int maxPendingPerAdmin, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var effectiveTtl = Positive(ttl);

        // Per-admin cap: the counter is created with the TTL of the first plan in the window, so the cap is an
        // approximation (it may reset slightly before the last pending plan expires), never unbounded growth.
        var countKey = AdminCountKey(plan.AdminSid);
        var count = await _state.IncrementAsync(countKey, 1, effectiveTtl, ct).ConfigureAwait(false)
            ?? throw new ClusterStateUnavailableException("increment", countKey, null);
        if (count > maxPendingPerAdmin)
        {
            await _state.IncrementAsync(countKey, -1, effectiveTtl, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            await _state.SetAsync(PlanKey(plan.Result.PlanId), plan, effectiveTtl, ct).ConfigureAwait(false);
        }
        catch
        {
            await _state.IncrementAsync(countKey, -1, effectiveTtl, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return true;
    }

    public async ValueTask<StoredAccessPlan?> GetPlanAsync(string planId, CancellationToken ct = default)
    {
        if (!IsValidPlanId(planId))
        {
            return null;
        }

        var stored = await _state.GetAsync<StoredAccessPlan>(PlanKey(planId), ct).ConfigureAwait(false);
        return stored != null && stored.Result.ExpiresAt > _time.GetUtcNow() ? stored : null;
    }

    public async ValueTask<bool> SetConfirmedTokenAsync(string planId, string tokenHash, CancellationToken ct = default)
    {
        var stored = await GetPlanAsync(planId, ct).ConfigureAwait(false);
        if (stored == null)
        {
            return false;
        }

        var remaining = stored.Result.ExpiresAt - _time.GetUtcNow();
        await _state.SetAsync(PlanKey(planId), stored with { ConfirmedTokenHash = tokenHash }, Positive(remaining), ct).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> TryConsumeTokenAsync(string tokenHash, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        var key = TokenKeyPrefix + tokenHash;
        var count = await _state.IncrementAsync(key, 1, Positive(ttl), ct).ConfigureAwait(false)
            ?? throw new ClusterStateUnavailableException("increment", key, null);
        return count == 1;
    }

    private static TimeSpan Positive(TimeSpan ttl) => ttl > TimeSpan.Zero ? ttl : TimeSpan.FromSeconds(1);

    private static bool IsValidPlanId(string planId) =>
        !string.IsNullOrWhiteSpace(planId) && planId.Length <= MaxPlanIdLength;

    private static string PlanKey(string planId) => PlanKeyPrefix + planId.Trim().ToLowerInvariant();

    private static string AdminCountKey(string adminSid)
        => AdminCountKeyPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(adminSid.Trim().ToUpperInvariant())));
}

namespace Autheris.Application.Identity.Services;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;

public interface IScimSyncStateTracker
{
    Task RecordAuthenticatedContactAsync(TenantId tenantId, string realmId, bool isWrite, CancellationToken ct = default);

    ValueTask<bool> IsSyncStaleAsync(TenantId tenantId, IdentityRealm realm, ScimRealmSyncState? state, CancellationToken ct = default);
}

public sealed class ScimSyncStateTracker(
    IScimProvisioningStore store,
    TimeProvider? timeProvider = null) : IScimSyncStateTracker
{
    private static readonly TimeSpan ContactThrottleWindow = TimeSpan.FromSeconds(30);

    private readonly IScimProvisioningStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(string TenantId, string RealmId), DateTimeOffset> _lastContactRecorded = new();

    public async Task RecordAuthenticatedContactAsync(
        TenantId tenantId,
        string realmId,
        bool isWrite,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(realmId);

        var now = _timeProvider.GetUtcNow();
        var key = (tenantId.Value, realmId);

        if (isWrite)
        {
            _lastContactRecorded[key] = now;
            await _store.UpsertRealmSyncStateAsync(tenantId, realmId, now, now, ct).ConfigureAwait(false);
            return;
        }

        if (_lastContactRecorded.TryGetValue(key, out var lastRecorded) && (now - lastRecorded) < ContactThrottleWindow)
        {
            return;
        }

        _lastContactRecorded[key] = now;
        await _store.UpsertRealmSyncStateAsync(tenantId, realmId, now, lastWriteAt: null, ct).ConfigureAwait(false);
    }

    public ValueTask<bool> IsSyncStaleAsync(
        TenantId tenantId,
        IdentityRealm realm,
        ScimRealmSyncState? state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(realm);

        if (state?.LastContactAt is not { } lastContact)
        {
            return ValueTask.FromResult(true);
        }

        var now = _timeProvider.GetUtcNow();
        var maxAge = TimeSpan.FromMinutes(Math.Max(1, realm.ScimSettings.MaxSyncAgeMinutes));
        return ValueTask.FromResult((now - lastContact) > maxAge);
    }
}

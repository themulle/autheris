namespace Autheris.Application.Identity.Caching;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Identity;
using BitFaster.Caching.Lru;

public interface IRoleResolverCache
{
    ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default);
    ValueTask SetAsync(string realmId, string userKey, RoleResolution resolution, CancellationToken ct = default);
    void Invalidate(string realmId, string userKey);
    void InvalidateAll();
}

public sealed class RoleResolverCache : IRoleResolverCache, IGovernanceCacheInvalidator, IDisposable
{
    private const int MaxCacheKeyLength = IdentityGroupsChangedChannel.MaxKeyLength + 32;
    private readonly ConcurrentLru<(string RealmId, string UserKey), RoleResolution> _l1Cache;
    private readonly byte[] _hmacKey;
    private readonly TimeProvider _timeProvider;
    private readonly IDisposable? _governanceSubscription;

    public RoleResolverCache(
        int capacity = 10000,
        byte[]? hmacKey = null,
        TimeProvider? timeProvider = null,
        IGovernanceChangeNotifier? changeNotifier = null)
    {
        _l1Cache = new ConcurrentLru<(string RealmId, string UserKey), RoleResolution>(capacity);
        _hmacKey = hmacKey ?? RandomNumberGenerator.GetBytes(32);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _governanceSubscription = changeNotifier?.Subscribe(OnGovernanceChange);
    }

    private void OnGovernanceChange(GovernanceChangeEvent change)
    {
        if (change.IsFullInvalidation || !string.IsNullOrEmpty(change.GroupId))
        {
            InvalidateAll();
            return;
        }

        if (!string.IsNullOrEmpty(change.SubjectKey))
        {
            if (!string.IsNullOrEmpty(change.RealmId))
            {
                Invalidate(change.RealmId, change.SubjectKey);
            }
            else
            {
                foreach (var entry in _l1Cache)
                {
                    if (string.Equals(entry.Key.UserKey, change.SubjectKey, StringComparison.Ordinal)
                        || IsEpochCacheKeyForUser(entry.Key.UserKey, change.SubjectKey))
                    {
                        _l1Cache.TryRemove(entry.Key);
                    }
                }
            }
        }
    }

    public void InvalidateTenant(string tenantId) => InvalidateAll();

    public void Dispose() => _governanceSubscription?.Dispose();

    private static bool IsWithinBounds(string realmId, string userKey)
    {
        if (string.IsNullOrEmpty(realmId) || string.IsNullOrEmpty(userKey)
            || realmId.Length > IdentityGroupsChangedChannel.MaxKeyLength
            || userKey.Length > MaxCacheKeyLength)
        {
            return false;
        }

        if (TryStripEpochPrefix(userKey, out var unwrapped))
        {
            return unwrapped.Length > 0 && unwrapped.Length <= IdentityGroupsChangedChannel.MaxKeyLength;
        }

        var rawLength = userKey.StartsWith("role:", StringComparison.Ordinal) ? userKey.Length - 5 : userKey.Length;
        return rawLength > 0 && rawLength <= IdentityGroupsChangedChannel.MaxKeyLength;
    }

    public ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default)
    {
        if (!IsWithinBounds(realmId, userKey))
        {
            return ValueTask.FromResult<RoleResolution?>(null);
        }

        var key = (realmId, userKey);
        if (_l1Cache.TryGet(key, out var resolution))
        {
            if (resolution.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                // Expired: fail closed, never serve past ExpiresAt
                _l1Cache.TryRemove(key);
                return ValueTask.FromResult<RoleResolution?>(null);
            }
            return ValueTask.FromResult<RoleResolution?>(resolution);
        }

        return ValueTask.FromResult<RoleResolution?>(null);
    }

    public ValueTask SetAsync(string realmId, string userKey, RoleResolution resolution, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(resolution);

        // Over-long or empty keys are never cached (bounded key space; nothing to log raw).
        if (!IsWithinBounds(realmId, userKey) || resolution.IsDegraded)
        {
            return ValueTask.CompletedTask;
        }

        var key = (realmId, userKey);
        _l1Cache.AddOrUpdate(key, resolution);
        return ValueTask.CompletedTask;
    }

    public void Invalidate(string realmId, string userKey)
    {
        _l1Cache.TryRemove((realmId, userKey));
        if (!string.IsNullOrEmpty(userKey))
        {
            _l1Cache.TryRemove((realmId, $"role:{userKey}"));
        }

        if (string.IsNullOrEmpty(realmId) || string.IsNullOrEmpty(userKey))
        {
            return;
        }

        foreach (var entry in _l1Cache)
        {
            if (string.Equals(entry.Key.RealmId, realmId, StringComparison.Ordinal)
                && IsEpochCacheKeyForUser(entry.Key.UserKey, userKey))
            {
                _l1Cache.TryRemove(entry.Key);
            }
        }
    }

    private static bool IsEpochCacheKeyForUser(string cachedKey, string userKey) =>
        TryStripEpochPrefix(cachedKey, out var unwrapped) && unwrapped.Equals(userKey.AsSpan(), StringComparison.Ordinal);

    private static bool TryStripEpochPrefix(string key, out ReadOnlySpan<char> unwrapped)
    {
        unwrapped = default;
        if (!key.StartsWith("role:e", StringComparison.Ordinal) && !key.StartsWith("scim:e", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = key.AsSpan(6);
        var colonIdx = rest.IndexOf(':');
        if (colonIdx <= 0)
        {
            return false;
        }

        for (var i = 0; i < colonIdx; i++)
        {
            if (rest[i] is < '0' or > '9')
            {
                return false;
            }
        }

        unwrapped = rest[(colonIdx + 1)..];
        return true;
    }

    public void InvalidateAll()
    {
        _l1Cache.Clear();
    }

    public byte[] FrameWithHmac(string payload)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(_hmacKey);
        var hash = hmac.ComputeHash(payloadBytes);

        var framed = new byte[hash.Length + payloadBytes.Length];
        Buffer.BlockCopy(hash, 0, framed, 0, hash.Length);
        Buffer.BlockCopy(payloadBytes, 0, framed, hash.Length, payloadBytes.Length);
        return framed;
    }

    public bool TryVerifyHmac(ReadOnlySpan<byte> framed, out string? payload)
    {
        payload = null;
        if (framed.Length <= 32)
        {
            return false;
        }

        var hash = framed[..32];
        var payloadSpan = framed[32..];

        using var hmac = new HMACSHA256(_hmacKey);
        var expectedHash = hmac.ComputeHash(payloadSpan.ToArray());

        if (!CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            return false;
        }

        payload = Encoding.UTF8.GetString(payloadSpan);
        return true;
    }
}

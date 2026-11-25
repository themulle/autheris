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
    private const int MaxCacheKeyLength = IdentityGroupsChangedChannel.MaxKeyLength + 128;
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
        if (change.IsFullInvalidation)
        {
            InvalidateAll();
            return;
        }

        if (!string.IsNullOrEmpty(change.GroupId))
        {
            InvalidateTenant(change.TenantId);
            return;
        }

        if (!string.IsNullOrEmpty(change.SubjectKey))
        {
            foreach (var entry in _l1Cache)
            {
                if (!MatchesTenantScope(entry.Key.UserKey, change.TenantId))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(change.RealmId)
                    && !string.Equals(entry.Key.RealmId, change.RealmId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (MatchesUserKey(entry.Key.UserKey, change.SubjectKey))
                {
                    _l1Cache.TryRemove(entry.Key);
                }
            }
        }
    }

    public void InvalidateTenant(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            InvalidateAll();
            return;
        }

        foreach (var entry in _l1Cache)
        {
            if (MatchesTenantScope(entry.Key.UserKey, tenantId))
            {
                _l1Cache.TryRemove(entry.Key);
            }
        }
    }

    public void Dispose() => _governanceSubscription?.Dispose();

    private static bool IsWithinBounds(string realmId, string userKey)
    {
        if (string.IsNullOrEmpty(realmId) || string.IsNullOrEmpty(userKey)
            || realmId.Length > IdentityGroupsChangedChannel.MaxKeyLength
            || userKey.Length > MaxCacheKeyLength)
        {
            return false;
        }

        var hasTenant = TryParseCacheKey(userKey, out _, out var afterTenant);
        _ = hasTenant;
        var effectiveSpan = TryStripEpochPrefix(afterTenant, out var unwrapped) ? unwrapped : afterTenant;
        return effectiveSpan.Length > 0 && effectiveSpan.Length <= IdentityGroupsChangedChannel.MaxKeyLength;
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
                && MatchesUserKey(entry.Key.UserKey, userKey))
            {
                _l1Cache.TryRemove(entry.Key);
            }
        }
    }

    private static bool MatchesTenantScope(string cachedKey, string tenantId) =>
        !TryParseCacheKey(cachedKey, out var entryTenant, out _)
        || entryTenant.Equals(tenantId.AsSpan(), StringComparison.Ordinal);

    private static bool MatchesUserKey(string cachedKey, string userKey)
    {
        var target = userKey.AsSpan();
        if (cachedKey.AsSpan().Equals(target, StringComparison.Ordinal))
        {
            return true;
        }

        _ = TryParseCacheKey(cachedKey, out _, out var afterTenant);
        if (afterTenant.Equals(target, StringComparison.Ordinal))
        {
            return true;
        }

        return TryStripEpochPrefix(afterTenant, out var unwrapped) && unwrapped.Equals(target, StringComparison.Ordinal);
    }

    private static bool TryParseCacheKey(string key, out ReadOnlySpan<char> tenantId, out ReadOnlySpan<char> afterTenant)
    {
        tenantId = default;
        var span = key.AsSpan();
        var hasKindPrefix = span.StartsWith("role:", StringComparison.Ordinal) || span.StartsWith("scim:", StringComparison.Ordinal);
        if (!hasKindPrefix)
        {
            afterTenant = span;
            return false;
        }

        var afterKind = span[5..];
        if (afterKind.Length >= 3 && afterKind[0] == 't')
        {
            var colonIdx = afterKind[1..].IndexOf(':');
            if (colonIdx > 0)
            {
                var candidateTenant = afterKind.Slice(1, colonIdx);
                if (IsValidTenantSegment(candidateTenant))
                {
                    tenantId = candidateTenant;
                    afterTenant = afterKind[(colonIdx + 2)..];
                    return true;
                }
            }
        }

        afterTenant = afterKind;
        return false;
    }

    private static bool IsValidTenantSegment(ReadOnlySpan<char> segment)
    {
        if (segment.Length is < 1 or > 64 || segment[0] == '_')
        {
            return false;
        }

        foreach (var ch in segment)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryStripEpochPrefix(ReadOnlySpan<char> span, out ReadOnlySpan<char> unwrapped)
    {
        unwrapped = default;
        if (span.Length < 3 || span[0] != 'e')
        {
            return false;
        }

        var rest = span[1..];
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

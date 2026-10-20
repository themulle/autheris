namespace Autheris.Application.Identity.Caching;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Identity;
using BitFaster.Caching.Lru;

public interface IRoleResolverCache
{
    ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default);
    ValueTask SetAsync(string realmId, string userKey, RoleResolution resolution, CancellationToken ct = default);
    void Invalidate(string realmId, string userKey);
    void InvalidateAll();
}

public sealed class RoleResolverCache : IRoleResolverCache
{
    private readonly ConcurrentLru<(string RealmId, string UserKey), RoleResolution> _l1Cache;
    private readonly byte[] _hmacKey;

    public RoleResolverCache(int capacity = 10000, byte[]? hmacKey = null)
    {
        _l1Cache = new ConcurrentLru<(string RealmId, string UserKey), RoleResolution>(capacity);
        _hmacKey = hmacKey ?? RandomNumberGenerator.GetBytes(32);
    }

    public ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default)
    {
        var key = (realmId, userKey);
        if (_l1Cache.TryGet(key, out var resolution))
        {
            if (resolution.ExpiresAt <= DateTimeOffset.UtcNow)
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

        var key = (realmId, userKey);
        _l1Cache.AddOrUpdate(key, resolution);
        return ValueTask.CompletedTask;
    }

    public void Invalidate(string realmId, string userKey)
    {
        _l1Cache.TryRemove((realmId, userKey));
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

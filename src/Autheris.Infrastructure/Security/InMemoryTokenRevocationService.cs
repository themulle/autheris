using System.Collections.Concurrent;
using System.Security.Claims;

namespace Autheris.Infrastructure.Security;

/// <summary>
/// SEC M-14 (GAP-B): Process-local token revocation store. Entries expire at the given <c>until</c> time
/// (token expiry), so the store does not grow unbounded. Used when no Redis/Garnet is configured and as the
/// local layer of <see cref="RedisTokenRevocationService"/>.
/// </summary>
public sealed class InMemoryTokenRevocationService : ITokenRevocationService
{
    private const int CleanupThreshold = 10000;

    private sealed record Entry(DateTimeOffset RevokedAt, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryTokenRevocationService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<bool> IsRevokedAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return ValueTask.FromResult(IsRevoked(principal));
    }

    public Task RevokeAsync(string subjectOrJti, DateTimeOffset until, CancellationToken ct = default)
    {
        Revoke(subjectOrJti, _timeProvider.GetUtcNow(), until);
        return Task.CompletedTask;
    }

    internal bool IsRevoked(ClaimsPrincipal principal)
    {
        var keys = TokenRevocationKeys.GetLookupKeys(principal);
        if (keys.Count == 0 || _entries.IsEmpty)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var issuedAt = TokenRevocationKeys.GetIssuedAt(principal);
        foreach (var key in keys)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                continue;
            }

            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                continue;
            }

            if (TokenRevocationKeys.IsCovered(issuedAt, entry.RevokedAt))
            {
                return true;
            }
        }

        return false;
    }

    internal void Revoke(string subjectOrJti, DateTimeOffset revokedAt, DateTimeOffset until)
    {
        var key = TokenRevocationKeys.Normalize(subjectOrJti);
        if (until <= revokedAt)
        {
            return;
        }

        // A repeated revocation keeps the latest revocation time and the longest retention.
        _entries.AddOrUpdate(
            key,
            new Entry(revokedAt, until),
            (_, existing) => new Entry(
                existing.RevokedAt > revokedAt ? existing.RevokedAt : revokedAt,
                existing.ExpiresAt > until ? existing.ExpiresAt : until));

        if (_entries.Count > CleanupThreshold)
        {
            foreach (var kvp in _entries)
            {
                if (kvp.Value.ExpiresAt <= revokedAt)
                {
                    _entries.TryRemove(kvp);
                }
            }
        }
    }
}

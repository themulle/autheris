namespace Autheris.Extensions.DataCatalog;

using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// SEC ARCH-MNT-17: process-wide, single-slot cache of the Purview access token. The slot is bound to the credential set
/// (tenant, client id, SHA-256 of the configured secret) so a changed credential never receives a token minted for another one.
/// The key uses the configured secret (or secret reference), not the resolved Key Vault value: a rotated value behind an
/// unchanged reference keeps the old token until it expires (minus the 60 s skew) or the options change.
/// The token lives in memory only; neither the token, the secret nor the fingerprint is logged or placed in an exception.
/// </summary>
public sealed class PurviewTokenCache : IDisposable
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan LockWaitLimit = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly IDisposable? _changeRegistration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _slotLock = new();
    private Slot? _slot;
    private long _generation;

    /// <param name="time">Clock for expiry.</param>
    /// <param name="subscribeToOptionsChange">Registers the callback that runs when the gateway options change.</param>
    public PurviewTokenCache(TimeProvider time, Func<Action, IDisposable?>? subscribeToOptionsChange = null)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _changeRegistration = subscribeToOptionsChange?.Invoke(Invalidate);
    }

    internal bool HasCachedToken
    {
        get { lock (_slotLock) { return _slot is not null; } }
    }

    /// <summary>Derives the opaque slot key; the secret only enters the hash.</summary>
    internal static string ComputeKey(string tenantId, string clientId, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes($"{tenantId}\0{clientId}\0{secret}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    /// <summary>Drops the slot and invalidates every fetch that started before this call.</summary>
    internal void Invalidate()
    {
        lock (_slotLock)
        {
            _generation++;
            _slot = null;
        }
    }

    public async Task<string> GetOrFetchAsync(
        string key,
        Func<CancellationToken, Task<(string Token, TimeSpan ExpiresIn)>> fetch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(fetch);

        if (TryGetFresh(key, out var cached))
        {
            return cached;
        }

        if (!await _gate.WaitAsync(LockWaitLimit, ct).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out waiting for the Purview token refresh.");
        }

        try
        {
            if (TryGetFresh(key, out cached))
            {
                return cached;
            }

            long generation;
            lock (_slotLock)
            {
                generation = _generation;
            }

            // A failed fetch throws before anything is stored; a stale slot is never served as a fallback.
            var (token, expiresIn) = await fetch(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("The Purview token endpoint returned no access token.");
            }

            var expiry = _time.GetUtcNow() + expiresIn - Skew;
            lock (_slotLock)
            {
                // SC-03: only store when the options did not change while the fetch was in flight.
                if (generation == _generation && expiry > _time.GetUtcNow())
                {
                    _slot = new Slot(key, token, expiry);
                }
            }

            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _changeRegistration?.Dispose();
        _gate.Dispose();
    }

    private bool TryGetFresh(string key, out string token)
    {
        lock (_slotLock)
        {
            if (_slot is { } s && string.Equals(s.Key, key, StringComparison.Ordinal) && _time.GetUtcNow() < s.Expiry)
            {
                token = s.Token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    private sealed record Slot(string Key, string Token, DateTimeOffset Expiry)
    {
        public override string ToString() => nameof(Slot);
    }
}

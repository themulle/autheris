namespace Autheris.Application.Streaming.Crypto;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Result of <see cref="CdcStreamKeyring.ClassifyDek"/> (INF-13).</summary>
public enum CdcDekClassification
{
    Readable = 0,
    Unreadable = 1,
    Indeterminate = 2
}

/// <summary>A data-encryption key checked out of the keyring. <see cref="Key"/> is a private copy owned by the caller, who zeroes it after use.</summary>
public readonly record struct CdcDek(string DekId, byte[] Key);

/// <summary>
/// Per-stream data-encryption keys (PLAN-CDC-99 D-A8): creation, wrapping under the active KEK, rotation by age and by nonce budget
/// (SEC-CDC99-15), a bounded LRU cache of unwrapped keys that are zeroed on eviction (SEC-CDC99-14), and re-wrapping after a KEK rotation.
/// Every failure surfaces as <see cref="CdcStreamKeyUnavailableException"/> with a stable code only (fail closed).
/// </summary>
public sealed class CdcStreamKeyring : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DekSize = 32;

    private readonly ICdcStreamKeyStore _store;
    private readonly ICdcKeyEncryptionKeyProvider _keks;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheGate = new();
    private readonly LinkedList<(string Key, byte[] Dek)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Dek)>> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _usage = new(StringComparer.Ordinal);

    public CdcStreamKeyring(ICdcStreamKeyStore store, ICdcKeyEncryptionKeyProvider keks, IOptionsMonitor<GatewayOptions> options, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _keks = keks ?? throw new ArgumentNullException(nameof(keks));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
    }

    private CdcStreamEncryptionOptions Opt => _options.CurrentValue.CdcStream.Encryption;

    /// <summary>
    /// Checks out the DEK for exactly one encryption (counts against the nonce budget). Rotates when the key is older than
    /// <c>DekRotationHours</c> or has encrypted <c>MaxEventsPerDek</c> payloads in this process (SEC-CDC99-15). Image keys are bounded the same way.
    /// </summary>
    public async ValueTask<CdcDek> AcquireForEncryptionAsync(CdcStreamId id, CdcKeyPurpose purpose, CancellationToken ct = default)
    {
        if (purpose == CdcKeyPurpose.Fingerprint)
        {
            return await GetFingerprintKeyAsync(id, ct).ConfigureAwait(false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await FindActiveAsync(id, purpose, ct).ConfigureAwait(false);
            if (active is not null && !NeedsRotation(active))
            {
                var dek = Unwrap(active);
                _usage[UsageKey(id, active.DekId)] = _usage.GetValueOrDefault(UsageKey(id, active.DekId)) + 1;
                return new CdcDek(active.DekId, (byte[])dek.Clone());
            }

            if (active is not null)
            {
                await _store.RetireKeyAsync(id, active.DekId, _time.GetUtcNow(), ct).ConfigureAwait(false);
            }

            var created = await CreateAsync(id, purpose, ct).ConfigureAwait(false);
            _usage[UsageKey(id, created.DekId)] = 1;
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The stable HMAC key of the key fingerprint (created on first use, never rotated).</summary>
    public async ValueTask<CdcDek> GetFingerprintKeyAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = (await SafeGetKeysAsync(id, ct).ConfigureAwait(false))
                .Where(k => k.Purpose == CdcKeyPurpose.Fingerprint)
                .OrderBy(k => k.CreatedAt)
                .FirstOrDefault();
            return existing is not null ? new CdcDek(existing.DekId, (byte[])Unwrap(existing).Clone()) : await CreateAsync(id, CdcKeyPurpose.Fingerprint, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The unwrapped DEK referenced by a stored event or image (decryption never creates keys).</summary>
    public async ValueTask<byte[]> GetDekAsync(CdcStreamId id, string dekId, CancellationToken ct = default)
    {
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(CacheKey(id, dekId), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return (byte[])node.Value.Dek.Clone();
            }
        }

        var keys = await SafeGetKeysAsync(id, ct).ConfigureAwait(false);
        var wrapped = keys.FirstOrDefault(k => string.Equals(k.DekId, dekId, StringComparison.Ordinal))
            ?? throw new CdcStreamKeyUnavailableException();
        return (byte[])Unwrap(wrapped).Clone();
    }

    /// <summary>Retires the active image key so that the next image write creates a fresh one (admin-triggered rotation).</summary>
    public async ValueTask<string?> RotateImageKeyAsync(CdcStreamId id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var active = await FindActiveAsync(id, CdcKeyPurpose.Image, ct).ConfigureAwait(false);
            if (active is not null)
            {
                await _store.RetireKeyAsync(id, active.DekId, _time.GetUtcNow(), ct).ConfigureAwait(false);
            }

            return (await CreateAsync(id, CdcKeyPurpose.Image, ct).ConfigureAwait(false)).DekId;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-wraps every DEK of the stream that is wrapped under a KEK other than the active one. Returns the number re-wrapped.</summary>
    public async ValueTask<int> RewrapAsync(CdcStreamId id, CancellationToken ct = default)
    {
        var activeId = _keks.ActiveKekId;
        if (activeId is null || !_keks.TryGetKek(activeId, out _))
        {
            return 0;
        }

        var count = 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var key in await SafeGetKeysAsync(id, ct).ConfigureAwait(false))
            {
                if (string.Equals(key.KekId, activeId, StringComparison.Ordinal))
                {
                    continue;
                }

                // An old KEK that was already removed makes the key unreadable: it stays as is (fail closed) and is reported by the caller.
                if (!_keks.TryGetKek(key.KekId, out _))
                {
                    continue;
                }

                var dek = Unwrap(key);
                var rewrapped = Wrap(id, key.Purpose, key.DekId, dek, activeId);
                await _store.ReplaceWrappingAsync(id, key.DekId, activeId, rewrapped, ct).ConfigureAwait(false);
                count++;
            }
        }
        finally
        {
            _gate.Release();
        }

        return count;
    }

    public void Dispose()
    {
        lock (_cacheGate)
        {
            foreach (var entry in _lru)
            {
                CryptographicOperations.ZeroMemory(entry.Dek);
            }

            _lru.Clear();
            _cache.Clear();
        }

        _gate.Dispose();
    }

    private bool NeedsRotation(CdcWrappedKey active)
    {
        var o = Opt;
        if (o.DekRotationHours > 0 && _time.GetUtcNow() - active.CreatedAt >= TimeSpan.FromHours(o.DekRotationHours))
        {
            return true;
        }

        return _usage.GetValueOrDefault(UsageKey(active.StreamId, active.DekId)) >= Math.Max(1, o.MaxEventsPerDek);
    }

    private async ValueTask<CdcWrappedKey?> FindActiveAsync(CdcStreamId id, CdcKeyPurpose purpose, CancellationToken ct) =>
        (await SafeGetKeysAsync(id, ct).ConfigureAwait(false))
            .Where(k => k.Purpose == purpose && k.RetiredAt is null)
            .OrderByDescending(k => k.CreatedAt)
            .FirstOrDefault();

    private async ValueTask<IReadOnlyList<CdcWrappedKey>> SafeGetKeysAsync(CdcStreamId id, CancellationToken ct)
    {
        try
        {
            return await _store.GetKeysAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CdcStreamKeyUnavailableException(CdcActivationCodes.KeyUnavailable, ex);
        }
    }

    private async ValueTask<CdcDek> CreateAsync(CdcStreamId id, CdcKeyPurpose purpose, CancellationToken ct)
    {
        var activeKek = _keks.ActiveKekId;
        if (activeKek is null || !_keks.TryGetKek(activeKek, out _))
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var dek = RandomNumberGenerator.GetBytes(DekSize);
        var dekId = Guid.NewGuid().ToString("N");
        var wrapped = new CdcWrappedKey(id, dekId, purpose, activeKek, Wrap(id, purpose, dekId, dek, activeKek), _time.GetUtcNow(), null);
        bool added;
        try
        {
            added = await _store.TryAddKeyAsync(wrapped, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new CdcStreamKeyUnavailableException(CdcActivationCodes.KeyUnavailable, ex);
        }

        if (!added)
        {
            // A concurrent node won: use its key (re-read), never ours.
            CryptographicOperations.ZeroMemory(dek);
            var winner = purpose == CdcKeyPurpose.Fingerprint
                ? (await SafeGetKeysAsync(id, ct).ConfigureAwait(false)).Where(k => k.Purpose == purpose).OrderBy(k => k.CreatedAt).FirstOrDefault()
                : await FindActiveAsync(id, purpose, ct).ConfigureAwait(false);
            return winner is null ? throw new CdcStreamKeyUnavailableException() : new CdcDek(winner.DekId, (byte[])Unwrap(winner).Clone());
        }

        Cache(id, dekId, dek);
        return new CdcDek(dekId, (byte[])dek.Clone());
    }

    /// <summary>True only when the active KEK is positively resolved at full length. Any failure (including exceptions) is false (INF-13).</summary>
    public bool IsActiveKekAvailable()
    {
        try
        {
            var active = _keks.ActiveKekId;
            return active is not null && _keks.ResolveKek(active, out _) == CdcKekLookup.Available;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// INF-13: classifies a wrapped DEK without surfacing key material. <see cref="CdcDekClassification.Unreadable"/> only when the loss is
    /// proven permanent (KEK id removed while the active KEK resolves, malformed blob, or AES-GCM failure under a fully resolved KEK);
    /// everything else, including every unexpected exception, is <see cref="CdcDekClassification.Indeterminate"/>. Cancellation propagates.
    /// </summary>
    public CdcDekClassification ClassifyDek(CdcWrappedKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!IsActiveKekAvailable())
            {
                return CdcDekClassification.Indeterminate;
            }

            if (key.WrappedKey is null || key.WrappedKey.Length < NonceSize + TagSize)
            {
                return CdcDekClassification.Unreadable;
            }

            switch (_keks.ResolveKek(key.KekId, out var kek))
            {
                case CdcKekLookup.Unknown:
                    return CdcDekClassification.Unreadable;
                case CdcKekLookup.Available when kek.Length > 0:
                    break;
                default:
                    return CdcDekClassification.Indeterminate;
            }

            var dek = new byte[key.WrappedKey.Length - NonceSize - TagSize];
            try
            {
                using var aes = new AesGcm(kek.Span, TagSize);
                aes.Decrypt(
                    key.WrappedKey.AsSpan(0, NonceSize), key.WrappedKey.AsSpan(NonceSize + TagSize), key.WrappedKey.AsSpan(NonceSize, TagSize),
                    dek, DekAad(key.StreamId, key.Purpose, key.DekId));
                return CdcDekClassification.Readable;
            }
            catch (AuthenticationTagMismatchException)
            {
                return CdcDekClassification.Unreadable;
            }
            catch (CryptographicException)
            {
                // A KEK of an unexpected size or other crypto failure is not proof of permanent loss.
                return CdcDekClassification.Indeterminate;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return CdcDekClassification.Indeterminate;
        }
    }

    private byte[] Unwrap(CdcWrappedKey key)
    {
        var cacheKey = CacheKey(key.StreamId, key.DekId);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(cacheKey, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Dek;
            }
        }

        if (!_keks.TryGetKek(key.KekId, out var kek) || key.WrappedKey is null || key.WrappedKey.Length < NonceSize + TagSize)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var dek = new byte[key.WrappedKey.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(kek.Span, TagSize);
            aes.Decrypt(
                key.WrappedKey.AsSpan(0, NonceSize), key.WrappedKey.AsSpan(NonceSize + TagSize), key.WrappedKey.AsSpan(NonceSize, TagSize),
                dek, DekAad(key.StreamId, key.Purpose, key.DekId));
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dek);
            throw new CdcStreamKeyUnavailableException();
        }

        Cache(key.StreamId, key.DekId, dek);
        return dek;
    }

    private byte[] Wrap(CdcStreamId id, CdcKeyPurpose purpose, string dekId, byte[] dek, string kekId)
    {
        if (!_keks.TryGetKek(kekId, out var kek))
        {
            throw new CdcStreamKeyUnavailableException();
        }

        var blob = new byte[NonceSize + TagSize + dek.Length];
        RandomNumberGenerator.Fill(blob.AsSpan(0, NonceSize));
        try
        {
            using var aes = new AesGcm(kek.Span, TagSize);
            aes.Encrypt(blob.AsSpan(0, NonceSize), dek, blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), DekAad(id, purpose, dekId));
        }
        catch (CryptographicException)
        {
            throw new CdcStreamKeyUnavailableException();
        }

        return blob;
    }

    private void Cache(CdcStreamId id, string dekId, byte[] dek)
    {
        var key = CacheKey(id, dekId);
        lock (_cacheGate)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _cache.Remove(key);
                if (!ReferenceEquals(existing.Value.Dek, dek))
                {
                    CryptographicOperations.ZeroMemory(existing.Value.Dek);
                }
            }

            _cache[key] = _lru.AddFirst((key, dek));
            var max = Math.Max(1, Opt.MaxCachedDeks);
            while (_lru.Count > max)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _cache.Remove(last.Value.Key);
                CryptographicOperations.ZeroMemory(last.Value.Dek);
            }
        }
    }

    /// <summary>AAD binding a wrapped DEK to its stream, purpose and id.</summary>
    public static byte[] DekAad(CdcStreamId id, CdcKeyPurpose purpose, string dekId) =>
        Encoding.UTF8.GetBytes($"cdc-dek|{id.Value}|{purpose.ToString().ToLowerInvariant()}|{dekId}");

    private static string CacheKey(CdcStreamId id, string dekId) => $"{id.Value}/{dekId}";

    private static string UsageKey(CdcStreamId id, string dekId) => CacheKey(id, dekId);
}

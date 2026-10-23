namespace Autheris.Application.Governance.TrustedDocuments;

using System.Collections.Frozen;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>Immutable view of the active app-scoped trusted documents (Issue #281). Lookup is by the exact <see cref="AppIdentity"/>.</summary>
public sealed class TrustedDocumentAppSnapshot
{
    private readonly FrozenDictionary<AppIdentity, FrozenSet<string>> _hashes;
    private readonly FrozenDictionary<string, FrozenSet<string>> _origins;

    public TrustedDocumentAppSnapshot(TrustedDocumentStoreSnapshot stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        Generation = stored.Generation;
        _hashes = stored.ActiveVersions.ToFrozenDictionary(
            v => new AppIdentity(v.AppId, v.Version), v => v.Hashes.ToFrozenSet(StringComparer.Ordinal));
        _origins = stored.ActiveVersions
            .GroupBy(v => v.AppId, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First().Origins.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public long Generation { get; }

    /// <summary>The version is active and <paramref name="lowerCaseHash"/> is one of its hashes.</summary>
    public bool IsDocumentTrusted(AppIdentity app, string lowerCaseHash) =>
        _hashes.TryGetValue(app, out var set) && set.Contains(lowerCaseHash);

    /// <summary><paramref name="normalizedOrigin"/> is registered for the app (exact ordinal match of the normal form).</summary>
    public bool IsOriginAllowed(string appId, string normalizedOrigin) =>
        _origins.TryGetValue(appId, out var set) && set.Contains(normalizedOrigin);
}

public interface ITrustedDocumentAppRegistry
{
    /// <summary>The current snapshot. Throws when the store cannot be read: callers must reject (fail closed, SEC-281-7).</summary>
    ValueTask<TrustedDocumentAppSnapshot> GetAsync(CancellationToken ct);

    /// <summary>Makes the next <see cref="GetAsync"/> check the store at once (a local write is effective immediately).</summary>
    void Invalidate();
}

/// <summary>
/// Singleton registry: the generation row is compared at most every <c>CacheRefreshSeconds</c> (changes of other instances); the writing
/// instance invalidates synchronously. Refresh is single-flight (one store read per interval, no stampede). A failed check clears the
/// snapshot, so nothing stale is served once a refresh is due and has failed (SEC-281-7).
/// </summary>
public sealed class TrustedDocumentAppRegistry : ITrustedDocumentAppRegistry, IDisposable
{
    private readonly ITrustedDocumentRepository _repository;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private TrustedDocumentAppSnapshot? _snapshot;
    private long _checkedAt;
    private volatile bool _invalidated = true;

    public TrustedDocumentAppRegistry(ITrustedDocumentRepository repository, IOptions<GatewayOptions>? options = null, TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _time = time ?? TimeProvider.System;
        _interval = TimeSpan.FromSeconds((options?.Value ?? new GatewayOptions()).GraphQL.AppScopedTrustedDocuments.CacheRefreshSeconds);
    }

    public void Invalidate() => _invalidated = true;

    public void Dispose() => _reload.Dispose();

    public async ValueTask<TrustedDocumentAppSnapshot> GetAsync(CancellationToken ct)
    {
        if (TryFresh(out var fresh))
        {
            return fresh;
        }

        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TryFresh(out fresh))
            {
                return fresh;   // another caller refreshed while this one waited (single flight)
            }

            _invalidated = false;   // an Invalidate() during the load sets it again
            long generation = await _repository.GetTrustedDocumentGenerationAsync(ct).ConfigureAwait(false);
            var current = _snapshot;
            if (current == null || current.Generation != generation)
            {
                current = _snapshot = new TrustedDocumentAppSnapshot(await _repository.LoadTrustedDocumentSnapshotAsync(ct).ConfigureAwait(false));
            }

            Interlocked.Exchange(ref _checkedAt, _time.GetTimestamp());
            return current;
        }
        catch
        {
            _snapshot = null;       // nothing stale is kept after a failed check
            _invalidated = true;
            throw;
        }
        finally
        {
            _reload.Release();
        }
    }

    private bool TryFresh(out TrustedDocumentAppSnapshot snapshot)
    {
        var current = _snapshot;
        if (current != null && !_invalidated && _time.GetElapsedTime(Interlocked.Read(ref _checkedAt), _time.GetTimestamp()) < _interval)
        {
            snapshot = current;
            return true;
        }

        snapshot = null!;
        return false;
    }
}

namespace Autheris.Application.VirtualFilters;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>
/// Keeps the current virtual filter snapshot. The generation is compared at most every
/// <see cref="VirtualFilterOptions.GenerationCheckSeconds"/> (changes from other instances arrive within that time);
/// a local write invalidates immediately. A failed check keeps nothing stale: it throws, and the resolver denies.
/// </summary>
public sealed class VirtualFilterSnapshotProvider : IVirtualFilterSnapshotProvider, IDisposable
{
    private readonly IVirtualFilterRepository _repository;
    private readonly TimeSpan _checkInterval;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private VirtualFilterSnapshot? _snapshot;
    private long _checkedAtTicks;
    private volatile bool _invalidated = true;

    public VirtualFilterSnapshotProvider(IVirtualFilterRepository repository, IOptions<GatewayOptions>? options = null, TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _checkInterval = TimeSpan.FromSeconds(Math.Max(0, options?.Value?.VirtualFilters?.GenerationCheckSeconds ?? 5));
        _time = time ?? TimeProvider.System;
    }

    public void Invalidate() => _invalidated = true;

    public void Dispose() => _reload.Dispose();

    public async ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default)
    {
        var current = _snapshot;
        long now = _time.GetTimestamp();
        if (current != null && !_invalidated && _time.GetElapsedTime(Interlocked.Read(ref _checkedAtTicks), now) < _checkInterval)
        {
            return current;
        }

        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _invalidated = false;
            long generation = await _repository.GetGenerationAsync(ct).ConfigureAwait(false);
            if (_snapshot == null || _snapshot.Generation != generation)
            {
                _snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _checkedAtTicks, _time.GetTimestamp());
            return _snapshot;
        }
        finally
        {
            _reload.Release();
        }
    }
}

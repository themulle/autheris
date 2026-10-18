namespace Autheris.Application.Identity.Attributes;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// The active registry as an immutable snapshot (A-7). The generation is compared at most every few seconds (changes of other instances),
/// a local write invalidates at once. A failed reload throws: the resolver treats that as a failed provider call and the filter denies.
/// </summary>
public sealed class CallerAttributeRegistry : ICallerAttributeRegistry, IDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly ICallerAttributeRepository _repository;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private CallerAttributeSnapshot? _snapshot;
    private long _checkedAt;
    private volatile bool _invalidated = true;

    public CallerAttributeRegistry(ICallerAttributeRepository repository, IOptions<GatewayOptions>? options = null, TimeProvider? time = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _ = options;
        _time = time ?? TimeProvider.System;
    }

    public void Invalidate() => _invalidated = true;

    public void Dispose() => _reload.Dispose();

    public async ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct)
    {
        var current = _snapshot;
        long now = _time.GetTimestamp();
        if (current != null && !_invalidated && _time.GetElapsedTime(Interlocked.Read(ref _checkedAt), now) < CheckInterval)
        {
            return current;
        }

        await _reload.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _invalidated = false;
            long generation = await _repository.GetCallerAttributeGenerationAsync(ct).ConfigureAwait(false);
            if (_snapshot == null || _snapshot.Generation != generation)
            {
                var stored = await _repository.LoadCallerAttributeSnapshotAsync(ct).ConfigureAwait(false);
                _snapshot = new CallerAttributeSnapshot(
                    stored.Generation,
                    stored.Attributes.Select(a => a.Definition),
                    stored.ServiceSets.Select(s => s.Set));
            }

            Interlocked.Exchange(ref _checkedAt, _time.GetTimestamp());
            return _snapshot;
        }
        catch
        {
            _invalidated = true;   // nothing stale is kept after a failed check
            throw;
        }
        finally
        {
            _reload.Release();
        }
    }
}

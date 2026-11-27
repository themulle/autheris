namespace Autheris.Application.Common.Interfaces;

/// <summary>
/// O10: Bounds concurrent table reads per key. <see cref="TryEnter"/> returns a lease that frees the slot on dispose, or
/// null when <paramref name="maxConcurrent"/> reads with this key are already running. A limit of 0 or less is unlimited.
/// <see cref="EnterAsync(IReadOnlyList{string}, int, TimeSpan, CancellationToken)"/> additionally waits a bounded time
/// (first in, first out) for a free slot.
/// </summary>
public interface ITableReadConcurrencyGate
{
    IDisposable? TryEnter(string key, int maxConcurrent);

    /// <summary>
    /// Acquires one slot for every key (all or nothing, one shared wait budget). Returns one composite lease, or null when
    /// a slot did not become free within <paramref name="maxWait"/>. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> (or the ambient scope lease) is cancelled. The default implementation never waits.
    /// </summary>
    ValueTask<IDisposable?> EnterAsync(IReadOnlyList<string> keys, int maxConcurrent, TimeSpan maxWait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var acquired = new List<IDisposable>(keys.Count);
        foreach (var key in keys)
        {
            var lease = TryEnter(key, maxConcurrent);
            if (lease is null)
            {
                foreach (var held in acquired)
                {
                    held.Dispose();
                }

                return ValueTask.FromResult<IDisposable?>(null);
            }

            acquired.Add(lease);
        }

        return ValueTask.FromResult<IDisposable?>(acquired.Count == 1 ? acquired[0] : new CompositeLease(acquired));
    }

    /// <summary>Single-key convenience for <see cref="EnterAsync(IReadOnlyList{string}, int, TimeSpan, CancellationToken)"/>.</summary>
    ValueTask<IDisposable?> EnterAsync(string key, int maxConcurrent, TimeSpan maxWait, CancellationToken ct) =>
        EnterAsync([key], maxConcurrent, maxWait, ct);

    private sealed class CompositeLease(List<IDisposable> leases) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }
}

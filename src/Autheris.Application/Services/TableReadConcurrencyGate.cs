using Autheris.Application.Interfaces;

namespace Autheris.Application.Services;

/// <summary>
/// O10 (docs/plans/rls-subquery-in-strategy.md): counts running reads per key (tenant, user, table) in this process.
/// Keys disappear when their count drops to zero, so the dictionary only holds keys with running reads.
/// </summary>
public sealed class TableReadConcurrencyGate : ITableReadConcurrencyGate
{
    private static readonly IDisposable Unlimited = new NoopLease();

    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public IDisposable? TryEnter(string key, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (maxConcurrent <= 0)
        {
            return Unlimited;
        }

        lock (_sync)
        {
            _running.TryGetValue(key, out var count);
            if (count >= maxConcurrent)
            {
                return null;
            }
            _running[key] = count + 1;
        }

        return new Lease(this, key);
    }

    private void Release(string key)
    {
        lock (_sync)
        {
            if (!_running.TryGetValue(key, out var count))
            {
                return;
            }
            if (count <= 1)
            {
                _running.Remove(key);
            }
            else
            {
                _running[key] = count - 1;
            }
        }
    }

    private sealed class Lease(TableReadConcurrencyGate gate, string key) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release(key);
            }
        }
    }

    private sealed class NoopLease : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

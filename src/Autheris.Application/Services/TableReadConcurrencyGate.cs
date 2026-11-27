using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;

namespace Autheris.Application.Services;

/// <summary>
/// O10: counts running reads per key (tenant, user, table) in this process and lets further requests wait a bounded
/// time in first-in-first-out order. A key has state only while it has running reads or waiters. Waiters exist only
/// while the key is at its limit; a released slot is handed to the oldest live waiter without ever dropping below
/// the running count, so new callers cannot overtake queued ones.
/// </summary>
public sealed class TableReadConcurrencyGate : ITableReadConcurrencyGate
{
    private static readonly IDisposable Unlimited = new NoopLease();

    private readonly Dictionary<string, KeyState> _states = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;

    public TableReadConcurrencyGate(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IDisposable? TryEnter(string key, int maxConcurrent)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (maxConcurrent <= 0)
        {
            return Unlimited;
        }

        lock (_sync)
        {
            return TryEnterLocked(key, maxConcurrent) ? new Lease(this, key) : null;
        }
    }

    public async ValueTask<IDisposable?> EnterAsync(IReadOnlyList<string> keys, int maxConcurrent, TimeSpan maxWait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (maxConcurrent <= 0)
        {
            return Unlimited;
        }

        if (keys.Count == 1)
        {
            return await EnterKeyAsync(keys[0], maxConcurrent, maxWait, ct).ConfigureAwait(false);
        }

        var ordered = new List<string>(keys.Count);
        foreach (var key in keys)
        {
            if (!ordered.Contains(key, StringComparer.Ordinal))
            {
                ordered.Add(key);
            }
        }

        ordered.Sort(StringComparer.Ordinal);
        var acquired = new List<IDisposable>(ordered.Count);
        var start = _timeProvider.GetTimestamp();
        try
        {
            foreach (var key in ordered)
            {
                var remaining = maxWait - _timeProvider.GetElapsedTime(start);
                var lease = await EnterKeyAsync(key, maxConcurrent, remaining, ct).ConfigureAwait(false);
                if (lease is null)
                {
                    ReleaseAll(acquired);
                    return null;
                }

                acquired.Add(lease);
            }
        }
        catch
        {
            ReleaseAll(acquired);
            throw;
        }

        return new CompositeLease(acquired);
    }

    private static void ReleaseAll(List<IDisposable> leases)
    {
        foreach (var lease in leases)
        {
            lease.Dispose();
        }

        leases.Clear();
    }

    private bool TryEnterLocked(string key, int max)
    {
        if (_states.TryGetValue(key, out var state))
        {
            if (state.Running >= max || state.Waiters.Count > 0)
            {
                return false;
            }

            state.Running++;
            return true;
        }

        _states[key] = new KeyState { Running = 1 };
        return true;
    }

    private async ValueTask<IDisposable?> EnterKeyAsync(string key, int max, TimeSpan maxWait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_sync)
        {
            if (TryEnterLocked(key, max))
            {
                return new Lease(this, key);
            }

            // Fail fast without allocating a wait: no budget, or the queue for this key is already as long as the limit.
            if (maxWait <= TimeSpan.Zero
                || !_states.TryGetValue(key, out var full)
                || full.Waiters.Count >= max)
            {
                return null;
            }

            waiter = new Waiter(key);
            waiter.Node = full.Waiters.AddLast(waiter);
        }

        CancellationTokenSource? timeoutCts = null;
        CancellationTokenRegistration timeoutReg = default, callerReg = default, scopeReg = default;
        try
        {
            timeoutCts = new CancellationTokenSource(maxWait, _timeProvider);
            var scopeToken = ScopeQueryExecutionGuard.TryGetCurrent(out var scope) ? scope.CancellationToken : CancellationToken.None;
            timeoutReg = timeoutCts.Token.Register(() =>
            {
                RemoveWaiter(waiter);
                waiter.Tcs.TrySetResult(null);
            });
            if (ct.CanBeCanceled)
            {
                callerReg = ct.Register(() =>
                {
                    RemoveWaiter(waiter);
                    waiter.Tcs.TrySetCanceled(ct);
                });
            }

            if (scopeToken.CanBeCanceled)
            {
                scopeReg = scopeToken.Register(() =>
                {
                    RemoveWaiter(waiter);
                    waiter.Tcs.TrySetCanceled(scopeToken);
                });
            }

            // Await the source directly: a slot handed over concurrently with a cancel must reach this caller, never be dropped.
            return await waiter.Tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            RemoveWaiter(waiter);
            if (!waiter.Tcs.TrySetCanceled(CancellationToken.None) && waiter.Tcs.Task.IsCompletedSuccessfully)
            {
                // A slot was handed over before the failure: give it back instead of leaking it.
                (await waiter.Tcs.Task.ConfigureAwait(false))?.Dispose();
            }

            throw;
        }
        finally
        {
            timeoutReg.Dispose();
            callerReg.Dispose();
            scopeReg.Dispose();
            timeoutCts?.Dispose();
        }
    }

    private void Release(string key)
    {
        while (true)
        {
            Waiter? next;
            lock (_sync)
            {
                if (!_states.TryGetValue(key, out var state))
                {
                    return;
                }

                if (state.Waiters.First is { } node)
                {
                    next = node.Value;
                    state.Waiters.RemoveFirst();
                    next.Node = null;
                }
                else
                {
                    if (state.Running <= 1)
                    {
                        _states.Remove(key);
                    }
                    else
                    {
                        state.Running--;
                    }

                    return;
                }
            }

            // Completed outside the lock; a waiter that already finished (timeout, cancel) refuses and the next one is tried.
            if (next.Tcs.TrySetResult(new Lease(this, key)))
            {
                return;
            }
        }
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is { List: not null } node && _states.TryGetValue(waiter.Key, out var state))
            {
                state.Waiters.Remove(node);
                waiter.Node = null;
                if (state.Running == 0 && state.Waiters.Count == 0)
                {
                    _states.Remove(waiter.Key);
                }
            }
        }
    }

    private sealed class KeyState
    {
        public int Running;
        public readonly LinkedList<Waiter> Waiters = new();
    }

    private sealed class Waiter(string key)
    {
        public string Key { get; } = key;
        public LinkedListNode<Waiter>? Node;
        public TaskCompletionSource<IDisposable?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private sealed class CompositeLease(List<IDisposable> leases) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseAll(leases);
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

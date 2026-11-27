using Autheris.Application.Common.Interfaces;
using Autheris.Application.Common.Quota;
using Autheris.Domain.Exceptions;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// The read slots of ONE GraphQL operation for one gate key (tenant, user, root table). Root fields of the same operation
/// share the slots the operation already holds instead of competing with each other for the per-user limit: a finishing
/// root field hands its gate lease directly to a waiting sibling. The operation never holds more global leases than the
/// gate allows, because every lease comes from the gate.
/// </summary>
internal sealed class OperationReadSlots : IDisposable
{
    internal const int MaxSiblingWaiters = 16;
    internal const int ThrottledRetryAfterSeconds = 2;

    /// <summary>Upper bound of a sibling wait, in addition to the caller token and the scope lease token.</summary>
    internal static readonly TimeSpan SiblingWaitLimit = TimeSpan.FromSeconds(30);

    private readonly ITableReadConcurrencyGate _gate;
    private readonly string _key;
    private readonly int _max;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _held;
    private bool _disposed;

    public OperationReadSlots(ITableReadConcurrencyGate gate, string key, int max, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(key);
        _gate = gate;
        _key = key;
        _max = max;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a lease for one root field. Throws <see cref="GatewayThrottledException"/> when no slot is available
    /// (gate refusal, wait expiry, waiter cap or disposed pool) and <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(TimeSpan wait, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var global = _gate.TryEnter(_key, _max);
        if (global is not null)
        {
            return Register(global);
        }

        Waiter? sibling = null;
        lock (_sync)
        {
            if (_disposed)
            {
                throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
            }

            // Re-checked under the lock: a sibling that finished since the failed TryEnter must not leave this caller queued behind nobody.
            if (_held > 0)
            {
                if (_waiters.Count >= MaxSiblingWaiters)
                {
                    throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
                }

                sibling = new Waiter();
                sibling.Node = _waiters.AddLast(sibling);
            }
        }

        if (sibling is not null)
        {
            return await WaitForSiblingAsync(sibling, ct).ConfigureAwait(false);
        }

        global = await _gate.EnterAsync(_key, _max, wait, ct).ConfigureAwait(false)
                 ?? throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
        return Register(global);
    }

    public void Dispose()
    {
        List<Waiter> pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = [.. _waiters];
            _waiters.Clear();
            foreach (var waiter in pending)
            {
                waiter.Node = null;
            }
        }

        foreach (var waiter in pending)
        {
            waiter.Tcs.TrySetException(new GatewayThrottledException(ThrottledRetryAfterSeconds));
        }
    }

    private RootLease Register(IDisposable global)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _held++;
                return new RootLease(this, global);
            }
        }

        global.Dispose();
        throw new GatewayThrottledException(ThrottledRetryAfterSeconds);
    }

    private async ValueTask<IDisposable> WaitForSiblingAsync(Waiter waiter, CancellationToken ct)
    {
        CancellationTokenSource? limitCts = null;
        CancellationTokenRegistration limitReg = default, callerReg = default, scopeReg = default;
        try
        {
            limitCts = new CancellationTokenSource(SiblingWaitLimit, _timeProvider);
            var scopeToken = ScopeQueryExecutionGuard.TryGetCurrent(out var scope) ? scope.CancellationToken : CancellationToken.None;
            limitReg = limitCts.Token.Register(() =>
            {
                RemoveWaiter(waiter);
                waiter.Tcs.TrySetException(new GatewayThrottledException(ThrottledRetryAfterSeconds));
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

            // Awaited directly: a lease handed over concurrently with a cancel still reaches this caller.
            return await waiter.Tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            RemoveWaiter(waiter);
            if (!waiter.Tcs.TrySetCanceled(CancellationToken.None) && waiter.Tcs.Task.IsCompletedSuccessfully)
            {
                // A lease was handed over before the failure: give it back instead of leaking the slot.
                (await waiter.Tcs.Task.ConfigureAwait(false)).Dispose();
            }

            throw;
        }
        finally
        {
            limitReg.Dispose();
            callerReg.Dispose();
            scopeReg.Dispose();
            limitCts?.Dispose();
        }
    }

    private void RemoveWaiter(Waiter waiter)
    {
        lock (_sync)
        {
            if (waiter.Node is { List: not null } node)
            {
                _waiters.Remove(node);
                waiter.Node = null;
            }
        }
    }

    private void Release(IDisposable global)
    {
        while (true)
        {
            Waiter? next = null;
            var disposeGlobal = false;
            lock (_sync)
            {
                if (_disposed)
                {
                    disposeGlobal = true;
                }
                else if (_waiters.First is { } node)
                {
                    next = node.Value;
                    _waiters.RemoveFirst();
                    next.Node = null;
                }
                else
                {
                    _held--;
                    disposeGlobal = true;
                }
            }

            if (disposeGlobal)
            {
                global.Dispose();
                return;
            }

            // Completed outside the lock. A waiter that already finished (cancel, limit) refuses: try the next one.
            if (next!.Tcs.TrySetResult(new RootLease(this, global)))
            {
                return;
            }
        }
    }

    private sealed class Waiter
    {
        public LinkedListNode<Waiter>? Node;
        public TaskCompletionSource<IDisposable> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RootLease(OperationReadSlots owner, IDisposable global) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(global);
            }
        }
    }
}

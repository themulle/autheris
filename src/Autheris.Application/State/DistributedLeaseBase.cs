namespace Autheris.Application.State;

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared lease behaviour: heartbeat renewal every ttl/3, fail-closed <see cref="LeaseLost"/> signalling and an
/// idempotent, never-throwing dispose. Backends only supply the atomic extend and release primitives.
/// </summary>
public abstract class DistributedLeaseBase : IDistributedLease
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationToken _lostToken;
    private ITimer? _timer;
    private long _lastConfirmedTicks;
    private int _disposed;
    private int _ticking;

    protected DistributedLeaseBase(TimeProvider time, TimeSpan ttl, string resourceKey, long token, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        _time = time;
        _ttl = ttl;
        _logger = logger;
        _lostToken = _lost.Token;
        ResourceKey = resourceKey;
        Token = token;
        _lastConfirmedTicks = _time.GetUtcNow().UtcTicks;
    }

    public string ResourceKey { get; }

    public long Token { get; }

    public CancellationToken LeaseLost => _lostToken;

    public DateTimeOffset LastConfirmedAt => new(Interlocked.Read(ref _lastConfirmedTicks), TimeSpan.Zero);

    /// <summary>Called by the concrete lease at the end of its constructor to begin heartbeat renewal.</summary>
    protected void StartHeartbeat()
    {
        var period = _ttl / 3;
        _timer = _time.CreateTimer(_ => OnTick(), null, period, period);
    }

    /// <summary>Atomically extends the lease for the owner. False means the lease is no longer held.</summary>
    protected abstract ValueTask<bool> TryExtendAsync(CancellationToken ct);

    /// <summary>Releases the lease if (and only if) it is still held by this owner.</summary>
    protected abstract ValueTask ReleaseAsync();

    /// <summary>Hook for metrics when the lease is declared lost.</summary>
    protected virtual void OnLeaseLost(string reason)
    {
    }

    /// <summary>Hook for metrics when a renewal attempt failed with an infrastructure error.</summary>
    protected virtual void OnRenewFailure()
    {
    }

    private void OnTick()
    {
        // Timer callbacks must never overlap; a slow extend simply skips the next tick.
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _ticking, 1) != 0)
        {
            return;
        }

        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            if (_lost.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                var extended = await TryExtendAsync(CancellationToken.None).ConfigureAwait(false);
                if (extended)
                {
                    Interlocked.Exchange(ref _lastConfirmedTicks, _time.GetUtcNow().UtcTicks);
                    return;
                }

                _logger.LogWarning("Lease {Resource} (token {Token}) was lost: extension rejected by the store.", ResourceKey, Token);
                SignalLost("extend_rejected");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                OnRenewFailure();
                var unconfirmedFor = _time.GetUtcNow() - LastConfirmedAt;
                _logger.LogWarning(ex, "Lease {Resource} (token {Token}) renewal failed; unconfirmed for {Unconfirmed}.", ResourceKey, Token, unconfirmedFor);
                if (unconfirmedFor >= _ttl * 2 / 3)
                {
                    _logger.LogError("Lease {Resource} (token {Token}) is treated as lost: renewal failing beyond two thirds of the ttl.", ResourceKey, Token);
                    SignalLost("renew_unconfirmed");
                }
            }
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private void SignalLost(string reason)
    {
        if (_lost.IsCancellationRequested)
        {
            return;
        }

        OnLeaseLost(reason);
        try
        {
            _lost.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently: nothing left to signal.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_timer is not null)
            {
                await _timer.DisposeAsync().ConfigureAwait(false);
            }

            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Releasing lease {Resource} (token {Token}) failed; it will expire on its own.", ResourceKey, Token);
        }
        finally
        {
            try
            {
                await _lost.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }

            _lost.Dispose();
        }
    }
}

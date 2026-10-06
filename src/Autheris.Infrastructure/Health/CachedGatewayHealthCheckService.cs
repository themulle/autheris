using Autheris.Application.Interfaces;

namespace Autheris.Infrastructure.Health;

/// <summary>
/// SEC R3-4: /health/ready is anonymous and reaches the governance DB (SQLite: behind the repository lock).
/// This decorator serves a short-lived cached report and collapses concurrent callers into a single probe, so the
/// probe rate on the database is bounded by 1/<c>ttl</c> regardless of how many anonymous requests arrive.
/// </summary>
public sealed class CachedGatewayHealthCheckService : IGatewayHealthCheckService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(5);

    private readonly IGatewayHealthCheckService _inner;
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task<GatewayHealthReport>? _current;
    private long _currentStartedAt;

    public CachedGatewayHealthCheckService(IGatewayHealthCheckService inner, TimeSpan? ttl = null, TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ttl = ttl ?? DefaultTtl;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default)
    {
        Task<GatewayHealthReport> task;
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            var expired = _current == null
                          || _current.IsFaulted
                          || _current.IsCanceled
                          || (_current.IsCompleted && _time.GetElapsedTime(_currentStartedAt, now) >= _ttl);
            if (expired)
            {
                // The shared probe must not be tied to the first caller's cancellation token.
                _current = Task.Run(() => _inner.CheckHealthAsync(CancellationToken.None));
                _currentStartedAt = now;
            }

            task = _current!;
        }

        return task.WaitAsync(ct);
    }
}

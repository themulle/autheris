namespace Autheris.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Event bus for broadcast events across cluster nodes.
/// SECURITY NOTE: no delivery guarantee; do not rely on it for security-relevant correctness.
/// Correctness must be ensured through authoritative stores and pull validation (epochs / generations).
/// </summary>
public interface IEventBus
{
    Task PublishAsync<T>(string channel, T message, CancellationToken ct = default);
    IDisposable Subscribe<T>(string channel, Func<T, Task> handler);

    /// <summary>Increments a cluster-wide monotonic counter in the message broker (e.g. Redis INCR).</summary>
    Task<long> IncrementCounterAsync(string key, CancellationToken ct = default);

    /// <summary>Reads a cluster-wide monotonic counter in the message broker (e.g. Redis GET).</summary>
    Task<long> GetCounterAsync(string key, CancellationToken ct = default);

    /// <summary>Fired when broker connectivity is restored after a partition, allowing local caches to resynchronize.</summary>
    event Action? ConnectionRestored;
}


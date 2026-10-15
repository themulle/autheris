namespace Autheris.Infrastructure.Caching;

using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;

/// <summary>
/// Provides asynchronous and non-blocking access to the Redis/Garnet <see cref="IConnectionMultiplexer"/>
/// so DI resolution never blocks synchronously on network I/O during startup (ARC-I-10).
/// </summary>
public interface IRedisConnectionProvider
{
    bool IsConfigured { get; }

    bool IsConnected { get; }

    IConnectionMultiplexer? TryGetMultiplexer();

    ValueTask<IConnectionMultiplexer?> GetMultiplexerAsync(CancellationToken ct = default);
}

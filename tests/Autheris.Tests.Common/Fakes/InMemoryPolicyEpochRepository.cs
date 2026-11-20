using System.Collections.Concurrent;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="IPolicyEpochRepository"/> tracking per-table policy epochs.
/// </summary>
public sealed class InMemoryPolicyEpochRepository(long initialEpoch = 0) : IPolicyEpochRepository
{
    private readonly ConcurrentDictionary<TableIdentifier, long> _epochs = new();
    private int _incrementCalls;

    public int IncrementCalls => Volatile.Read(ref _incrementCalls);

    public Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
        => Task.FromResult(_epochs.GetOrAdd(table, initialEpoch));

    public Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _incrementCalls);
        return Task.FromResult(_epochs.AddOrUpdate(table, initialEpoch + 1, static (_, current) => current + 1));
    }
}

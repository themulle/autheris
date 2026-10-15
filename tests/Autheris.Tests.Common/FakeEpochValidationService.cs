namespace Autheris.Tests.Common;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;

/// <summary>
/// Shared in-memory <see cref="IEpochValidationService"/> test double for unit and integration tests.
/// </summary>
public sealed class FakeEpochValidationService : IEpochValidationService
{
    private readonly ConcurrentDictionary<TableIdentifier, long> _epochs = new();

    public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        var current = _epochs.GetOrAdd(table, 1L);
        return Task.FromResult(current == cachedEpoch);
    }

    public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        _epochs.AddOrUpdate(table, 2L, static (_, current) => current + 1L);
        return Task.CompletedTask;
    }

    public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        return Task.FromResult(_epochs.GetOrAdd(table, 1L));
    }

    public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
    {
        var result = tables.Distinct().ToDictionary(t => t, t => _epochs.GetOrAdd(t, 1L));
        return Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(result);
    }

    public ValueTask<IReadOnlyDictionary<TableIdentifier, bool>> AreEpochsValidAsync(IReadOnlyList<EpochCheck> checks, CancellationToken ct = default)
    {
        var dict = new Dictionary<TableIdentifier, bool>(checks.Count);
        foreach (var check in checks)
        {
            var current = _epochs.GetOrAdd(check.Table, 1L);
            dict[check.Table] = current == check.CachedEpoch;
        }
        return ValueTask.FromResult<IReadOnlyDictionary<TableIdentifier, bool>>(dict);
    }
}

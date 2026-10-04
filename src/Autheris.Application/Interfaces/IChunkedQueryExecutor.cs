using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Autheris.Application.Interfaces;

public sealed record ParameterBudget(
    int KeyColumnCount = 1,
    int ContextParameterCount = 0,
    Autheris.Domain.Common.DatabaseDialect Dialect = Autheris.Domain.Common.DatabaseDialect.Sqlite,
    int SafetyBuffer = 50);

/// <summary>
/// Behandelt den Sonderfall, dass IN-Listen aufgrund vieler IDs zu lang werden
/// und Abfragen in mehrere parametrisierte Queries aufgeteilt werden müssen.
/// Berücksichtigt strikte RDBMS-Parameterbudgets (auch für Composite Keys).
/// </summary>
public interface IChunkedQueryExecutor
{
    int DefaultChunkSize { get; }

    int CalculateEffectiveChunkSize(
        int keyColumnCount,
        int contextParameterCount = 0,
        Autheris.Domain.Common.DatabaseDialect dialect = Autheris.Domain.Common.DatabaseDialect.Sqlite,
        int safetyBuffer = 50);

    Task<IReadOnlyDictionary<TKey, List<TValue>>> ExecuteGroupedChunkedQueryAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull;

    Task<(IReadOnlyDictionary<TKey, List<TValue>> Results, int QueriesDispatched)> ExecuteGroupedWithMetricsAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull;

    Task<(IReadOnlyDictionary<TKey, List<TValue>> Results, int QueriesDispatched)> ExecuteGroupedWithBudgetAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        ParameterBudget budget,
        CancellationToken ct = default)
        where TKey : notnull;

    Task<IReadOnlyDictionary<TKey, TValue>> ExecuteChunkedQueryAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull;

    Task<(IReadOnlyDictionary<TKey, TValue> Results, int QueriesDispatched)> ExecuteWithMetricsAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull;

    Task<(IReadOnlyDictionary<TKey, TValue> Results, int QueriesDispatched)> ExecuteWithBudgetAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        ParameterBudget budget,
        CancellationToken ct = default)
        where TKey : notnull;
}

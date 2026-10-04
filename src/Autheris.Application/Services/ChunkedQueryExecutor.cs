using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;

namespace Autheris.Application.Services;

/// <summary>
/// Implementiert die Aufteilung langer ID-Listen (IN-Klauseln) in handhabbare Chunks,
/// führt mehrere parametrisierte Queries aus und aggregiert die Teilergebnisse deterministisch.
/// </summary>
public sealed class ChunkedQueryExecutor : IChunkedQueryExecutor
{
    public int DefaultChunkSize { get; }
    public int MaxDegreeOfParallelism { get; }
    private readonly Autheris.Domain.Interfaces.IParameterBudgetProvider? _budgetProvider;

    public ChunkedQueryExecutor(
        int defaultChunkSize = 500,
        Autheris.Domain.Interfaces.IParameterBudgetProvider? budgetProvider = null,
        int maxDegreeOfParallelism = 8)
    {
        if (defaultChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultChunkSize), "Chunk-Größe muss größer als 0 sein.");
        }
        if (maxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism), "MaxDegreeOfParallelism muss größer als 0 sein.");
        }
        DefaultChunkSize = defaultChunkSize;
        _budgetProvider = budgetProvider;
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    public async Task<IReadOnlyDictionary<TKey, List<TValue>>> ExecuteGroupedChunkedQueryAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull
    {
        var (results, _) = await ExecuteGroupedWithMetricsAsync(keys, chunkQueryFunc, chunkSize, ct);
        return results;
    }

    public async Task<(IReadOnlyDictionary<TKey, List<TValue>> Results, int QueriesDispatched)> ExecuteGroupedWithMetricsAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(chunkQueryFunc);

        if (keys == null || keys.Count == 0)
        {
            return (new Dictionary<TKey, List<TValue>>(), 0);
        }

        var effectiveChunkSize = chunkSize.HasValue && chunkSize.Value > 0 ? chunkSize.Value : DefaultChunkSize;

        // Deduplizieren, um redundante Parameter in SQL IN-Listen zu verhindern
        var distinctKeys = keys.Distinct().ToList();
        var chunks = distinctKeys.Chunk(effectiveChunkSize).ToList();

        if (chunks.Count == 0)
        {
            return (new Dictionary<TKey, List<TValue>>(), 0);
        }

        // Fast-path for single chunk: avoid Parallel.ForEachAsync scheduling overhead
        if (chunks.Count == 1)
        {
            var singleResult = await chunkQueryFunc(chunks[0], ct);
            var singleMerged = new Dictionary<TKey, List<TValue>>(distinctKeys.Count);
            foreach (var key in distinctKeys)
            {
                singleMerged[key] = new List<TValue>();
            }

            if (singleResult != null)
            {
                foreach (var kvp in singleResult)
                {
                    if (!singleMerged.TryGetValue(kvp.Key, out var list))
                    {
                        list = new List<TValue>();
                        singleMerged[kvp.Key] = list;
                    }

                    if (kvp.Value != null)
                    {
                        list.AddRange(kvp.Value);
                    }
                }
            }

            return (singleMerged, 1);
        }

        // Bounded parallel execution of chunk queries
        var chunkResults = new IReadOnlyDictionary<TKey, List<TValue>>?[chunks.Count];
        await Parallel.ForEachAsync(
            chunks.Select((chunk, index) => (chunk, index)),
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                chunkResults[item.index] = await chunkQueryFunc(item.chunk, token);
            });

        var merged = new Dictionary<TKey, List<TValue>>(distinctKeys.Count);

        // Alle abgefragten Keys vorinitialisieren
        foreach (var key in distinctKeys)
        {
            merged[key] = new List<TValue>();
        }

        // Ergebnisse aus den Teilabfragen zusammenführen
        foreach (var dict in chunkResults)
        {
            if (dict == null) continue;

            foreach (var kvp in dict)
            {
                if (!merged.TryGetValue(kvp.Key, out var list))
                {
                    list = new List<TValue>();
                    merged[kvp.Key] = list;
                }

                if (kvp.Value != null)
                {
                    list.AddRange(kvp.Value);
                }
            }
        }

        return (merged, chunks.Count);
    }

    public async Task<IReadOnlyDictionary<TKey, TValue>> ExecuteChunkedQueryAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull
    {
        var (results, _) = await ExecuteWithMetricsAsync(keys, chunkQueryFunc, chunkSize, ct);
        return results;
    }

    public async Task<(IReadOnlyDictionary<TKey, TValue> Results, int QueriesDispatched)> ExecuteWithMetricsAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        int? chunkSize = null,
        CancellationToken ct = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(chunkQueryFunc);

        if (keys == null || keys.Count == 0)
        {
            return (new Dictionary<TKey, TValue>(), 0);
        }

        var effectiveChunkSize = chunkSize.HasValue && chunkSize.Value > 0 ? chunkSize.Value : DefaultChunkSize;

        var distinctKeys = keys.Distinct().ToList();
        var chunks = distinctKeys.Chunk(effectiveChunkSize).ToList();

        if (chunks.Count == 0)
        {
            return (new Dictionary<TKey, TValue>(), 0);
        }

        // Fast-path for single chunk: avoid Parallel.ForEachAsync scheduling overhead
        if (chunks.Count == 1)
        {
            var singleResult = await chunkQueryFunc(chunks[0], ct);
            var singleMerged = new Dictionary<TKey, TValue>(distinctKeys.Count);

            if (singleResult != null)
            {
                foreach (var kvp in singleResult)
                {
                    singleMerged[kvp.Key] = kvp.Value;
                }
            }

            return (singleMerged, 1);
        }

        // Bounded parallel execution of chunk queries
        var chunkResults = new IReadOnlyDictionary<TKey, TValue>?[chunks.Count];
        await Parallel.ForEachAsync(
            chunks.Select((chunk, index) => (chunk, index)),
            new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism, CancellationToken = ct },
            async (item, token) =>
            {
                chunkResults[item.index] = await chunkQueryFunc(item.chunk, token);
            });

        var merged = new Dictionary<TKey, TValue>(distinctKeys.Count);

        foreach (var dict in chunkResults)
        {
            if (dict == null) continue;

            foreach (var kvp in dict)
            {
                merged[kvp.Key] = kvp.Value;
            }
        }

        return (merged, chunks.Count);
    }

    public int CalculateEffectiveChunkSize(
        int keyColumnCount,
        int contextParameterCount = 0,
        Autheris.Domain.Common.DatabaseDialect dialect = Autheris.Domain.Common.DatabaseDialect.Sqlite,
        int safetyBuffer = 50)
    {
        if (_budgetProvider != null)
        {
            return _budgetProvider.CalculateEffectiveChunkSize(DefaultChunkSize, keyColumnCount, contextParameterCount, dialect, safetyBuffer);
        }

        int maxEngineParams = dialect switch
        {
            Autheris.Domain.Common.DatabaseDialect.Sqlite => 999,
            Autheris.Domain.Common.DatabaseDialect.Oracle => 1000,
            Autheris.Domain.Common.DatabaseDialect.SqlServer => 2100,
            Autheris.Domain.Common.DatabaseDialect.PostgreSql => 10000,
            Autheris.Domain.Common.DatabaseDialect.Databricks => 10000,
            _ => 999
        };

        int cols = Math.Max(1, keyColumnCount);
        int available = Math.Max(1, maxEngineParams - Math.Max(0, contextParameterCount) - Math.Max(0, safetyBuffer));
        int sizeFromBudget = available / cols;

        return Math.Max(1, Math.Min(DefaultChunkSize, sizeFromBudget));
    }

    public Task<(IReadOnlyDictionary<TKey, List<TValue>> Results, int QueriesDispatched)> ExecuteGroupedWithBudgetAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, List<TValue>>>> chunkQueryFunc,
        ParameterBudget budget,
        CancellationToken ct = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(budget);
        int chunkSize = CalculateEffectiveChunkSize(budget.KeyColumnCount, budget.ContextParameterCount, budget.Dialect, budget.SafetyBuffer);
        return ExecuteGroupedWithMetricsAsync(keys, chunkQueryFunc, chunkSize, ct);
    }

    public Task<(IReadOnlyDictionary<TKey, TValue> Results, int QueriesDispatched)> ExecuteWithBudgetAsync<TKey, TValue>(
        IReadOnlyCollection<TKey> keys,
        Func<IReadOnlyList<TKey>, CancellationToken, Task<IReadOnlyDictionary<TKey, TValue>>> chunkQueryFunc,
        ParameterBudget budget,
        CancellationToken ct = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(budget);
        int chunkSize = CalculateEffectiveChunkSize(budget.KeyColumnCount, budget.ContextParameterCount, budget.Dialect, budget.SafetyBuffer);
        return ExecuteWithMetricsAsync(keys, chunkQueryFunc, chunkSize, ct);
    }
}

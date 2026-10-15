namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// In-memory implementation of <see cref="ICrossSourceRowReader"/> for unit testing and federated staging (#75).
/// </summary>
public sealed class InMemoryCrossSourceRowReader : ICrossSourceRowReader
{
    private readonly ConcurrentDictionary<TableIdentifier, List<IReadOnlyDictionary<string, object?>>> _tables = new();

    public void SeedTable(TableIdentifier table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        _tables[table] = rows
            .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsByKeysAsync(
        TableMetadata targetTable,
        IReadOnlyList<string> joinKeyColumns,
        IReadOnlyList<CompositeKey> batchKeys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);
        ArgumentNullException.ThrowIfNull(joinKeyColumns);
        ArgumentNullException.ThrowIfNull(batchKeys);

        if (!_tables.TryGetValue(targetTable.Identifier, out var rows) || rows.Count == 0 || batchKeys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var keySet = new HashSet<CompositeKey>(batchKeys);
        var matched = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            if (CrossSourceRelationResolver.TryExtractKey(row, joinKeyColumns, out var key) && keySet.Contains(key))
            {
                matched.Add(row);
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadRowsForFilterAsync(
        TableMetadata targetTable,
        string filterSql,
        int maxRows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetTable);

        if (!_tables.TryGetValue(targetTable.Identifier, out var rows) || rows.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var matched = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(filterSql) || StreamingRowFilterAstEvaluator.Matches(row, filterSql))
            {
                matched.Add(row);
                if (maxRows > 0 && matched.Count >= maxRows)
                {
                    break;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }
}

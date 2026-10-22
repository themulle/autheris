namespace Autheris.Application.Data.Relations;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// In-memory implementation of <see cref="ICrossSourceRowReader"/> for unit testing and federated staging (#75).
/// </summary>
/// <remarks>Not used by the GraphQL path; replaced by WP-FED-06.</remarks>
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
        CrossSourceKeyReadRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_tables.TryGetValue(request.Target.Identifier, out var rows) || rows.Count == 0 || request.Keys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var keySet = new HashSet<CompositeKey>(request.Keys);
        var matched = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            if (CrossSourceRelationResolver.TryExtractKey(row, request.JoinKeyColumns, out var key) && keySet.Contains(key))
            {
                matched.Add(row);
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }
}

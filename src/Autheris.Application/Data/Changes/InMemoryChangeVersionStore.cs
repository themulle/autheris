namespace Autheris.Application.Data.Changes;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Relations;
using Autheris.Domain.Common;

/// <summary>
/// In-memory implementation of <see cref="IChangeVersionProvider"/> (#77) for unit and integration testing: it supports seeding versions,
/// minimum valid versions, change logs and current table rows. It is not a production registration (VF-13): without a real change log
/// the production default is <see cref="NoChangeLogVersionProvider"/>.
/// </summary>
public sealed class InMemoryChangeVersionStore : IChangeVersionProvider
{
    private readonly ConcurrentDictionary<TableIdentifier, long> _currentVersions = new();
    private readonly ConcurrentDictionary<TableIdentifier, long> _minValidVersions = new();
    private readonly ConcurrentDictionary<TableIdentifier, List<SourceChangeRecord>> _changes = new();
    private readonly ConcurrentDictionary<TableIdentifier, List<IReadOnlyDictionary<string, object?>>> _currentRows = new();

    public bool ProvidesPreImages => true;

    /// <summary>Counter tracking how many times <see cref="ReadCurrentRowsByPrimaryKeysAsync"/> was invoked (for verifying 0-row reads on 304 Not Modified).</summary>
    public int RowReadInvocationCount { get; private set; }

    public void SetTableVersion(TableIdentifier table, long currentVersion, long minValidVersion = 0)
    {
        _currentVersions[table] = currentVersion;
        _minValidVersions[table] = minValidVersion;
    }

    public void SetCurrentRows(TableIdentifier table, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        _currentRows[table] = rows
            .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public void RecordChange(TableIdentifier table, SourceChangeRecord change, long? newCurrentVersion = null)
    {
        var list = _changes.GetOrAdd(table, _ => new List<SourceChangeRecord>());
        lock (list)
        {
            list.Add(change);
        }

        var nextVer = newCurrentVersion ?? change.ChangeVersion;
        _currentVersions.AddOrUpdate(table, nextVer, (_, existing) => Math.Max(existing, nextVer));
        _minValidVersions.TryAdd(table, 0);
    }

    public ValueTask<long?> GetCurrentVersionAsync(TableIdentifier table, CancellationToken ct = default) =>
        ValueTask.FromResult<long?>(_currentVersions.TryGetValue(table, out var ver) ? ver : null);

    public ValueTask<long?> GetMinValidVersionAsync(TableIdentifier table, CancellationToken ct = default)
    {
        if (_minValidVersions.TryGetValue(table, out var minVer))
        {
            return ValueTask.FromResult<long?>(minVer);
        }

        return ValueTask.FromResult<long?>(0L);
    }

    public Task<IReadOnlyList<SourceChangeRecord>> GetChangesBetweenAsync(
        TableIdentifier table,
        long sinceVersion,
        long upToVersion,
        int maxRecords,
        CancellationToken ct = default)
    {
        if (!_changes.TryGetValue(table, out var list))
        {
            return Task.FromResult<IReadOnlyList<SourceChangeRecord>>(Array.Empty<SourceChangeRecord>());
        }

        List<SourceChangeRecord> matching;
        lock (list)
        {
            matching = list
                .Where(c => c.ChangeVersion > sinceVersion && c.ChangeVersion <= upToVersion)
                .OrderBy(c => c.ChangeVersion)
                .Take(Math.Max(0, maxRecords))
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<SourceChangeRecord>>(matching);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCurrentRowsByPrimaryKeysAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyList<CompositeKey> keys,
        CancellationToken ct = default)
    {
        RowReadInvocationCount++;

        if (!_currentRows.TryGetValue(table, out var rows) || rows.Count == 0 || keys.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(Array.Empty<IReadOnlyDictionary<string, object?>>());
        }

        var keySet = new HashSet<CompositeKey>(keys);
        var matched = new List<IReadOnlyDictionary<string, object?>>();

        foreach (var row in rows)
        {
            if (CrossSourceRelationResolver.TryExtractKey(row, primaryKeyColumns, out var pk) && keySet.Contains(pk))
            {
                matched.Add(row);
            }
        }

        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(matched);
    }
}

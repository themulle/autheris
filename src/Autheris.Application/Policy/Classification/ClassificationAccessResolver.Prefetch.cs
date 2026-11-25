namespace Autheris.Application.Policy.Classification;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>Issue #1043: set-based reads of the persisted column classifications and mask overrides.</summary>
public sealed partial class ClassificationAccessResolver
{
    /// <summary>
    /// Issue #1043: the persisted rows of every column of the table (by column id, as the per-column reads), from the batch prefetch when
    /// it covers the table for this tenant, otherwise in one repository read.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<T>>> LoadPerTableAsync<T>(
        TableMetadata metadata,
        string tenantId,
        ColumnPolicyRowPrefetch? prefetch,
        Func<ColumnPolicyRowPrefetch, string, IReadOnlyList<T>> fromPrefetch,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<T>>>> read,
        CancellationToken ct)
    {
        var ids = ColumnIdsOf(metadata);
        if (ids.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<T>>(StringComparer.OrdinalIgnoreCase);
        }

        if (prefetch is not null && prefetch.Covers(tenantId, ids, _timeProvider.GetUtcNow()))
        {
            var fromBatch = new Dictionary<string, IReadOnlyList<T>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                var rows = fromPrefetch(prefetch, id);
                if (rows.Count > 0)
                {
                    fromBatch[id] = rows;
                }
            }

            return fromBatch;
        }

        return await read(ids, ct).ConfigureAwait(false);
    }

    private static List<string> ColumnIdsOf(TableMetadata metadata)
    {
        var ids = new List<string>(metadata.Columns.Count);
        foreach (var col in metadata.Columns)
        {
            if (col.Id != Guid.Empty)
            {
                ids.Add(col.Id.ToString());
            }
        }

        return ids;
    }

    /// <summary>
    /// Issue #1043: reads the persisted column classifications and mask overrides of every column of <paramref name="tables"/> for
    /// <paramref name="tenantId"/> in set-based statements, for a batch of access decisions. Null without a repository.
    /// </summary>
    public async ValueTask<ColumnPolicyRowPrefetch?> PrefetchColumnRowsAsync(
        IReadOnlyList<TableMetadata> tables,
        string tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (_repository is null)
        {
            return null;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            foreach (var id in ColumnIdsOf(table))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return null;
        }

        var readAt = _timeProvider.GetUtcNow();
        var catalog = await LoadCatalogAsync(tenantId, ct).ConfigureAwait(false);
        var classifications = await _repository.GetColumnClassificationsBatchAsync(ids, tenantId, ct).ConfigureAwait(false);
        var overrides = await _repository.GetColumnMaskOverridesBatchAsync(ids, tenantId, ct).ConfigureAwait(false);
        return new ColumnPolicyRowPrefetch(tenantId, readAt, ids, classifications, overrides, catalog);
    }
}

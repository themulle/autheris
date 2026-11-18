namespace Autheris.Infrastructure.Persistence.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Resolves several tables for one request with a single batched epoch read that validates every cache hit at once.
/// A table without an epoch row, or an unreadable epoch, is a miss and reloads from the database; never a valid hit.
/// </summary>
internal static class CatalogMetadataBatchReader
{
    /// <summary>One entry per requested table in request order (null = unknown or inactive).</summary>
    internal static async Task<IReadOnlyList<TableMetadata?>> ReadAsync(
        GovernanceRepository repo, IReadOnlyList<TableIdentifier> tables, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var result = new TableMetadata?[tables.Count];
        if (tables.Count == 0)
        {
            return result;
        }

        var nowTicks = TimeProvider.System.GetUtcNow().UtcTicks;
        var hits = new Dictionary<int, (TableMetadata Metadata, long Epoch)>();
        for (var i = 0; i < tables.Count; i++)
        {
            if (repo.MetadataCache.TryGetValue(tables[i].ToString().ToLowerInvariant(), out var entry) &&
                entry.Metadata != null &&
                nowTicks - entry.CachedAtTicks < repo.MetadataCacheTtlTicks)
            {
                hits[i] = (entry.Metadata, entry.Epoch);
            }
        }

        Dictionary<Guid, long>? epochs = null;
        if (hits.Count > 0)
        {
            try
            {
                var ids = hits.Values.Select(h => h.Metadata.Table.Id).Distinct().ToList();
                await using var epochConn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
                epochs = await GovernanceRepository.ReadTableEpochsByIdsAsync(epochConn, ids, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                repo.LogCatalogNotice("The table epochs could not be read; the metadata cache is bypassed (fail-closed).");
            }
        }

        for (var i = 0; i < tables.Count; i++)
        {
            if (epochs != null &&
                hits.TryGetValue(i, out var hit) &&
                epochs.TryGetValue(hit.Metadata.Table.Id, out var current) &&
                current == hit.Epoch)
            {
                result[i] = hit.Metadata;
                continue;
            }

            result[i] = await repo.GetTableMetadataCoreAsync(tables[i], null, ct, probeCache: false).ConfigureAwait(false);
        }

        // Tier and tags are never cached: a fresh overlay is applied to every returned table on one connection.
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] is { } structural)
            {
                var overlay = await DatasetCatalogStore.ReadOverlayAsync(
                    conn, null, repo.KeyDialect, structural.Table.Id, repo.CatalogLogger, ct).ConfigureAwait(false);
                result[i] = DatasetCatalogStore.Apply(structural, overlay);
            }
        }

        return result;
    }
}

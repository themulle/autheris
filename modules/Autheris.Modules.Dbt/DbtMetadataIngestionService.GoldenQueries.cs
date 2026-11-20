namespace Autheris.Modules.Dbt;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Tenancy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed partial class DbtMetadataIngestionService
{
    private readonly IGoldenQueryRepository? _goldenQueryRepository;

    /// <summary>
    /// Replaces the dbt golden queries of every synced dataset (an empty or missing list removes them). Invalid entries are skipped;
    /// warnings and log lines carry the dataset id, the entry index and a reason code only, never an entry value.
    /// </summary>
    private async Task SyncGoldenQueriesAsync(
        IReadOnlyList<(TableMetadata Metadata, DbtGoldenQueryMeta Meta)> pending,
        List<string> warnings,
        CancellationToken ct)
    {
        if (_goldenQueryRepository is null || pending.Count == 0)
        {
            return;
        }

        var existingByTenant = new Dictionary<string, ILookup<string, GoldenQueryStoreRecord>>(StringComparer.Ordinal);
        foreach (var (metadata, meta) in pending)
        {
            if (meta.Invalid)
            {
                continue;
            }

            var dataset = metadata.Identifier.ToString();
            var tableId = metadata.Table.Id.ToString();
            var tenant = ResolveGoldenQueryTenant(metadata);
            if (tenant is not { } resolved)
            {
                warnings.Add($"Table '{dataset}': golden queries skipped (TENANT_UNRESOLVED).");
                _logger.LogWarning("GOLDEN_QUERIES_SKIPPED: dataset {DatasetId} reason {ReasonCode}", dataset, "TENANT_UNRESOLVED");
                continue;
            }

            try
            {
                var entries = BuildGoldenQueryEntries(metadata, tableId, meta, dataset, warnings);
                if (!existingByTenant.TryGetValue(resolved.Value, out var existing))
                {
                    var rows = await _goldenQueryRepository.GetForTenantAsync(resolved, ct).ConfigureAwait(false);
                    existing = rows.Where(r => r.Source == GoldenQuerySource.Dbt).ToLookup(r => r.TableId, StringComparer.Ordinal);
                    existingByTenant[resolved.Value] = existing;
                }

                if (IsUnchanged(existing[tableId], entries))
                {
                    continue;
                }

                await _goldenQueryRepository.ReplaceForTableAsync(resolved, tableId, GoldenQuerySource.Dbt, entries, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                warnings.Add($"Table '{dataset}': golden queries were not stored (GOLDEN_QUERIES_STORE_FAILED).");
                _logger.LogWarning("GOLDEN_QUERIES_STORE_FAILED: dataset {DatasetId} error {Error}", dataset, ex.GetType().Name);
            }
        }
    }

    private List<GoldenQueryStoreEntry> BuildGoldenQueryEntries(TableMetadata metadata, string tableId, DbtGoldenQueryMeta meta, string dataset, List<string> warnings)
    {
        var entries = new List<GoldenQueryStoreEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (meta.TotalEntries > DbtGoldenQueryValidator.MaxEntriesPerDataset)
        {
            warnings.Add($"Table '{dataset}': golden queries truncated (GOLDEN_QUERIES_TRUNCATED, limit {DbtGoldenQueryValidator.MaxEntriesPerDataset}).");
            _logger.LogWarning("GOLDEN_QUERIES_TRUNCATED: dataset {DatasetId} limit {Limit}", dataset, DbtGoldenQueryValidator.MaxEntriesPerDataset);
        }

        foreach (var raw in meta.Entries.Take(DbtGoldenQueryValidator.MaxEntriesPerDataset))
        {
            var (entry, reason) = DbtGoldenQueryValidator.Validate(raw, metadata.Identifier, tableId);
            if (entry is not null && !seen.Add(entry.SourceKey))
            {
                (entry, reason) = (null, "TITLE_DUPLICATE");
            }

            if (entry is null)
            {
                warnings.Add($"Table '{dataset}': golden query entry {raw.Index} skipped ({reason}).");
                _logger.LogWarning("GOLDEN_QUERY_ENTRY_SKIPPED: dataset {DatasetId} entry {EntryIndex} reason {ReasonCode}", dataset, raw.Index, reason);
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    private static bool IsUnchanged(IEnumerable<GoldenQueryStoreRecord> stored, IReadOnlyList<GoldenQueryStoreEntry> desired)
    {
        var current = stored.ToDictionary(r => r.SourceKey, r => r.ContentHash, StringComparer.Ordinal);
        return current.Count == desired.Count
            && desired.All(d => current.TryGetValue(d.SourceKey, out var hash) && string.Equals(hash, d.ComputeContentHash(), StringComparison.Ordinal));
    }

    private TenantId? ResolveGoldenQueryTenant(TableMetadata metadata) =>
        !string.IsNullOrWhiteSpace(metadata.OwnerTenantId) && TenantId.TryParse(metadata.OwnerTenantId, out var owner)
            ? owner
            : TenantResolver.EffectiveDefaultOf(_gatewayOptions?.Value.Tenancy);
}

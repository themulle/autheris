namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Catalog;

public partial class GovernanceRepository
{
    private const int MaxBatchChunkSize = 500;

    /// <summary>
    /// Validates and upserts a batch of table metadata entries using parameter-budget chunking, deterministic lock ordering,
    /// per-chunk transactions, persisted-state change classification, and transient conflict retries.
    /// </summary>
    public async Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return Array.Empty<CatalogTableUpsertResult>();
        }

        // Pre-flight validation fails before any DB connection is opened or any table row is touched.
        var validated = new TableMetadata[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var item = batch[i] ?? throw new ArgumentNullException(nameof(batch));
            CatalogSourceNameValidator.Validate(item);
            item.ValidateRequiredFilterColumns();
            SemanticVirtualTables.EnsureNamespaceRespected(item);
            validated[i] = item.ReplaceKeyDefinition ? TableKeyDefinitionValidator.Validate(item) : item;
        }
        return await UpsertValidatedBatchAsync(validated, ct).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertValidatedBatchAsync(TableMetadata[] validated, CancellationToken ct)
    {
        // Deduplicate by case-insensitive identifier (latest entry wins) while preserving input-to-slot mapping.
        var keyToSlot = new Dictionary<CatalogBatchUpsertHelper.NormalizedIdentifierKey, int>();
        var distinct = new List<TableMetadata>();
        var inputToSlot = new int[validated.Length];
        for (var i = 0; i < validated.Length; i++)
        {
            var key = CatalogBatchUpsertHelper.NormalizedIdentifierKey.From(validated[i].Identifier);
            if (keyToSlot.TryGetValue(key, out var slot))
            {
                distinct[slot] = validated[i];
                inputToSlot[i] = slot;
            }
            else
            {
                slot = distinct.Count;
                keyToSlot[key] = slot;
                distinct.Add(validated[i]);
                inputToSlot[i] = slot;
            }
        }

        // Deterministic sort by (Domain, Schema, TableName) avoids lock-order deadlocks across concurrent batches.
        var sortedSlots = Enumerable.Range(0, distinct.Count)
            .OrderBy(s => distinct[s].Identifier.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => distinct[s].Identifier.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => distinct[s].Identifier.TableName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dbDialect = IsPostgreSql ? DatabaseDialect.PostgreSql : DatabaseDialect.SqlServer;
        var chunkSize = Math.Clamp(
            _dialect.ParameterBudget.CalculateEffectiveChunkSize(MaxBatchChunkSize, 3, 0, dbDialect, 50),
            1,
            MaxBatchChunkSize);

        var writeScope = CatalogWriteScopeContext.TryGetActive(this);
        var distinctResults = new CatalogTableUpsertResult[distinct.Count];
        for (var offset = 0; offset < sortedSlots.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, sortedSlots.Length - offset);
            var chunkSlots = new int[count];
            var chunkItems = new TableMetadata[count];
            for (var i = 0; i < count; i++)
            {
                var slot = sortedSlots[offset + i];
                chunkSlots[i] = slot;
                chunkItems[i] = distinct[slot];
            }

            var outcomes = await CatalogBatchUpsertHelper.ExecuteChunkWithRetryAsync(
                this, chunkItems, writeScope, IsTransientChunkConflict, ct).ConfigureAwait(false);
            var hydrated = await CatalogBatchUpsertHelper.HydrateChunkResultsAsync(
                this, chunkItems, outcomes, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
            {
                distinctResults[chunkSlots[i]] = hydrated[i];
            }
        }

        var results = new CatalogTableUpsertResult[validated.Length];
        for (var i = 0; i < validated.Length; i++)
        {
            results[i] = distinctResults[inputToSlot[i]];
        }

        return results;
    }

    private bool IsTransientChunkConflict(Exception ex) =>
        ex is DbException dbEx && (_dialect.IsTransient(dbEx) || dbEx.IsTransient || dbEx.SqlState is "40001" or "40P01");
}

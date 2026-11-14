namespace Autheris.Application.State.Caching.TableCache.Engines;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Crypto;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// Consumes CDC stream events from ICdcStreamStore and applies incremental mutations to the table cache.
/// Automatically detects stream sequence gaps, fences the cache, and triggers shadow resync.
/// Conforms to 04.
/// </summary>
public sealed class CdcTableCacheConsumer
{
    private readonly ICdcStreamStore _streamStore;
    private readonly ITableCacheStore _cacheStore;
    private readonly ITableCacheCatalog _catalog;
    private readonly ITableCacheManager _manager;
    private readonly ILogger<CdcTableCacheConsumer>? _logger;
    private readonly ICdcStreamCrypto? _crypto;
    private readonly ITableCacheInvalidator? _invalidator;

    public CdcTableCacheConsumer(
        ICdcStreamStore streamStore,
        ITableCacheStore cacheStore,
        ITableCacheCatalog catalog,
        ITableCacheManager manager,
        ILogger<CdcTableCacheConsumer>? logger = null,
        ICdcStreamCrypto? crypto = null,
        ITableCacheInvalidator? invalidator = null)
    {
        _streamStore = streamStore ?? throw new ArgumentNullException(nameof(streamStore));
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger;
        _crypto = crypto;
        _invalidator = invalidator;
    }

    public async Task ProcessStreamBatchAsync(
        CdcStreamId streamId,
        TableIdentifier table,
        int maxBatchSize = 1000,
        CancellationToken ct = default)
    {
        if (!_catalog.TryGet(table, out var metadata) || metadata is null)
        {
            return;
        }

        long fromOffset = (metadata.LastWatermarkOffset ?? 0) + 1;

        try
        {
            var readResult = await _streamStore.ReadAsync(streamId, fromOffset, maxBatchSize, ct).ConfigureAwait(false);

            if (readResult.Events == null || readResult.Events.Count == 0)
            {
                return;
            }

            var mutations = new List<TableCacheMutation>();
            long latestOffset = metadata.LastWatermarkOffset ?? 0;

            foreach (var evt in readResult.Events)
            {
                // 08 Sequence Integrity & Gap Detection
                if (evt.Kind == CdcStreamEventKind.Gap)
                {
                    _logger?.LogWarning("SEC-BL04-08: Gap event detected for table '{Table}' at stream offset {Offset}. Fencing table.", table, evt.Offset);
                    await FenceTableAndTriggerResyncAsync(table, metadata, ct).ConfigureAwait(false);
                    return;
                }

                // Check monotonic offset progression
                if (evt.Offset > latestOffset + 1 && latestOffset > 0)
                {
                    _logger?.LogWarning("SEC-BL04-08: Sequence jump detected for table '{Table}'. Expected: {Expected}, Actual: {Actual}. Fencing table.", table, latestOffset + 1, evt.Offset);
                    throw new CdcGapDetectedException(table, latestOffset + 1, evt.Offset);
                }

                // Discard stale or duplicate events idempotently
                if (evt.Offset <= latestOffset && latestOffset > 0)
                {
                    continue;
                }

                if (evt.Kind == CdcStreamEventKind.Change && evt.Operation.HasValue)
                {
                    if (_crypto is null)
                    {
                        throw new InvalidOperationException("CDC stream crypto is unavailable to decode change payload.");
                    }

                    var plaintext = await _crypto.UnprotectEventAsync(evt, ct).ConfigureAwait(false);
                    CdcChangePayload payload;
                    try
                    {
                        payload = CdcPayloadCodec.DecodeChange(plaintext);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(plaintext);
                    }

                    var op = evt.Operation.Value;
                    var pkDict = payload.Key;
                    var rowDict = op == CdcOperation.Delete
                        ? payload.Before ?? new Dictionary<string, object?>()
                        : payload.After ?? payload.Before ?? new Dictionary<string, object?>();

                    if (pkDict is null || pkDict.Count == 0 ||
                        ((op == CdcOperation.Insert || op == CdcOperation.Snapshot || op == CdcOperation.Update) && rowDict.Count == 0))
                    {
                        throw new InvalidOperationException("CDC change event payload carried no decoded values.");
                    }

                    mutations.Add(new TableCacheMutation(
                        op,
                        pkDict,
                        rowDict,
                        evt.Offset,
                        evt.ProducedAt));
                }

                latestOffset = evt.Offset;
            }

            if (mutations.Count > 0)
            {
                await _cacheStore.ApplyMutationsAsync(table, mutations, ct).ConfigureAwait(false);
            }

            // Update watermark offset in catalog
            var updatedMeta = metadata with
            {
                LastWatermarkOffset = latestOffset,
                IsHealthy = true,
                LastError = null
            };
            _catalog.Upsert(updatedMeta);
        }
        catch (CdcGapDetectedException)
        {
            _logger?.LogWarning("CDC stream gap encountered for '{Table}' ({ErrorType}). Fencing table and triggering resync.", table, nameof(CdcGapDetectedException));
            await FenceTableAndTriggerResyncAsync(table, metadata, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(
                "Failed applying CDC mutation batch for table '{Table}' ({ErrorType}, count={EventCount}). Fencing table and triggering resync.",
                table,
                ex.GetType().Name,
                metadata.RowCount);
            if (_invalidator is not null)
            {
                await _invalidator.InvalidateAsync(table, "CDC mutation batch failed; fencing table.", ct).ConfigureAwait(false);
            }
            await FenceTableAndTriggerResyncAsync(table, metadata, ct).ConfigureAwait(false);
        }
    }

    public async Task FenceTableAndTriggerResyncAsync(
        TableIdentifier table,
        TableCacheEntryMetadata metadata,
        CancellationToken ct = default)
    {
        // Immediately fence the table by setting IsHealthy = false
        var fencedMeta = metadata with
        {
            IsHealthy = false,
            LastError = "CDC stream gap detected: table fenced pending shadow resync."
        };
        _catalog.Upsert(fencedMeta);

        // Trigger automatic full shadow reload to resynchronize baseline
        try
        {
            await _manager.ForceRefreshAsync(table, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed automatic shadow reload after CDC gap for '{Table}'", table);
        }
    }
}

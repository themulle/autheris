namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Application.State.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hochperformante In-Process Hybrid Search Engine für den Datensatz- und Tabellenkatalog.
/// Kombiniert Okapi BM25 mit SIMD-beschleunigter Vektorsuche über Reciprocal Rank Fusion (RRF).
/// </summary>
public sealed class CatalogSearchEngine : ICatalogSearchEngine, ICatalogSearchStatusRecorder
{
    private readonly ILocalEmbeddingGenerator _embeddingGenerator;
    private readonly IOptions<CatalogSearchOptions> _options;
    private readonly ILogger<CatalogSearchEngine> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CatalogSearchEmbeddingCache _documentEmbeddingCache;
    private readonly IClassificationRepository? _classificationRepository;
    private readonly object _statusLock = new();
    private readonly Lock _stateLock = new();

    /// <summary>Fallback when <see cref="CatalogSearchOptions.MaxQueryLength"/> is not a positive value.</summary>
    public const int DefaultMaxQueryLength = 256;

    /// <summary>Maximum number of cached query embeddings (LRU; a miss only recomputes the vector).</summary>
    public const int QueryEmbeddingCacheCapacity = 1024;

    /// <summary>Delta-to-base ratio threshold above which a delta update compacts segments into a clean base snapshot.</summary>
    public const double DeltaRebuildRatio = 0.10;

    /// <summary>Maximum accumulated delta/tombstone entries before compacting segments into a clean base snapshot.</summary>
    public const int MaxDeltaEntriesBeforeRebuild = 5_000;

    /// <summary>Maximum accumulated base-segment tombstones before compacting into a clean base snapshot.</summary>
    public const int MaxTombstonesBeforeRebuild = 5_000;

    // Bounded LRU: arbitrary distinct queries must not grow the process memory without limit.
    private readonly BoundedCache<string, ReadOnlyMemory<float>> _embeddingCache = new(QueryEmbeddingCacheCapacity, comparer: StringComparer.Ordinal);

    private readonly Dictionary<long, long> _activeMergeVersions = new();
    private readonly List<(long Version, PreparedDeltaBatch Batch)> _pendingReplayLog = [];
    private long _mutationVersion;
    private long _nextMergeOperationId;
    private bool _compactionInProgress;

    private CatalogSearchSnapshot? _currentSnapshot;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastAttemptUtc;
    private string? _lastErrorCode;
    private (long StructureVersion, long ContentVersion)? _lastIndexedVersion;

    /// <summary>Test hook invoked outside locks during segment compaction right before atomic replay and commit.</summary>
    internal Func<CancellationToken, Task>? BeforeCompactionCommitForTesting { get; set; }

    public bool IsIndexReady => Volatile.Read(ref _currentSnapshot) != null;
    public int IndexedTableCount => Volatile.Read(ref _currentSnapshot)?.MetadataMap.Count ?? 0;
    public DateTimeOffset? LastIndexedAt => Volatile.Read(ref _currentSnapshot)?.CreatedAt;
    public int CachedDocumentEmbeddingCount => _documentEmbeddingCache.Count;

    public (long StructureVersion, long ContentVersion)? LastIndexedVersion
    {
        get
        {
            lock (_statusLock)
            {
                return _lastIndexedVersion;
            }
        }
    }

    /// <summary>Number of query embeddings currently cached (never above <see cref="QueryEmbeddingCacheCapacity"/>).</summary>
    public int CachedQueryEmbeddingCount => _embeddingCache.Count;

    public CatalogSearchEngine(
        ILocalEmbeddingGenerator embeddingGenerator,
        IOptions<CatalogSearchOptions> options,
        ILogger<CatalogSearchEngine> logger,
        TimeProvider? timeProvider = null,
        CatalogSearchEmbeddingCache? documentEmbeddingCache = null,
        IClassificationRepository? classificationRepository = null)
    {
        _embeddingGenerator = embeddingGenerator ?? throw new ArgumentNullException(nameof(embeddingGenerator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _documentEmbeddingCache = documentEmbeddingCache ?? new CatalogSearchEmbeddingCache();
        _classificationRepository = classificationRepository;
    }

    public CatalogSearchIndexStatus GetStatus(DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? _timeProvider.GetUtcNow();
        lock (_statusLock)
        {
            var lastSuccess = _lastSuccessUtc ?? Volatile.Read(ref _currentSnapshot)?.CreatedAt;
            var indexAge = lastSuccess.HasValue ? now - lastSuccess.Value : (TimeSpan?)null;
            return new CatalogSearchIndexStatus(lastSuccess, _lastAttemptUtc, _lastErrorCode, indexAge);
        }
    }

    public void RecordRebuildOutcome(
        DateTimeOffset? lastSuccessUtc,
        DateTimeOffset lastAttemptUtc,
        string? lastErrorCode,
        (long StructureVersion, long ContentVersion)? indexedVersion)
    {
        lock (_statusLock)
        {
            if (lastSuccessUtc.HasValue)
            {
                _lastSuccessUtc = lastSuccessUtc;
            }

            _lastAttemptUtc = lastAttemptUtc;
            _lastErrorCode = lastErrorCode;
            if (indexedVersion.HasValue)
            {
                _lastIndexedVersion = indexedVersion;
            }
        }
    }

    internal CatalogSearchSnapshot? CurrentSnapshotForTesting => Volatile.Read(ref _currentSnapshot);

    public async Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _logger.LogInformation("Starte Index-Neuaufbau für {Count} Katalog-Tabellen...", tables.Count);

        var (opId, startVersion) = BeginMergeOperation();
        try
        {
            var activeTables = tables.Where(static t => t != null && t.Table.IsActive && !t.Table.DiscoveryHidden).ToList();
            var prepared = await PrepareDocumentsAsync(activeTables, ct).ConfigureAwait(false);
            var vectors = await ResolveEmbeddingsWithCacheAsync(prepared, existingVectorIndex: null, ct).ConfigureAwait(false);
            var baseSnapshot = BuildCleanSnapshot(prepared, vectors, _timeProvider.GetUtcNow());

            lock (_stateLock)
            {
                var finalSnapshot = ReplayPendingDeltasLocked(baseSnapshot, startVersion, _timeProvider.GetUtcNow());
                _mutationVersion++;
                Interlocked.Exchange(ref _currentSnapshot, finalSnapshot);
            }

            _logger.LogInformation("Katalog-Suchindex erfolgreich aktualisiert. {Count} Tabellen aktiv.", activeTables.Count);
        }
        finally
        {
            EndMergeOperation(opId);
        }
    }

    /// <summary>
    /// Atomically applies incremental table additions, updates, deactivations, visibility removals, and deletions
    /// by publishing an immutable snapshot with tombstoned BM25/vector entries. Triggers segment compaction when
    /// delta size exceeds configured thresholds.
    /// </summary>
    public async Task ApplyDeltaAsync(
        IReadOnlyList<TableMetadata> upsertedTables,
        IReadOnlyCollection<TableIdentifier> removedOrInactiveIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upsertedTables);
        ArgumentNullException.ThrowIfNull(removedOrInactiveIds);

        if (upsertedTables.Count == 0 && removedOrInactiveIds.Count == 0)
        {
            return;
        }

        ct.ThrowIfCancellationRequested();

        var explicitRemovals = new HashSet<TableIdentifier>(removedOrInactiveIds);
        var activeCandidates = new List<TableMetadata>(upsertedTables.Count);
        foreach (var table in upsertedTables)
        {
            ArgumentNullException.ThrowIfNull(table);
            if (!table.Table.IsActive || table.Table.DiscoveryHidden || explicitRemovals.Contains(table.Identifier))
            {
                explicitRemovals.Add(table.Identifier);
                continue;
            }

            activeCandidates.Add(table);
        }

        PreparedDocument[] preparedDocs;
        ReadOnlyMemory<float>[] resolvedVectors;
        try
        {
            preparedDocs = await PrepareDocumentsAsync(activeCandidates, ct).ConfigureAwait(false);
            resolvedVectors = await ResolveEmbeddingsWithCacheAsync(preparedDocs, existingVectorIndex: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            RecordRebuildOutcome(lastSuccessUtc: null, _timeProvider.GetUtcNow(), CatalogSearchErrorCodes.EmbeddingFailed, indexedVersion: null);
            ex.Data[CatalogSearchErrorCodes.ErrorCodeDataKey] = CatalogSearchErrorCodes.EmbeddingFailed;
            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }

        bool needsCompaction = false;
        lock (_stateLock)
        {
            var current = _currentSnapshot;
            var batch = BuildEffectiveBatch(current, preparedDocs, resolvedVectors, explicitRemovals);

            var now = _timeProvider.GetUtcNow();
            var nextSnapshot = current is null
                ? ApplyBatchToEmptySnapshot(batch, now)
                : ApplyBatchToSnapshot(current, batch, now);

            _mutationVersion++;
            var version = _mutationVersion;
            if (_activeMergeVersions.Count > 0)
            {
                _pendingReplayLog.Add((version, batch));
            }

            Interlocked.Exchange(ref _currentSnapshot, nextSnapshot);
            if (!_compactionInProgress && ShouldCompact(nextSnapshot))
            {
                _compactionInProgress = true;
                needsCompaction = true;
            }
        }

        if (needsCompaction)
        {
            await CompactSnapshotAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task CompactSnapshotAsync(CancellationToken ct)
    {
        CatalogSearchSnapshot? captured;
        long capturedVersion;
        long opId;

        lock (_stateLock)
        {
            captured = _currentSnapshot;
            if (captured is null)
            {
                _compactionInProgress = false;
                return;
            }

            capturedVersion = _mutationVersion;
            opId = ++_nextMergeOperationId;
            _activeMergeVersions[opId] = capturedVersion;
        }

        try
        {
            if (BeforeCompactionCommitForTesting != null)
            {
                await BeforeCompactionCommitForTesting(ct).ConfigureAwait(false);
            }

            var activeTables = captured.MetadataMap.Values.Where(static t => t.Table.IsActive && !t.Table.DiscoveryHidden).ToList();
            var prepared = await PrepareDocumentsAsync(activeTables, ct).ConfigureAwait(false);
            var vectors = await ResolveEmbeddingsWithCacheAsync(prepared, captured.VectorIndex, ct).ConfigureAwait(false);
            var compactedBase = BuildCleanSnapshot(prepared, vectors, _timeProvider.GetUtcNow());

            lock (_stateLock)
            {
                var finalSnapshot = ReplayPendingDeltasLocked(compactedBase, capturedVersion, _timeProvider.GetUtcNow());
                _mutationVersion++;
                Interlocked.Exchange(ref _currentSnapshot, finalSnapshot);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep existing base + delta + tombstones snapshot serving cleanly if compaction fails.
            RecordRebuildOutcome(lastSuccessUtc: null, _timeProvider.GetUtcNow(), CatalogSearchErrorCodes.RebuildFailed, indexedVersion: null);
            _logger.LogWarning(ex, "Catalog search segment compaction failed; continuing to serve existing base, delta, and tombstone snapshot.");
        }
        finally
        {
            lock (_stateLock)
            {
                _compactionInProgress = false;
            }
            EndMergeOperation(opId);
        }
    }

    private static bool ShouldCompact(CatalogSearchSnapshot snapshot)
    {
        if (snapshot.TombstoneCount >= MaxTombstonesBeforeRebuild ||
            snapshot.DeltaOperationCount > MaxDeltaEntriesBeforeRebuild)
        {
            return true;
        }

        return snapshot.BaseDocumentCount > 0 &&
               (double)snapshot.DeltaOperationCount / snapshot.BaseDocumentCount > DeltaRebuildRatio;
    }

    private (long OperationId, long StartVersion) BeginMergeOperation()
    {
        lock (_stateLock)
        {
            var opId = ++_nextMergeOperationId;
            var startVersion = _mutationVersion;
            _activeMergeVersions[opId] = startVersion;
            return (opId, startVersion);
        }
    }

    private void EndMergeOperation(long opId)
    {
        lock (_stateLock)
        {
            _activeMergeVersions.Remove(opId);
            if (_activeMergeVersions.Count == 0)
            {
                _pendingReplayLog.Clear();
            }
            else
            {
                long oldestActive = _activeMergeVersions.Values.Min();
                _pendingReplayLog.RemoveAll(entry => entry.Version <= oldestActive);
            }
        }
    }

    private CatalogSearchSnapshot ReplayPendingDeltasLocked(CatalogSearchSnapshot snapshot, long sinceVersion, DateTimeOffset now)
    {
        var current = snapshot;
        foreach (var (version, batch) in _pendingReplayLog)
        {
            if (version > sinceVersion)
            {
                current = ApplyBatchToSnapshot(current, batch, now);
            }
        }
        return current;
    }

    private async Task<PreparedDocument[]> PrepareDocumentsAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct)
    {
        var docs = new PreparedDocument[tables.Count];
        var hiddenResolver = new ClassificationAccessResolver.IndexingHiddenTextResolver(_classificationRepository);
        for (int i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var table = tables[i];
            var hiddenCols = await hiddenResolver.ResolveAsync(table, ct).ConfigureAwait(false);
            var (bm25Doc, text) = CatalogSearchDocumentBuilder.BuildDocuments(table, hiddenCols);
            var sha256 = CatalogSearchEmbeddingCache.ComputeContentSha256Hex(text);
            docs[i] = new PreparedDocument(table, bm25Doc, text, sha256, hiddenCols);
        }
        return docs;
    }

    private async Task<ReadOnlyMemory<float>[]> ResolveEmbeddingsWithCacheAsync(
        IReadOnlyList<PreparedDocument> docs,
        VectorSearchIndex? existingVectorIndex,
        CancellationToken ct)
    {
        var results = new ReadOnlyMemory<float>[docs.Count];
        if (docs.Count == 0 || !IsVectorSearchAvailable())
        {
            return results;
        }

        var (providerName, modelId, dimension) = ResolveModelIdentity();
        _documentEmbeddingCache.ClearIfModelChanged(providerName, modelId, dimension);

        var missesByHash = new Dictionary<string, int>(StringComparer.Ordinal);
        var missTexts = new List<string>();
        var missHashes = new List<string>();
        var docMissSlot = new int[docs.Count];
        Array.Fill(docMissSlot, -1);

        for (int i = 0; i < docs.Count; i++)
        {
            var doc = docs[i];
            if (_documentEmbeddingCache.TryGet(providerName, modelId, dimension, doc.ContentSha256Hex, out var cached) &&
                (dimension <= 0 || cached.Length == dimension))
            {
                results[i] = cached;
                continue;
            }

            if (existingVectorIndex != null &&
                existingVectorIndex.TryGetVector(doc.Table.Identifier, out var snapshotVec) &&
                !snapshotVec.IsEmpty &&
                (dimension <= 0 || snapshotVec.Length == dimension))
            {
                results[i] = snapshotVec;
                _documentEmbeddingCache.Set(providerName, modelId, dimension, doc.ContentSha256Hex, snapshotVec);
                continue;
            }

            if (!missesByHash.TryGetValue(doc.ContentSha256Hex, out int slot))
            {
                slot = missTexts.Count;
                missesByHash[doc.ContentSha256Hex] = slot;
                missTexts.Add(doc.ScrubbedText);
                missHashes.Add(doc.ContentSha256Hex);
            }

            docMissSlot[i] = slot;
        }

        if (missTexts.Count > 0)
        {
            IReadOnlyList<ReadOnlyMemory<float>> computed;
            try
            {
                computed = await _embeddingGenerator.GenerateNormalizedEmbeddingsAsync(missTexts, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                ex.Data[CatalogSearchErrorCodes.ErrorCodeDataKey] = CatalogSearchErrorCodes.EmbeddingFailed;
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            if (computed.Count != missTexts.Count)
            {
                var mismatch = new InvalidOperationException(
                    $"Embedding generator returned {computed.Count} vectors for {missTexts.Count} documents.");
                mismatch.Data[CatalogSearchErrorCodes.ErrorCodeDataKey] = CatalogSearchErrorCodes.EmbeddingFailed;
                throw mismatch;
            }

            for (int slot = 0; slot < computed.Count; slot++)
            {
                if (!computed[slot].IsEmpty)
                {
                    _documentEmbeddingCache.Set(providerName, modelId, dimension, missHashes[slot], computed[slot]);
                }
            }

            for (int i = 0; i < docs.Count; i++)
            {
                int slot = docMissSlot[i];
                if (slot >= 0)
                {
                    results[i] = computed[slot];
                }
            }
        }

        return results;
    }

    private (string ProviderName, string ModelId, int Dimension) ResolveModelIdentity()
    {
        var providerName = _options.Value.EmbeddingProvider.ToString();
        var modelId = !string.IsNullOrWhiteSpace(_options.Value.ModelSha256)
            ? $"{_embeddingGenerator.ModelIdentifier}#{_options.Value.ModelSha256}"
            : _embeddingGenerator.ModelIdentifier;
        var dimension = _embeddingGenerator.EmbeddingDimensions > 0
            ? _embeddingGenerator.EmbeddingDimensions
            : _options.Value.EmbeddingDimensions;
        return (providerName, modelId ?? string.Empty, dimension);
    }

    private static CatalogSearchSnapshot BuildCleanSnapshot(
        PreparedDocument[] prepared,
        ReadOnlyMemory<float>[] vectors,
        DateTimeOffset now)
    {
        var bm25Docs = new List<Bm25Document>(prepared.Length);
        var vectorDocs = new List<(TableIdentifier, ReadOnlyMemory<float>)>(prepared.Length);
        var metadataBuilder = ImmutableDictionary.CreateBuilder<TableIdentifier, TableMetadata>();
        var fkBuilder = ImmutableDictionary.CreateBuilder<TableIdentifier, List<TableRelationship>>();
        var hiddenBuilder = ImmutableDictionary.CreateBuilder<TableIdentifier, IReadOnlySet<string>>();

        for (int i = 0; i < prepared.Length; i++)
        {
            var doc = prepared[i];
            metadataBuilder[doc.Table.Identifier] = doc.Table;
            if (doc.HiddenColumns.Count > 0)
            {
                hiddenBuilder[doc.Table.Identifier] = doc.HiddenColumns;
            }
            bm25Docs.Add(doc.Bm25Doc);

            if (!vectors[i].IsEmpty)
            {
                vectorDocs.Add((doc.Table.Identifier, vectors[i]));
            }

            var fks = CatalogSearchDocumentBuilder.BuildForeignKeys(doc.Table);
            if (fks is { Count: > 0 })
            {
                fkBuilder[doc.Table.Identifier] = fks;
            }
        }

        return new CatalogSearchSnapshot(
            Bm25Index: new Bm25SearchIndex(bm25Docs),
            VectorIndex: new VectorSearchIndex(vectorDocs),
            MetadataMap: metadataBuilder.ToImmutable(),
            ForeignKeysGraph: fkBuilder.ToImmutable(),
            CreatedAt: now,
            HiddenTextColumns: hiddenBuilder.ToImmutable());
    }

    private static PreparedDeltaBatch BuildEffectiveBatch(
        CatalogSearchSnapshot? current,
        PreparedDocument[] candidates,
        ReadOnlyMemory<float>[] vectors,
        HashSet<TableIdentifier> explicitRemovals)
    {
        var removals = new HashSet<TableIdentifier>(explicitRemovals);
        var upserts = new List<PreparedUpsert>(candidates.Length);

        for (int i = 0; i < candidates.Length; i++)
        {
            var doc = candidates[i];
            // If an existing non-hidden table transitioned to Hidden, treat it as removed from searchable segments.
            if (doc.Table.Table.DiscoveryHidden ||
                (current != null &&
                 current.MetadataMap.TryGetValue(doc.Table.Identifier, out var existing) &&
                 existing.Table.Visibility != DatasetVisibility.Hidden &&
                 doc.Table.Table.Visibility == DatasetVisibility.Hidden))
            {
                removals.Add(doc.Table.Identifier);
                continue;
            }

            upserts.Add(new PreparedUpsert(doc, vectors[i]));
        }

        return new PreparedDeltaBatch(upserts, removals.ToArray());
    }

    private static CatalogSearchSnapshot ApplyBatchToEmptySnapshot(PreparedDeltaBatch batch, DateTimeOffset now)
    {
        var prepared = batch.Upserts.Select(static u => u.Doc).ToArray();
        var vectors = batch.Upserts.Select(static u => u.Vector).ToArray();
        return BuildCleanSnapshot(prepared, vectors, now);
    }

    private static CatalogSearchSnapshot ApplyBatchToSnapshot(
        CatalogSearchSnapshot current,
        PreparedDeltaBatch batch,
        DateTimeOffset now)
    {
        var bm25Upserts = batch.Upserts.Select(static u => u.Doc.Bm25Doc);
        var nextBm25 = current.Bm25Index.ApplyDelta(bm25Upserts, batch.Removals);

        var vecUpserts = batch.Upserts
            .Where(static u => !u.Vector.IsEmpty)
            .Select(static u => (u.Doc.Table.Identifier, u.Vector));
        var vecRemovals = batch.Removals.Concat(
            batch.Upserts.Where(static u => u.Vector.IsEmpty).Select(static u => u.Doc.Table.Identifier));
        var nextVector = current.VectorIndex.ApplyDelta(vecUpserts, vecRemovals);

        var metadataBuilder = ToImmutableBuilder(current.MetadataMap);
        var fkBuilder = ToImmutableBuilder(current.ForeignKeysGraph);
        var hiddenBuilder = ToImmutableBuilder(current.HiddenTextColumns ?? ImmutableDictionary<TableIdentifier, IReadOnlySet<string>>.Empty);

        foreach (var id in batch.Removals)
        {
            metadataBuilder.Remove(id);
            fkBuilder.Remove(id);
            hiddenBuilder.Remove(id);
        }

        foreach (var upsert in batch.Upserts)
        {
            var id = upsert.Doc.Table.Identifier;
            metadataBuilder[id] = upsert.Doc.Table;
            if (upsert.Doc.HiddenColumns.Count > 0)
            {
                hiddenBuilder[id] = upsert.Doc.HiddenColumns;
            }
            else
            {
                hiddenBuilder.Remove(id);
            }
            var fks = CatalogSearchDocumentBuilder.BuildForeignKeys(upsert.Doc.Table);
            if (fks is { Count: > 0 })
            {
                fkBuilder[id] = fks;
            }
            else
            {
                fkBuilder.Remove(id);
            }
        }

        return new CatalogSearchSnapshot(
            Bm25Index: nextBm25,
            VectorIndex: nextVector,
            MetadataMap: metadataBuilder.ToImmutable(),
            ForeignKeysGraph: fkBuilder.ToImmutable(),
            CreatedAt: now,
            HiddenTextColumns: hiddenBuilder.ToImmutable());
    }

    private static ImmutableDictionary<TableIdentifier, TValue>.Builder ToImmutableBuilder<TValue>(
        IReadOnlyDictionary<TableIdentifier, TValue> dict) =>
        dict is ImmutableDictionary<TableIdentifier, TValue> imm
            ? imm.ToBuilder()
            : dict.ToImmutableDictionary().ToBuilder();

    private sealed record PreparedDocument(TableMetadata Table, Bm25Document Bm25Doc, string ScrubbedText, string ContentSha256Hex, IReadOnlySet<string> HiddenColumns);
    private sealed record PreparedUpsert(PreparedDocument Doc, ReadOnlyMemory<float> Vector);
    private sealed record PreparedDeltaBatch(IReadOnlyList<PreparedUpsert> Upserts, IReadOnlyCollection<TableIdentifier> Removals);

    public IReadOnlyList<CatalogSearchHit> Search(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null) =>
        SearchCore(query, isVisiblePredicate, precomputedQueryVector: null, isColumnRestricted);

    /// <summary>
    /// Asynchronous variant of <see cref="Search"/>: the query embedding is computed without blocking a thread and honours
    /// <paramref name="ct"/>. Results are identical to <see cref="Search"/>.
    /// </summary>
    public async Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        Func<TableIdentifier, bool>? isColumnRestricted = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(isVisiblePredicate);
        EnsureQueryLength(query.QueryText);
        ct.ThrowIfCancellationRequested();

        var queryVector = ReadOnlyMemory<float>.Empty;
        if (query.Mode != CatalogSearchMode.KeywordBm25 && IsVectorSearchAvailable() && Volatile.Read(ref _currentSnapshot) != null)
        {
            queryVector = await GetOrComputeQueryEmbeddingAsync(query.QueryText, ct).ConfigureAwait(false);
        }

        return SearchCore(query, isVisiblePredicate, queryVector, isColumnRestricted);
    }

    /// <summary>
    /// Rejects over-long query text at the engine boundary, before it reaches BM25 tokenization or the embedding provider
    /// (a 400 for REST callers via <see cref="GatewayInvalidQueryException"/>; the message never echoes the query).
    /// </summary>
    private void EnsureQueryLength(string? queryText)
    {
        var maxLength = _options.Value.MaxQueryLength > 0 ? _options.Value.MaxQueryLength : DefaultMaxQueryLength;
        if (queryText != null && queryText.Length > maxLength)
        {
            throw new GatewayInvalidQueryException(
                $"The catalog search query exceeds the maximum length of {maxLength} characters.",
                "CATALOG_SEARCH_QUERY_TOO_LONG");
        }
    }

    private bool IsVectorSearchAvailable() =>
        _options.Value.EmbeddingProvider != CatalogEmbeddingProvider.None &&
        (_embeddingGenerator is not ICatalogEmbeddingGenerator ceg || ceg.IsAvailable);

    public string ResolveEffectiveMode(CatalogSearchMode requestedMode) =>
        (requestedMode, IsVectorSearchAvailable()) switch
        {
            (CatalogSearchMode.KeywordBm25, _) => "keyword",
            (_, false) => "keyword",
            (CatalogSearchMode.SemanticVector, true) => "semantic",
            (CatalogSearchMode.Hybrid, true) => "hybrid",
            _ => "keyword"
        };

    private IReadOnlyList<CatalogSearchHit> SearchCore(
        CatalogSearchQuery query,
        Func<TableIdentifier, bool> isVisiblePredicate,
        ReadOnlyMemory<float>? precomputedQueryVector,
        Func<TableIdentifier, bool>? isColumnRestricted = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(isVisiblePredicate);
        EnsureQueryLength(query.QueryText);

        // Sperrenfreier Lesezugriff via Volatile.Read
        var snapshot = Volatile.Read(ref _currentSnapshot);
        if (snapshot == null)
        {
            throw new CatalogSearchIndexNotReadyException();
        }

        bool isDomainMatch(TableIdentifier id) =>
            string.IsNullOrWhiteSpace(query.DomainFilter) ||
            string.Equals(id.Domain, query.DomainFilter.Trim(), StringComparison.OrdinalIgnoreCase);

        bool isAllowed(TableIdentifier id) =>
            isDomainMatch(id) && isVisiblePredicate(id);

        string effectiveMode = ResolveEffectiveMode(query.Mode);

        int rrfK = _options.Value.RrfConstantK;
        int topK = Math.Max(query.Limit * 3, 50);

        // 1. BM25 Ausführung mit Pre-Filtering
        var bm25Hits = (effectiveMode == "keyword" || effectiveMode == "hybrid")
            ? snapshot.Bm25Index.Search(query.QueryText, topK, isAllowed: isAllowed, isColumnRestricted: isColumnRestricted)
            : [];

        // 2. Vektor Ausführung mit Pre-Filtering
        ReadOnlyMemory<float> queryVec = ReadOnlyMemory<float>.Empty;
        if (effectiveMode == "semantic" || effectiveMode == "hybrid")
        {
            queryVec = precomputedQueryVector ?? GetOrComputeQueryEmbedding(query.QueryText);
        }

        var vectorHits = !queryVec.IsEmpty
            ? snapshot.VectorIndex.Search(queryVec, _options.Value.MinVectorSimilarity, topK, isAllowed: isAllowed)
            : [];

        // 3. Reciprocal Rank Fusion (RRF)
        var scoreMap = new Dictionary<TableIdentifier, (double Score, double? Bm25, double? Vec, List<string> Terms)>();

        for (int rank = 0; rank < bm25Hits.Count; rank++)
        {
            var hit = bm25Hits[rank];
            double rrf = 1.0 / (rrfK + rank + 1);

            if (!scoreMap.TryGetValue(hit.TableId, out var cur))
                cur = (0, hit.Score, null, new List<string>(hit.MatchedTerms));
            else
                cur = (cur.Score, hit.Score, cur.Vec, cur.Terms);

            scoreMap[hit.TableId] = (cur.Score + rrf, cur.Bm25, cur.Vec, cur.Terms);
        }

        for (int rank = 0; rank < vectorHits.Count; rank++)
        {
            var hit = vectorHits[rank];
            double rrf = 1.0 / (rrfK + rank + 1);

            if (!scoreMap.TryGetValue(hit.TableId, out var cur))
                cur = (0, null, hit.Score, []);
            else
                cur = (cur.Score, cur.Bm25, hit.Score, cur.Terms);

            scoreMap[hit.TableId] = (cur.Score + rrf, cur.Bm25, cur.Vec, cur.Terms);
        }

        // 4. Governance-Filterung (bereits vorab angewendet) & Join-Graph-Expansion
        var finalHits = scoreMap
            .OrderByDescending(kvp => kvp.Value.Score)
            .Take(query.Limit)
            .Select(kvp =>
            {
                snapshot.MetadataMap.TryGetValue(kvp.Key, out var meta);

                // Relationen auflösen (Bridge Tables / Joins)
                IReadOnlyList<TableRelationship> relations = [];
                if (query.ExpandRelations && snapshot.ForeignKeysGraph.TryGetValue(kvp.Key, out var relList))
                {
                    relations = relList.Where(r => isVisiblePredicate(r.ToTable))
                                       .Take(query.MaxRelationsPerHit)
                                       .ToList();
                }

                IReadOnlySet<string>? hiddenTextCols = null;
                snapshot.HiddenTextColumns?.TryGetValue(kvp.Key, out hiddenTextCols);
                var matchedCols = meta?.Columns
                    .Where(c => !c.DiscoveryHidden && kvp.Value.Terms.Any(term =>
                        c.ColumnName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        (!(hiddenTextCols?.Contains(c.ColumnName) ?? false) &&
                         ((!string.IsNullOrEmpty(c.DisplayName) && c.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                          (!string.IsNullOrEmpty(c.Description) && c.Description.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                          c.Texts.Entries.Values.Any(entry =>
                              (!string.IsNullOrEmpty(entry.DisplayName) && entry.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                              (!string.IsNullOrEmpty(entry.Description) && entry.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))))))
                    .Select(c => c.ColumnName)
                    .Take(5)
                    .ToList() ?? [];

                var defaultDisplayName = !string.IsNullOrWhiteSpace(meta?.Table.DisplayName)
                    ? meta.Table.DisplayName
                    : $"{kvp.Key.Domain}.{kvp.Key.TableName}";

                string resolvedDisplayName = defaultDisplayName;
                string? resolvedDescription = meta?.Table.Description;

                if (!string.IsNullOrWhiteSpace(query.Language) && meta?.Table != null)
                {
                    var resolved = meta.Table.Texts.Resolve(
                        [query.Language],
                        defaultDisplayName,
                        meta.Table.Description,
                        meta.Table.LongDescription);
                    resolvedDisplayName = resolved.DisplayName;
                    resolvedDescription = resolved.Description;
                }

                return new CatalogSearchHit(
                    TableIdentifier: kvp.Key,
                    DisplayName: resolvedDisplayName,
                    Description: resolvedDescription,
                    Domain: kvp.Key.Domain,
                    Sensitivity: meta?.Table.Sensitivity ?? "NORMAL",
                    CombinedScore: kvp.Value.Score,
                    Bm25Score: kvp.Value.Bm25,
                    VectorScore: kvp.Value.Vec,
                    MatchedTerms: kvp.Value.Terms,
                    RelevantColumns: matchedCols,
                    RelatedJoinPaths: relations,
                    // The root field is known only to the GraphQL catalog schema; CatalogDiscoveryService sets it (#682).
                    SuggestedGraphQlField: null,
                    EffectiveMode: effectiveMode,
                    Language: query.Language
                );
            })
            .ToList();

        return finalHits;
    }

    private ReadOnlyMemory<float> GetOrComputeQueryEmbedding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var hashKey = QueryEmbeddingCacheKey(text);
        if (_embeddingCache.TryGet(hashKey, out var cached))
        {
            return cached;
        }

        var vector = _embeddingGenerator.GenerateNormalizedEmbedding(text);
        CacheQueryEmbedding(hashKey, vector);
        return vector;
    }

    private async Task<ReadOnlyMemory<float>> GetOrComputeQueryEmbeddingAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ReadOnlyMemory<float>.Empty;
        }

        var hashKey = QueryEmbeddingCacheKey(text);
        if (_embeddingCache.TryGet(hashKey, out var cached))
        {
            return cached;
        }

        var vectors = await _embeddingGenerator.GenerateNormalizedEmbeddingsAsync([text], ct).ConfigureAwait(false);
        var vector = vectors.Count > 0 ? vectors[0] : ReadOnlyMemory<float>.Empty;
        CacheQueryEmbedding(hashKey, vector);
        return vector;
    }

    private void CacheQueryEmbedding(string hashKey, ReadOnlyMemory<float> vector)
    {
        // An empty vector means the provider failed or was unavailable: do not pin that degradation in the cache.
        if (!vector.IsEmpty)
        {
            _embeddingCache.Set(hashKey, vector);
        }
    }

    private static string QueryEmbeddingCacheKey(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

namespace Autheris.Tests.Unit.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogSearchIncrementalIndexSecurityTests
{
    private static readonly float[] BaseVec = [1f, 0f, 0f, 0f];
    private static readonly float[] AltVec = [0f, 1f, 0f, 0f];
    private static readonly float[] ThirdVec = [0f, 0f, 1f, 0f];

    [Fact]
    public async Task EmbeddingCache_UnchangedDocument_NotReEmbedded_ChangedDescription_ReEmbedded()
    {
        var embedder = new TrackingEmbeddingGenerator();
        var engine = CreateEngine(embedder);

        var t1 = CreateTable("sales", "orders", "Customer orders and shipments", visibility: DatasetVisibility.Full);
        var t2 = CreateTable("crm", "customers", "Customer master profiles", visibility: DatasetVisibility.Full);

        await engine.RebuildIndexAsync([t1, t2]);
        embedder.EmbeddedTextCount.ShouldBe(2);

        // Full rebuild over identical tables reuses cached document vectors.
        embedder.ResetCounters();
        await engine.RebuildIndexAsync([t1, t2]);
        embedder.EmbeddedTextCount.ShouldBe(0);
        embedder.AsyncCallCount.ShouldBe(0);

        // Delta with one unchanged table and one modified description embeds only the changed table.
        var t2Updated = t2 with { Table = t2.Table with { Description = "Updated CRM account master profiles" } };
        await engine.ApplyDeltaAsync([t1, t2Updated], []);
        embedder.EmbeddedTextCount.ShouldBe(1);
        embedder.EmbeddedTexts.Single().ShouldContain("Updated CRM account master profiles");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EmbeddingCache_ModelOrProviderOrDimensionChange_NotReused()
    {
        var sharedCache = new CatalogSearchEmbeddingCache();
        var table = CreateTable("sales", "orders", "Orders ledger", visibility: DatasetVisibility.Full);

        var genA = new TrackingEmbeddingGenerator(modelId: "model-v1", dimensions: 4);
        var engineA = CreateEngine(genA, CatalogEmbeddingProvider.LocalOnnx, sharedCache);
        await engineA.RebuildIndexAsync([table]);
        genA.EmbeddedTextCount.ShouldBe(1);
        sharedCache.Count.ShouldBe(1);

        // Same model/provider/dim hits the shared cache.
        var genA2 = new TrackingEmbeddingGenerator(modelId: "model-v1", dimensions: 4);
        var engineA2 = CreateEngine(genA2, CatalogEmbeddingProvider.LocalOnnx, sharedCache);
        await engineA2.RebuildIndexAsync([table]);
        genA2.EmbeddedTextCount.ShouldBe(0);

        // Model ID change must miss and clear previous model entries.
        var genB = new TrackingEmbeddingGenerator(modelId: "model-v2", dimensions: 4);
        var engineB = CreateEngine(genB, CatalogEmbeddingProvider.LocalOnnx, sharedCache);
        await engineB.RebuildIndexAsync([table]);
        genB.EmbeddedTextCount.ShouldBe(1);
        sharedCache.Count.ShouldBe(1);

        // Provider change must miss and not reuse.
        var genProvider = new TrackingEmbeddingGenerator(modelId: "model-v2", dimensions: 4);
        var engineProvider = CreateEngine(genProvider, CatalogEmbeddingProvider.Ollama, sharedCache);
        await engineProvider.RebuildIndexAsync([table]);
        genProvider.EmbeddedTextCount.ShouldBe(1);

        // Dimension change must miss and not reuse.
        var genDim = new TrackingEmbeddingGenerator(modelId: "model-v2", dimensions: 8);
        var engineDim = CreateEngine(genDim, CatalogEmbeddingProvider.Ollama, sharedCache);
        await engineDim.RebuildIndexAsync([table]);
        genDim.EmbeddedTextCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EmbeddingCache_KeyUsesScrubbedText_NeverStoresRawText()
    {
        var cache = new CatalogSearchEmbeddingCache();
        var embedder = new TrackingEmbeddingGenerator();
        var engine = CreateEngine(embedder, CatalogEmbeddingProvider.LocalOnnx, cache);

        const string rawSecret1 = "sk-live-SUPERSECRETVALUE111";
        const string rawSecret2 = "sk-live-DIFFERENTSECRET222";

        var t1 = CreateTable("sec", "audit_one", $"Token api_key={rawSecret1} endpoint logs", visibility: DatasetVisibility.Full);
        var t2 = CreateTable("sec", "audit_one", $"Token api_key={rawSecret2} endpoint logs", visibility: DatasetVisibility.Full);

        await engine.RebuildIndexAsync([t1]);
        embedder.EmbeddedTextCount.ShouldBe(1);
        embedder.EmbeddedTexts.Single().ShouldNotContain(rawSecret1);

        // Different raw secret redacts to identical scrubbed text -> exact same cache key, zero re-embedding.
        embedder.ResetCounters();
        await engine.RebuildIndexAsync([t2]);
        embedder.EmbeddedTextCount.ShouldBe(0);
        cache.Count.ShouldBe(1);

        // Verify cache internal fields never store document source text or secret material.
        var dictField = typeof(CatalogSearchEmbeddingCache).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance);
        dictField.ShouldNotBeNull();
        var map = (System.Collections.IEnumerable)dictField.GetValue(cache)!;
        foreach (var kvpObj in map)
        {
            var key = kvpObj.GetType().GetProperty("Key")!.GetValue(kvpObj)!.ToString()!;
            key.ShouldNotContain(rawSecret1);
            key.ShouldNotContain(rawSecret2);
            key.ShouldNotContain("endpoint logs");
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void EmbeddingCache_BoundedLru_EnforcesMaxEntriesAndMaxBytes()
    {
        var cache = new CatalogSearchEmbeddingCache(maxEntries: 3, maxBytes: 4 * sizeof(float) * 2);
        var vec = new float[4] { 1f, 0f, 0f, 0f };

        cache.Set("LocalOnnx", "m1", 4, "HASH1", vec);
        cache.Set("LocalOnnx", "m1", 4, "HASH2", vec);
        cache.Count.ShouldBe(2);
        cache.CurrentBytes.ShouldBe(32);

        // Adding a third exceeds maxBytes (2 vectors = 32 bytes), evicting oldest LRU.
        cache.Set("LocalOnnx", "m1", 4, "HASH3", vec);
        cache.Count.ShouldBe(2);
        cache.CurrentBytes.ShouldBe(32);
        cache.TryGet("LocalOnnx", "m1", 4, "HASH1", out _).ShouldBeFalse();
        cache.TryGet("LocalOnnx", "m1", 4, "HASH3", out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ApplyDelta_TombstoneAndNewDocument_PublishedAtomically_OldColumnTextNeverReturned()
    {
        var embedder = new TrackingEmbeddingGenerator(vectorSelector: text =>
            text.Contains("legacy_ssn_token", StringComparison.OrdinalIgnoreCase)
                ? BaseVec
                : text.Contains("departments", StringComparison.OrdinalIgnoreCase)
                    ? ThirdVec
                    : AltVec);
        var engine = CreateEngine(embedder);

        var initialOrders = CreateTable(
            "sales",
            "orders",
            "Primary customer orders table",
            columns: ["order_id", "legacy_ssn_token"],
            visibility: DatasetVisibility.Full);
        var otherTable = CreateTable("hr", "departments", "Department hierarchy", visibility: DatasetVisibility.Full);

        await engine.RebuildIndexAsync([initialOrders, otherTable]);

        // Initial version matches legacy column token.
        engine.Search(new CatalogSearchQuery("legacy_ssn_token", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .Select(h => h.TableIdentifier).ShouldContain(initialOrders.Identifier);
        engine.Search(new CatalogSearchQuery("legacy_ssn_token", Mode: CatalogSearchMode.SemanticVector), _ => true)
            .Select(h => h.TableIdentifier).ShouldContain(initialOrders.Identifier);

        // Update orders removing legacy_ssn_token and adding invoice_reference.
        var updatedOrders = CreateTable(
            "sales",
            "orders",
            "Primary customer orders table",
            columns: ["order_id", "invoice_reference"],
            visibility: DatasetVisibility.Full);

        await engine.ApplyDeltaAsync([updatedOrders], []);

        // Old column text must never be returned in either BM25 or Vector modes; updated doc appears once.
        engine.Search(new CatalogSearchQuery("legacy_ssn_token", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .ShouldBeEmpty();
        engine.Search(new CatalogSearchQuery("legacy_ssn_token", Mode: CatalogSearchMode.Hybrid), _ => true)
            .ShouldBeEmpty();
        var newHits = engine.Search(new CatalogSearchQuery("invoice_reference", Mode: CatalogSearchMode.Hybrid), _ => true);
        newHits.Count.ShouldBe(1);
        newHits[0].TableIdentifier.ShouldBe(initialOrders.Identifier);

        var snap = engine.CurrentSnapshotForTesting!;
        snap.VectorIndex.Search(BaseVec, minScore: 0.9f, topK: 10).ShouldBeEmpty();
        snap.VectorIndex.Search(AltVec, minScore: 0.9f, topK: 10).Select(x => x.TableId).ShouldContain(initialOrders.Identifier);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ApplyDelta_DeactivatedOrHiddenTable_TombstonedInBothBm25AndVector()
    {
        var embedder = new TrackingEmbeddingGenerator();
        var engine = CreateEngine(embedder);

        var activeKeep = CreateTable("sales", "orders", "Public order catalog", visibility: DatasetVisibility.Full);
        var toDeactivate = CreateTable("hr", "payroll", "Sensitive payroll disbursements", visibility: DatasetVisibility.Full);
        var toHide = CreateTable("fin", "ledgers", "General financial ledgers", visibility: DatasetVisibility.Full);
        var toDelete = CreateTable("ops", "incidents", "Incident tracking logs", visibility: DatasetVisibility.Full);

        await engine.RebuildIndexAsync([activeKeep, toDeactivate, toHide, toDelete]);
        engine.IndexedTableCount.ShouldBe(4);

        var deactivated = toDeactivate with { Table = toDeactivate.Table with { IsActive = false } };
        var hidden = toHide with { Table = toHide.Table with { Visibility = DatasetVisibility.Hidden } };

        await engine.ApplyDeltaAsync([deactivated, hidden], [toDelete.Identifier]);

        engine.IndexedTableCount.ShouldBe(1);
        var snap = engine.CurrentSnapshotForTesting!;
        snap.Bm25Index.Search("payroll", topK: 10).ShouldBeEmpty();
        snap.Bm25Index.Search("ledgers", topK: 10).ShouldBeEmpty();
        snap.Bm25Index.Search("incidents", topK: 10).ShouldBeEmpty();

        var vecHits = snap.VectorIndex.Search(BaseVec, minScore: 0.5f, topK: 10);
        vecHits.Select(x => x.TableId).ShouldBe([activeKeep.Identifier]);
    }

    [Fact]
    public void Bm25DeltaSegment_AddReplaceRemoveSequence_RankingMatchesFullRebuild()
    {
        var rng = new Random(20261114);
        var vocab = new[]
        {
            "customer", "invoice", "payment", "ledger", "shipment",
            "warehouse", "supplier", "contract", "revenue", "budget",
            "employee", "payroll", "tax", "refund", "product"
        };

        var corpus = new Dictionary<TableIdentifier, string>();
        for (int i = 0; i < 40; i++)
        {
            var id = new TableIdentifier("dom" + (i % 4), "dbo", $"tbl_{i}");
            corpus[id] = RandomDoc(rng, vocab, 8, 22);
        }

        var incremental = new Bm25SearchIndex(corpus.Select(kvp => (kvp.Key, kvp.Value)));

        // Execute 120 random delta steps mixing add, replace, and remove while verifying score & rank parity.
        for (int step = 0; step < 120; step++)
        {
            var upserts = new Dictionary<TableIdentifier, string>();
            var removals = new HashSet<TableIdentifier>();
            var touchedInBatch = new HashSet<TableIdentifier>();

            int opsInBatch = 1 + rng.Next(4);
            for (int op = 0; op < opsInBatch; op++)
            {
                int action = rng.Next(3);
                if (action == 0 || corpus.Count < 10)
                {
                    int idx = rng.Next(60);
                    var id = new TableIdentifier("dom" + (idx % 4), "dbo", $"tbl_{idx}");
                    if (!touchedInBatch.Add(id)) continue;
                    var text = RandomDoc(rng, vocab, 6, 20);
                    corpus[id] = text;
                    upserts[id] = text;
                }
                else if (action == 1)
                {
                    var id = corpus.Keys.ElementAt(rng.Next(corpus.Count));
                    if (!touchedInBatch.Add(id)) continue;
                    var text = RandomDoc(rng, vocab, 6, 20);
                    corpus[id] = text;
                    upserts[id] = text;
                }
                else
                {
                    var id = corpus.Keys.ElementAt(rng.Next(corpus.Count));
                    if (!touchedInBatch.Add(id)) continue;
                    corpus.Remove(id);
                    removals.Add(id);
                }
            }

            incremental = incremental.ApplyDelta(upserts.Select(kvp => (kvp.Key, kvp.Value)), removals);
            var rebuilt = new Bm25SearchIndex(corpus.Select(kvp => (kvp.Key, kvp.Value)));

            incremental.ActiveDocumentCount.ShouldBe(rebuilt.ActiveDocumentCount);

            var query = $"{vocab[rng.Next(vocab.Length)]} {vocab[rng.Next(vocab.Length)]}";
            var incHits = incremental.Search(query, topK: corpus.Count + 5);
            var rebHits = rebuilt.Search(query, topK: corpus.Count + 5);

            incHits.Count.ShouldBe(rebHits.Count);
            var incById = incHits.ToDictionary(h => h.TableId, h => h.Score);
            var rebById = rebHits.ToDictionary(h => h.TableId, h => h.Score);

            incById.Keys.ShouldBe(rebById.Keys, ignoreOrder: true);
            foreach (var id in incById.Keys)
            {
                incById[id].ShouldBe(rebById[id], 1e-10);
            }

            for (int r = 0; r < incHits.Count - 1; r++)
            {
                incHits[r].Score.ShouldBeGreaterThanOrEqualTo(incHits[r + 1].Score);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ApplyDelta_ConcurrentUpdateDuringMerge_NotLost_And_FailedDeltaKeepsServingPreviousSnapshot()
    {
        var embedder = new TrackingEmbeddingGenerator();
        var engine = CreateEngine(embedder);

        // Seed 20 tables so adding/updating 4 tables (>10% = 2) triggers segment compaction.
        var markerWords = new[]
        {
            "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
            "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tangoword"
        };
        var initialTables = Enumerable.Range(0, 20)
            .Select(i => CreateTable("sales", $"base_table_{i}", $"Base table documentation {markerWords[i]}", visibility: DatasetVisibility.Full))
            .ToList();
        await engine.RebuildIndexAsync(initialTables);

        var mergeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowMergeCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeCompactionCommitForTesting = async _ =>
        {
            mergeEntered.TrySetResult();
            await allowMergeCommit.Task;
        };

        var mergeBatch = Enumerable.Range(0, 4)
            .Select(i => CreateTable("sales", $"base_table_{i}", $"Updated via merge batch {markerWords[i]}", visibility: DatasetVisibility.Full))
            .ToList();
        var mergeTask = engine.ApplyDeltaAsync(mergeBatch, []);
        await mergeEntered.Task;

        // While compaction is in flight, execute a concurrent upsert and a concurrent deletion.
        var concurrentTable = CreateTable("finance", "concurrent_ledger", "Arrived concurrently during compaction", visibility: DatasetVisibility.Full);
        await engine.ApplyDeltaAsync([concurrentTable], [initialTables[19].Identifier]);

        allowMergeCommit.TrySetResult();
        await mergeTask;

        // Clear seam before subsequent assertions.
        engine.BeforeCompactionCommitForTesting = null;

        // Both the concurrent addition and concurrent removal survived compaction commit.
        engine.Search(new CatalogSearchQuery("Arrived concurrently during compaction", Mode: CatalogSearchMode.Hybrid), _ => true)
            .Select(h => h.TableIdentifier).ShouldContain(concurrentTable.Identifier);
        engine.Search(new CatalogSearchQuery("tangoword", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .ShouldBeEmpty();
        engine.Search(new CatalogSearchQuery("Base table documentation", Mode: CatalogSearchMode.Hybrid, Limit: 50), _ => true)
            .Select(h => h.TableIdentifier).ShouldNotContain(initialTables[19].Identifier);

        // Failed delta keeps the previous serving index intact and sets error status.
        embedder.ThrowOnNextCall = true;
        var failedTable = CreateTable("hr", "failed_table", "Must never drop previous serving snapshot", visibility: DatasetVisibility.Full);
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => engine.ApplyDeltaAsync([failedTable], []));
        ex.Data[CatalogSearchErrorCodes.ErrorCodeDataKey].ShouldBe(CatalogSearchErrorCodes.EmbeddingFailed);

        engine.IsIndexReady.ShouldBeTrue();
        engine.GetStatus().LastErrorCode.ShouldBe(CatalogSearchErrorCodes.EmbeddingFailed);
        engine.Search(new CatalogSearchQuery("Arrived concurrently during compaction", Mode: CatalogSearchMode.Hybrid), _ => true)
            .Select(h => h.TableIdentifier).ShouldContain(concurrentTable.Identifier);
        engine.Search(new CatalogSearchQuery("tangoword", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .ShouldBeEmpty();

        // Failed compaction keeps base + delta + tombstones serving intact and hides removed table.
        engine.BeforeCompactionCommitForTesting = _ => throw new InvalidOperationException("Simulated compaction failure.");
        var deltaWithTombstone = Enumerable.Range(5, 4)
            .Select(i => CreateTable("sales", $"base_table_{i}", $"Updated during failing merge {markerWords[i]}", visibility: DatasetVisibility.Full))
            .ToList();
        await engine.ApplyDeltaAsync(deltaWithTombstone, [initialTables[18].Identifier]);

        engine.BeforeCompactionCommitForTesting = null;
        engine.GetStatus().LastErrorCode.ShouldBe(CatalogSearchErrorCodes.RebuildFailed);
        engine.Search(new CatalogSearchQuery("sierra", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .ShouldBeEmpty();
        engine.Search(new CatalogSearchQuery("Base table documentation", Mode: CatalogSearchMode.Hybrid, Limit: 50), _ => true)
            .Select(h => h.TableIdentifier).ShouldNotContain(initialTables[18].Identifier);
        engine.Search(new CatalogSearchQuery("Updated during failing merge", Mode: CatalogSearchMode.Hybrid), _ => true)
            .Select(h => h.TableIdentifier).ShouldContain(initialTables[5].Identifier);
    }

    [Fact]
    public async Task ApplyDelta_ExceedingThreshold_CompactMergeReusesCachedEmbeddings()
    {
        var embedder = new TrackingEmbeddingGenerator();
        var engine = CreateEngine(embedder);

        // 10 base tables => DeltaRebuildRatio (10%) = 1 entry; 2 delta changes triggers compact merge.
        var baseTables = Enumerable.Range(0, 10)
            .Select(i => CreateTable("sales", $"tbl_{i}", $"Order ledger item {i}", visibility: DatasetVisibility.Full))
            .ToList();
        await engine.RebuildIndexAsync(baseTables);
        embedder.ResetCounters();

        var deltaChanges = new[]
        {
            baseTables[0] with { Table = baseTables[0].Table with { Description = "Compaction change zero" } },
            baseTables[1] with { Table = baseTables[1].Table with { Description = "Compaction change one" } }
        };

        await engine.ApplyDeltaAsync(deltaChanges, []);

        // Only the 2 changed delta documents required fresh embedding calls; compaction reused existing vectors.
        embedder.EmbeddedTextCount.ShouldBe(2);
        var snapshot = engine.CurrentSnapshotForTesting!;
        snapshot.DeltaOperationCount.ShouldBe(0);
        snapshot.TombstoneCount.ShouldBe(0);
        snapshot.BaseDocumentCount.ShouldBe(10);
    }

    private static CatalogSearchEngine CreateEngine(
        ILocalEmbeddingGenerator embedder,
        CatalogEmbeddingProvider provider = CatalogEmbeddingProvider.LocalOnnx,
        CatalogSearchEmbeddingCache? cache = null) =>
        new(
            embedder,
            Options.Create(new CatalogSearchOptions
            {
                EmbeddingProvider = provider,
                EmbeddingDimensions = embedder.EmbeddingDimensions,
                MinVectorSimilarity = 0.2f
            }),
            NullLogger<CatalogSearchEngine>.Instance,
            documentEmbeddingCache: cache);

    private static TableMetadata CreateTable(
        string domain,
        string tableName,
        string description,
        IReadOnlyList<string>? columns = null,
        DatasetVisibility visibility = DatasetVisibility.Hidden,
        bool isActive = true) =>
        new()
        {
            Identifier = new TableIdentifier(domain, "public", tableName),
            Table = new Table
            {
                SourceName = domain,
                SchemaName = "public",
                TableName = tableName,
                Description = description,
                Visibility = visibility,
                IsActive = isActive
            },
            Columns = (columns ?? ["id"]).Select(c => new TableColumn { ColumnName = c, DataType = "text" }).ToList(),
            PrimaryKeyColumns = ["id"]
        };

    private static string RandomDoc(Random rng, string[] vocab, int minWords, int maxWords)
    {
        int count = rng.Next(minWords, maxWords + 1);
        return string.Join(' ', Enumerable.Range(0, count).Select(_ => vocab[rng.Next(vocab.Length)]));
    }

    private sealed class TrackingEmbeddingGenerator(
        string modelId = "test-model",
        int dimensions = 4,
        Func<string, float[]>? vectorSelector = null) : ILocalEmbeddingGenerator
    {
        private readonly List<string> _embeddedTexts = [];
        private int _asyncCallCount;

        public bool ThrowOnNextCall { get; set; }
        public int AsyncCallCount => Volatile.Read(ref _asyncCallCount);
        public int EmbeddedTextCount { get { lock (_embeddedTexts) return _embeddedTexts.Count; } }
        public IReadOnlyList<string> EmbeddedTexts { get { lock (_embeddedTexts) return [.. _embeddedTexts]; } }

        public int EmbeddingDimensions => dimensions;
        public string ModelIdentifier => modelId;

        public void ResetCounters()
        {
            Interlocked.Exchange(ref _asyncCallCount, 0);
            lock (_embeddedTexts)
            {
                _embeddedTexts.Clear();
            }
        }

        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => SelectVector(text);

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(
            IReadOnlyList<string> texts,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (ThrowOnNextCall)
            {
                ThrowOnNextCall = false;
                throw new InvalidOperationException("Simulated embedding failure.");
            }

            Interlocked.Increment(ref _asyncCallCount);
            var results = new ReadOnlyMemory<float>[texts.Count];
            lock (_embeddedTexts)
            {
                for (int i = 0; i < texts.Count; i++)
                {
                    _embeddedTexts.Add(texts[i]);
                    results[i] = SelectVector(texts[i]);
                }
            }

            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
        }

        private float[] SelectVector(string text)
        {
            if (vectorSelector != null)
            {
                return vectorSelector(text);
            }

            var vec = new float[dimensions];
            vec[0] = 1f;
            return vec;
        }
    }
}

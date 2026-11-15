namespace Autheris.Tests.Unit.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogSearchWarmupDeltaTests
{
    [Fact]
    public async Task OneTableChange_UsesIncrementalDelta_WithoutReloadingAllTables()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tables = Enumerable.Range(0, 20)
            .Select(i => MakeTable($"tbl_{i}", $"Initial table description {i}", ["id", $"uniqcol{i}z"]))
            .ToList();
        var repo = new InMemoryDeltaMetadataRepository(tables);
        var versionProvider = new MutableCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new InMemoryEventBus();
        var embedder = new CountingEmbeddingGenerator();
        var engine = CreateEngine(embedder);

        using var service = CreateService(repo, engine, versionProvider, bus, time);
        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.IsIndexReady && engine.IndexedTableCount == 20, TimeSpan.FromSeconds(5));

        repo.GetAllCalls.ShouldBe(1);
        embedder.CallTextsCount.ShouldBe(20);
        embedder.CallTextsCount = 0;

        // Modify a single table (5% < 10%): warmup must read only the delta and skip GetAllTablesAsync.
        var targetId = tables[3].Identifier;
        var updated = MakeTable("tbl_3", "Refreshed ledger shipments description", ["id", "ledgercode9"]);
        repo.SetTable(updated, changed: true);
        versionProvider.Version = new CatalogVersion(1, 2);

        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.LastIndexedVersion == (1L, 2L), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        repo.GetAllCalls.ShouldBe(1);
        repo.ChangedSinceCalls.ShouldBeGreaterThanOrEqualTo(1);
        repo.GetTablesBatchCalls.ShouldBe(1);
        embedder.CallTextsCount.ShouldBe(1);

        engine.Search(new CatalogSearchQuery("uniqcol3z", Mode: CatalogSearchMode.KeywordBm25), _ => true).ShouldBeEmpty();
        engine.Search(new CatalogSearchQuery("ledgercode9", Mode: CatalogSearchMode.KeywordBm25), _ => true)
            .Select(h => h.TableIdentifier).ShouldBe([targetId]);
    }

    [Fact]
    public async Task DeactivatedOrRemovedTable_ViaDeltaWarmup_TombstonedWithoutFullRebuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tables = Enumerable.Range(0, 20)
            .Select(i => MakeTable($"t_{i}", $"Inventory table {i}", ["id", $"tokitem{i}z"]))
            .ToList();
        var repo = new InMemoryDeltaMetadataRepository(tables);
        var versionProvider = new MutableCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new InMemoryEventBus();
        var engine = CreateEngine(new CountingEmbeddingGenerator());

        using var service = CreateService(repo, engine, versionProvider, bus, time);
        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.IndexedTableCount == 20, TimeSpan.FromSeconds(5));

        var removedId = tables[7].Identifier;
        repo.MarkRemoved(removedId);
        versionProvider.Version = new CatalogVersion(1, 2);

        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.LastIndexedVersion == (1L, 2L), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        repo.GetAllCalls.ShouldBe(1);
        engine.IndexedTableCount.ShouldBe(19);
        engine.Search(new CatalogSearchQuery("tokitem7z", Mode: CatalogSearchMode.KeywordBm25), _ => true).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeltaAtOrAbove10Percent_FallsBackToFullRebuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tables = Enumerable.Range(0, 20)
            .Select(i => MakeTable($"ratio_{i}", $"Ratio table {i}", ["id"]))
            .ToList();
        var repo = new InMemoryDeltaMetadataRepository(tables);
        var versionProvider = new MutableCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new InMemoryEventBus();
        var engine = CreateEngine(new CountingEmbeddingGenerator());

        using var service = CreateService(repo, engine, versionProvider, bus, time);
        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.IndexedTableCount == 20, TimeSpan.FromSeconds(5));

        repo.SetTable(MakeTable("ratio_0", "Changed 0", ["id"]), changed: true);
        repo.SetTable(MakeTable("ratio_1", "Changed 1", ["id"]), changed: true);
        versionProvider.Version = new CatalogVersion(1, 2);

        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.LastIndexedVersion == (1L, 2L), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        // 2 / 20 = 10% -> went straight to full rebuild without calling batch GetTablesAsync.
        repo.GetTablesBatchCalls.ShouldBe(0);
        repo.GetAllCalls.ShouldBe(2);
    }

    [Fact]
    public async Task DeltaFailure_FallsBackToFullRebuild_AndRetainsPreviousSnapshot_WhenBothFail()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var tables = Enumerable.Range(0, 15)
            .Select(i => MakeTable($"fb_{i}", $"Fallback table {i}", ["id", $"fbcol{i}z"]))
            .ToList();
        var repo = new InMemoryDeltaMetadataRepository(tables);
        var versionProvider = new MutableCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new InMemoryEventBus();
        var engine = CreateEngine(new CountingEmbeddingGenerator());

        using var service = CreateService(repo, engine, versionProvider, bus, time);
        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.IndexedTableCount == 15, TimeSpan.FromSeconds(5));

        // 1) Delta fails, full reload succeeds.
        repo.ThrowOnChangedSince = true;
        repo.SetTable(MakeTable("fb_0", "Recovered full rebuild marker", ["id", "recoveredmarkerz"]), changed: true);
        versionProvider.Version = new CatalogVersion(1, 2);

        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.LastIndexedVersion == (1L, 2L), TimeSpan.FromSeconds(5));

        repo.GetAllCalls.ShouldBe(2);
        engine.Search(new CatalogSearchQuery("recoveredmarkerz", Mode: CatalogSearchMode.KeywordBm25), _ => true).Count.ShouldBe(1);

        // 2) Both delta and full reload fail -> previous index keeps serving and error status is recorded.
        repo.ThrowOnGetAll = true;
        versionProvider.Version = new CatalogVersion(1, 3);
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.GetStatus(time.GetUtcNow()).LastErrorCode == CatalogSearchErrorCodes.RebuildFailed, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        engine.IsIndexReady.ShouldBeTrue();
        engine.Search(new CatalogSearchQuery("recoveredmarkerz", Mode: CatalogSearchMode.KeywordBm25), _ => true).Count.ShouldBe(1);
    }

    private static CatalogSearchWarmupService CreateService(
        ITableMetadataRepository repo,
        ICatalogSearchEngine engine,
        ICatalogVersionProvider versionProvider,
        IEventBus bus,
        FakeTimeProvider time) =>
        new(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { Enabled = true, WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: bus,
            versionProvider: versionProvider,
            timeProvider: time);

    private static CatalogSearchEngine CreateEngine(ILocalEmbeddingGenerator embedder) =>
        new(
            embedder,
            Options.Create(new CatalogSearchOptions { EmbeddingProvider = CatalogEmbeddingProvider.LocalOnnx, EmbeddingDimensions = 4 }),
            NullLogger<CatalogSearchEngine>.Instance);

    private static TableMetadata MakeTable(string name, string description, string[] columns) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            SourceName = "sales",
            SchemaName = "public",
            TableName = name,
            Description = description,
            Visibility = DatasetVisibility.Full,
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "text" }).ToList(),
        PrimaryKeyColumns = ["id"]
    };

    private sealed class MutableCatalogVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Version { get; set; } = initial;
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => new(Version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Version.Formatted);
    }

    private sealed class InMemoryEventBus : IEventBus
    {
        private readonly List<Func<object, Task>> _handlers = [];
        public event Action? ConnectionRestored { add { } remove { } }
        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default)
            => string.Equals(channel, ICatalogVersionProvider.CatalogChangedEventChannel, StringComparison.Ordinal)
                ? Task.WhenAll(_handlers.Select(h => h(payload!)))
                : Task.CompletedTask;
        public IDisposable Subscribe<TEvent>(string channel, Func<TEvent, Task> handler)
        {
            if (string.Equals(channel, ICatalogVersionProvider.CatalogChangedEventChannel, StringComparison.Ordinal))
            {
                _handlers.Add(payload => handler((TEvent)payload));
            }
            return new Noop();
        }
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private sealed class CountingEmbeddingGenerator : ILocalEmbeddingGenerator
    {
        public int CallTextsCount { get; set; }
        public int EmbeddingDimensions => 4;
        public string ModelIdentifier => "test-model";
        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => new[] { 1f, 0f, 0f, 0f };
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            CallTextsCount += texts.Count;
            IReadOnlyList<ReadOnlyMemory<float>> list = texts.Select(_ => (ReadOnlyMemory<float>)new[] { 1f, 0f, 0f, 0f }).ToArray();
            return Task.FromResult(list);
        }
    }

    private sealed class InMemoryDeltaMetadataRepository(IEnumerable<TableMetadata> initial) : ITableMetadataRepository
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _tables = initial.ToDictionary(t => t.Identifier);
        private readonly HashSet<TableIdentifier> _changed = [];

        public int GetAllCalls { get; private set; }
        public int ChangedSinceCalls { get; private set; }
        public int GetTablesBatchCalls { get; private set; }
        public bool ThrowOnChangedSince { get; set; }
        public bool ThrowOnGetAll { get; set; }

        public void SetTable(TableMetadata table, bool changed)
        {
            _tables[table.Identifier] = table;
            if (changed) _changed.Add(table.Identifier);
        }

        public void MarkRemoved(TableIdentifier id)
        {
            _tables.Remove(id);
            _changed.Add(id);
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            if (ThrowOnGetAll) throw new InvalidOperationException("Simulated catalog store unavailable.");
            return Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToArray());
        }

        public Task<IReadOnlyList<TableIdentifier>> GetTablesChangedSinceAsync(long sinceVersion, CancellationToken ct = default)
        {
            ChangedSinceCalls++;
            if (ThrowOnChangedSince) throw new InvalidOperationException("Simulated delta read unavailable.");
            IReadOnlyList<TableIdentifier> snapshot = _changed.ToArray();
            _changed.Clear();
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<TableMetadata>> GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
        {
            GetTablesBatchCalls++;
            IReadOnlyList<TableMetadata> result = identifiers.Where(_tables.ContainsKey).Select(id => _tables[id]).ToArray();
            return Task.FromResult(result);
        }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(_tables.TryGetValue(table, out var m) ? m : null);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}

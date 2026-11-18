namespace Autheris.Tests.Unit.Catalog;

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
using Autheris.Infrastructure.Health;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogSearchWarmupServiceTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ICatalogSearchEngine _searchEngine = Substitute.For<ICatalogSearchEngine>();
    private readonly ICatalogVersionProvider _versionProvider = Substitute.For<ICatalogVersionProvider>();

    private static TableMetadata CreateTable(string table) => new()
    {
        Identifier = new TableIdentifier("default", "public", table),
        Table = new Table { Id = Guid.NewGuid(), SourceName = "default", SchemaName = "public", TableName = table, DisplayName = table, IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    [Fact]
    public async Task WarmupOnStartup_BuildsIndexOnce()
    {
        var tables = new List<TableMetadata> { CreateTable("t1") };
        _metadataRepo.StubAllTables(tables);
        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var options = Options.Create(new CatalogSearchOptions
        {
            WarmupOnStartup = true,
            RefreshSeconds = 0
        });

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: _versionProvider);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);
        await WaitHelper.WaitUntil(() => _searchEngine.ReceivedCalls().Any(), TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        await _searchEngine.Received(1).RebuildIndexAsync(Arg.Is<IReadOnlyList<TableMetadata>>(l => l.SequenceEqual(tables)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatalogChangedEvent_RebuildsIndex()
    {
        var tables1 = new List<TableMetadata> { CreateTable("t1") };
        var tables2 = new List<TableMetadata> { CreateTable("t1"), CreateTable("t2") };

        _metadataRepo.StubAllTablesSequence(tables1, tables2);

        Func<object, Task>? eventHandler = null;
        var eventBus = Substitute.For<IEventBus>();
        eventBus.Subscribe<object>(ICatalogVersionProvider.CatalogChangedEventChannel, Arg.Do<Func<object, Task>>(h => eventHandler = h))
            .Returns(new NoopDisposable());

        var options = Options.Create(new CatalogSearchOptions
        {
            WarmupOnStartup = true,
            RefreshSeconds = 0
        });

        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: eventBus,
            versionProvider: _versionProvider,
            timeProvider: fakeTime);

        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => service.GetStatus().LastSuccessUtc.HasValue, TimeSpan.FromSeconds(5));

        // Publish catalog:changed via captured handler and advance past minimum rebuild interval
        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(2, 2)));
        eventHandler.ShouldNotBeNull();
        await eventHandler(new object());
        await WaitHelper.WaitUntil(() => service.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        fakeTime.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await WaitHelper.WaitUntil(() => _searchEngine.ReceivedCalls().Count() >= 2, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        await _searchEngine.Received(2).RebuildIndexAsync(Arg.Any<IReadOnlyList<TableMetadata>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PeriodicRefresh_SkipsWhenVersionUnchanged()
    {
        var tables = new List<TableMetadata> { CreateTable("t1") };
        _metadataRepo.StubAllTables(tables);

        // Version stays at (1, 1)
        _versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVersion>(new CatalogVersion(1, 1)));

        var options = Options.Create(new CatalogSearchOptions
        {
            WarmupOnStartup = true,
            RefreshSeconds = 1 // 1 second interval
        });

        using var service = new CatalogSearchWarmupService(
            _metadataRepo,
            _searchEngine,
            options,
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: _versionProvider);

        await service.StartAsync(CancellationToken.None);
        try { await WaitHelper.WaitUntil(() => false, TimeSpan.FromMilliseconds(1200)); } catch (TimeoutException) { }
        await service.StopAsync(CancellationToken.None);

        // Only the initial warmup build should have occurred
        await _searchEngine.Received(1).RebuildIndexAsync(Arg.Is<IReadOnlyList<TableMetadata>>(l => l.SequenceEqual(tables)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HealthCheck_ExposesCatalogSearchIndexComponent()
    {
        var engine = Substitute.For<ICatalogSearchEngine>();
        engine.IsIndexReady.Returns(true);
        engine.IndexedTableCount.Returns(42);
        var time = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        engine.LastIndexedAt.Returns(time);

        var healthService = new GatewayHealthCheckService(
            Options.Create(new GatewayOptions()),
            NullLogger<GatewayHealthCheckService>.Instance,
            catalogSearchEngine: engine);

        var report = await healthService.CheckHealthAsync();

        var comp = report.Components.FirstOrDefault(c => c.Name == "CatalogSearchIndex");
        comp.Name.ShouldBe("CatalogSearchIndex");
        comp.IsHealthy.ShouldBeTrue();
        comp.Description.ShouldNotBeNull();
        comp.Description.ShouldContain("42 tables");
        comp.Description.ShouldContain("2026-11-04");
    }

    [Fact]
    public async Task VersionChangeDuringRebuild_TriggersSecondRebuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var versionProvider = new SequenceCatalogVersionProvider(
            new CatalogVersion(1, 1),
            new CatalogVersion(2, 1));
        var rebuildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRebuildToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new FakeWarmupSearchEngine(async (count, _) =>
        {
            if (count == 1)
            {
                rebuildStarted.TrySetResult();
                await allowFirstRebuildToFinish.Task;
            }
        }, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await rebuildStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Catalog version advances mid-rebuild (no extra event required).
        versionProvider.AdvanceTo(new CatalogVersion(2, 1));
        allowFirstRebuildToFinish.TrySetResult();

        await WaitHelper.WaitUntil(() => engine.Status.LastSuccessUtc.HasValue, TimeSpan.FromSeconds(5));
        await WaitHelper.WaitUntil(() => service.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);

        await WaitHelper.WaitUntil(() => engine.RebuildCount >= 2, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        engine.RebuildCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task FailedRebuild_KeepsPreviousVersionStamp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var versionProvider = new SequenceCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new FakeEventBus();
        var engine = new FakeWarmupSearchEngine((count, _) =>
        {
            if (count == 2)
            {
                throw new InvalidOperationException("sensitive secret connection failure");
            }

            return Task.CompletedTask;
        }, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: bus,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 1, TimeSpan.FromSeconds(5));

        // Second version triggers rebuild #2 which fails.
        versionProvider.AdvanceTo(new CatalogVersion(2, 1));
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.Status.LastErrorCode == CatalogSearchErrorCodes.RebuildFailed, TimeSpan.FromSeconds(5));

        // Same version (2, 1) must still be rebuilt on next cycle because failed rebuild kept version (1, 1).
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 3 && engine.Status.LastErrorCode == null, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        engine.RebuildCount.ShouldBe(3);
    }

    [Fact]
    public async Task EventBurstOf100_CausesOneRebuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var versionProvider = new SequenceCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new FakeEventBus();
        var engine = new FakeWarmupSearchEngine((_, _) => Task.CompletedTask, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: bus,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 1, TimeSpan.FromSeconds(5));

        versionProvider.AdvanceTo(new CatalogVersion(2, 1));
        for (int i = 0; i < 100; i++)
        {
            await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        }

        await WaitHelper.WaitUntil(() => service.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 2, TimeSpan.FromSeconds(5));

        // Advancing time further without new version changes should not trigger any extra rebuild.
        time.Advance(TimeSpan.FromSeconds(30));
        await service.StopAsync(CancellationToken.None);

        engine.RebuildCount.ShouldBe(2);
    }

    [Fact]
    public async Task RebuildsAreAtLeastMinimumIntervalApart()
    {
        var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var versionProvider = new SequenceCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new FakeEventBus();
        var rebuildTimes = new List<DateTimeOffset>();
        var engine = new FakeWarmupSearchEngine((_, _) =>
        {
            lock (rebuildTimes)
            {
                rebuildTimes.Add(time.GetUtcNow());
            }

            return Task.CompletedTask;
        }, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: bus,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 1, TimeSpan.FromSeconds(5));

        // 3 seconds after startup: event must wait for the remaining cooldown.
        versionProvider.AdvanceTo(new CatalogVersion(2, 1));
        time.Advance(TimeSpan.FromSeconds(3));
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => service.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        service.HasPendingRebuild.ShouldBeTrue();
        engine.RebuildCount.ShouldBe(1);

        // Advance remaining 2 seconds -> total 5 seconds since first rebuild.
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 2, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        rebuildTimes.Count.ShouldBe(2);
        (rebuildTimes[1] - rebuildTimes[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task VersionReadFailure_SetsClosedErrorCodeAndKeepsPreviousVersionStamp()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var versionProvider = new SequenceCatalogVersionProvider(new CatalogVersion(1, 1));
        var bus = new FakeEventBus();
        var engine = new FakeWarmupSearchEngine((_, _) => Task.CompletedTask, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            Options.Create(new CatalogSearchOptions { WarmupOnStartup = true, RefreshSeconds = 0 }),
            NullLogger<CatalogSearchWarmupService>.Instance,
            eventBus: bus,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 1, TimeSpan.FromSeconds(5));

        versionProvider.NextError = new TimeoutException("postgres password=secret timeout");
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.Status.LastErrorCode == CatalogSearchErrorCodes.VersionReadFailed, TimeSpan.FromSeconds(5));

        versionProvider.NextError = null;
        versionProvider.AdvanceTo(new CatalogVersion(2, 1));
        time.Advance(CatalogSearchWarmupService.MinimumRebuildInterval);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await WaitHelper.WaitUntil(() => engine.RebuildCount == 2 && engine.Status.LastErrorCode == null, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        engine.RebuildCount.ShouldBe(2);
    }

    [Fact]
    public async Task EmbeddingFailure_SetsEmbeddingFailedErrorCodeAndHidesExceptionText()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var failingEmbeddings = new FailingEmbeddingGenerator("ollama bearer-secret-123 failure");
        var searchOptions = Options.Create(new CatalogSearchOptions
        {
            WarmupOnStartup = true,
            RefreshSeconds = 0,
            EmbeddingProvider = CatalogEmbeddingProvider.Ollama
        });
        var engine = new CatalogSearchEngine(failingEmbeddings, searchOptions, NullLogger<CatalogSearchEngine>.Instance, time);
        var repo = new FakeTableMetadataRepository([CreateTable("orders")]);
        var versionProvider = new SequenceCatalogVersionProvider(new CatalogVersion(1, 1));

        using var service = new CatalogSearchWarmupService(
            repo,
            engine,
            searchOptions,
            NullLogger<CatalogSearchWarmupService>.Instance,
            versionProvider: versionProvider,
            timeProvider: time);

        await service.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => engine.GetStatus(time.GetUtcNow()).LastErrorCode == CatalogSearchErrorCodes.EmbeddingFailed, TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        var status = engine.GetStatus(time.GetUtcNow());
        status.LastErrorCode.ShouldBe(CatalogSearchErrorCodes.EmbeddingFailed);
        status.LastSuccessUtc.ShouldBeNull();
        status.LastAttemptUtc.ShouldNotBeNull();
    }

    private sealed class SequenceCatalogVersionProvider : ICatalogVersionProvider
    {
        private readonly object _gate = new();
        private readonly Queue<CatalogVersion> _sequence;
        private CatalogVersion _current;

        public Exception? NextError { get; set; }

        public SequenceCatalogVersionProvider(params CatalogVersion[] versions)
        {
            _sequence = new Queue<CatalogVersion>(versions);
            _current = versions.Length > 0 ? versions[^1] : default;
        }

        public void AdvanceTo(CatalogVersion next)
        {
            lock (_gate)
            {
                _sequence.Clear();
                _current = next;
            }
        }

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
        {
            if (NextError is { } ex)
            {
                return ValueTask.FromException<CatalogVersion>(ex);
            }

            lock (_gate)
            {
                if (_sequence.Count > 0)
                {
                    _current = _sequence.Dequeue();
                }

                return new ValueTask<CatalogVersion>(_current);
            }
        }

        public async Task<string> GetVersionAsync(CancellationToken ct = default) =>
            (await GetCatalogVersionAsync(ct).ConfigureAwait(false)).Formatted;
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly Dictionary<string, Func<object, Task>> _handlers = new();

        public event Action? ConnectionRestored { add { } remove { } }

        public Task PublishAsync<TEvent>(string channel, TEvent payload, CancellationToken ct = default)
        {
            if (_handlers.TryGetValue(channel, out var handler))
            {
                return handler(payload!);
            }

            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(string channel, Func<TEvent, Task> handler)
        {
            _handlers[channel] = payload => handler((TEvent)payload);
            return new NoopDisposable();
        }

        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeTableMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FailingEmbeddingGenerator(string errorMessage) : ILocalEmbeddingGenerator
    {
        public int EmbeddingDimensions => 3;
        public string ModelIdentifier => "test-ollama";
        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => throw new InvalidOperationException(errorMessage);
        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<ReadOnlyMemory<float>>>(new HttpRequestException(errorMessage));
    }

    private sealed class FakeWarmupSearchEngine(
        Func<int, CancellationToken, Task> onRebuild,
        TimeProvider timeProvider) : ICatalogSearchEngine, ICatalogSearchStatusRecorder
    {
        private int _invocationCount;
        private int _rebuildCount;
        private DateTimeOffset? _lastSuccessUtc;
        private DateTimeOffset? _lastAttemptUtc;
        private string? _lastErrorCode;

        public int RebuildCount => Volatile.Read(ref _rebuildCount);
        public bool IsIndexReady => _lastSuccessUtc.HasValue;
        public int IndexedTableCount { get; private set; }
        public DateTimeOffset? LastIndexedAt => _lastSuccessUtc;
        public (long StructureVersion, long ContentVersion)? LastIndexedVersion { get; private set; }
        public CatalogSearchIndexStatus Status => GetStatus(timeProvider.GetUtcNow());

        public async Task RebuildIndexAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _invocationCount);
            await onRebuild(n, ct);
            IndexedTableCount = tables.Count;
            _lastSuccessUtc = timeProvider.GetUtcNow();
        }

        public CatalogSearchIndexStatus GetStatus(DateTimeOffset? utcNow = null)
        {
            var now = utcNow ?? timeProvider.GetUtcNow();
            return new CatalogSearchIndexStatus(
                _lastSuccessUtc,
                _lastAttemptUtc,
                _lastErrorCode,
                _lastSuccessUtc.HasValue ? now - _lastSuccessUtc.Value : null);
        }

        public void RecordRebuildOutcome(
            DateTimeOffset? lastSuccessUtc,
            DateTimeOffset lastAttemptUtc,
            string? lastErrorCode,
            (long StructureVersion, long ContentVersion)? indexedVersion)
        {
            if (lastSuccessUtc.HasValue)
            {
                _lastSuccessUtc = lastSuccessUtc;
            }

            _lastAttemptUtc = lastAttemptUtc;
            _lastErrorCode = lastErrorCode;
            if (indexedVersion.HasValue)
            {
                LastIndexedVersion = indexedVersion;
            }

            if (lastErrorCode != CatalogSearchErrorCodes.VersionReadFailed)
            {
                Interlocked.Increment(ref _rebuildCount);
            }
        }

        public IReadOnlyList<CatalogSearchHit> Search(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null) => [];
        public Task<IReadOnlyList<CatalogSearchHit>> SearchAsync(CatalogSearchQuery query, Func<TableIdentifier, bool> isVisiblePredicate, Func<TableIdentifier, bool>? isColumnRestricted = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CatalogSearchHit>>([]);
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

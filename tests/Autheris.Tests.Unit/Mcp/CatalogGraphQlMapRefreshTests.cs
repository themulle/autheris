namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class CatalogGraphQlMapRefreshTests
{
    private static readonly TableIdentifier Customers = new("crm", "dbo", "customers");
    private static readonly TableIdentifier Orders = new("crm", "dbo", "orders");
    private static readonly TableIdentifier Invoices = new("erp", "dbo", "invoices");

    private static TableMetadata SqlTable(TableIdentifier id, string sourceName = "pg_main") => new()
    {
        Identifier = id,
        Table = new Table
        {
            TableName = id.TableName,
            SchemaName = id.Schema,
            IsActive = true,
            SourceType = "PostgreSql",
            SourceName = sourceName,
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "name", DataType = "varchar" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    [Fact]
    public async Task GetTableAsync_UsesCaseInsensitiveDictionaryLookup_AndMemoizesMapping()
    {
        var metadata = new FakeMetadataRepo([SqlTable(Customers), SqlTable(Orders)]);
        using var map = new CatalogGraphQlMap(metadata, new FakeRelationRepo());

        var first = await map.GetTableAsync(Orders);
        var upper = await map.GetTableAsync(new TableIdentifier("CRM", "DBO", "ORDERS"));
        var second = await map.GetTableAsync(Orders);

        first.ShouldNotBeNull();
        first.Table.ShouldBe(Orders);
        first.QueryField.ShouldBe("crm_dbo_orders");
        ReferenceEquals(first, upper).ShouldBeTrue();
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public async Task ColdStart_ConcurrentCallers_CoalesceIntoSingleBuild_AndIsolateCallerCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadata = new FakeMetadataRepo([SqlTable(Customers), SqlTable(Orders)])
        {
            BlockRead = async ct =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };
        using var map = new CatalogGraphQlMap(metadata, new FakeRelationRepo());

        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();
        var c1 = map.GetTableAsync(Customers, cts1.Token);
        var c2 = map.GetRootFieldAsync(Orders, cts2.Token);
        var c3 = map.GetTableAsync(Orders);
        var c4 = map.ResolveRootFieldAsync("crm_dbo_customers");

        await entered.Task.WaitAsync(WaitHelper.HangGuard);
        cts1.Cancel();
        cts2.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => c1.WaitAsync(WaitHelper.HangGuard));
        await Should.ThrowAsync<OperationCanceledException>(() => c2.WaitAsync(WaitHelper.HangGuard));
        c3.IsCompleted.ShouldBeFalse();
        c4.IsCompleted.ShouldBeFalse();

        release.SetResult();
        (await c3.WaitAsync(WaitHelper.HangGuard)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_orders");
        (await c4.WaitAsync(WaitHelper.HangGuard)).ShouldBe(Customers);
        metadata.ReadCount.ShouldBe(1);
    }

    [Fact]
    public async Task ColdStart_Failure_DoesNotCacheEmptyModel_AndNextCallRetries()
    {
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { FailReadsRemaining = 1 };
        using var map = new CatalogGraphQlMap(metadata, new FakeRelationRepo());

        await Should.ThrowAsync<InvalidOperationException>(() => map.GetTableAsync(Customers));

        var resolved = await map.GetTableAsync(Customers);
        resolved.ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_customers");
        metadata.ReadCount.ShouldBe(2);
    }

    [Fact]
    public async Task WarmMap_TimerTicksWithUnchangedCatalogVersion_NeverRebuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var snapshot = new FakeSnapshotProvider(CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []));
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]);
        using var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            refreshInterval: TimeSpan.FromSeconds(30),
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();
        snapshot.GetCount.ShouldBe(1);

        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromSeconds(30));
            await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);
        }

        snapshot.GetCount.ShouldBe(1);
        metadata.ReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task WarmMap_StaleWhileRevalidate_ServesPreviousModelWithoutBlockingDuringBackgroundRefresh()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var initialSnap = CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []);
        var snapshot = new FakeSnapshotProvider(initialSnap);
        var bus = new FakeEventBus();
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = version.Current };

        await using var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            eventBus: bus,
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();

        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.NextOverride = async ct =>
        {
            refreshEntered.TrySetResult();
            return await releaseRefresh.Task.WaitAsync(ct);
        };

        var v2 = new CatalogVersion(2, 1);
        version.Current = v2;
        metadata.Version = v2;
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(2, 1, time.GetUtcNow()));
        await refreshEntered.Task.WaitAsync(WaitHelper.HangGuard);

        var warmLookup = map.GetTableAsync(Customers);
        var warmRoot = map.GetRootFieldAsync(Customers);
        warmLookup.IsCompleted.ShouldBeTrue();
        warmRoot.IsCompleted.ShouldBeTrue();
        (await warmLookup)!.QueryField.ShouldBe("crm_dbo_customers");
        (await warmRoot).ShouldBe("crm_dbo_customers");

        releaseRefresh.SetResult(CatalogSnapshot.Create(v2, [SqlTable(Customers), SqlTable(Orders)], []));
        await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);

        (await map.GetTableAsync(Orders)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_orders");
    }

    [Fact]
    public async Task WarmMap_BackgroundRefreshTransientFailure_KeepsServingLastKnownGoodModel_AndRecoversOnNextTick()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var snap1 = CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []);
        var snapshot = new FakeSnapshotProvider(snap1);
        var bus = new FakeEventBus();
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = version.Current };

        await using var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            eventBus: bus,
            refreshInterval: TimeSpan.FromSeconds(30),
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();

        var v2 = new CatalogVersion(2, 1);
        version.Current = v2;
        metadata.Version = v2;
        snapshot.FailNextRefresh = true;
        snapshot.NextSnapshot = CatalogSnapshot.Create(v2, [SqlTable(Customers), SqlTable(Orders)], []);

        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(2, 1, time.GetUtcNow()));
        await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_customers");
        (await map.GetTableAsync(Orders)).ShouldBeNull();

        snapshot.FailNextRefresh = false;
        time.Advance(TimeSpan.FromSeconds(30));
        await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);

        (await map.GetTableAsync(Orders)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_orders");
    }

    [Fact]
    public async Task WarmMap_BurstOfCatalogChangedEventsDuringInFlightBuild_CoalescesIntoOneTrailingRefresh()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var snapshot = new FakeSnapshotProvider(CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []));
        var bus = new FakeEventBus();
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = version.Current };

        await using var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            eventBus: bus,
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();
        snapshot.GetCount.ShouldBe(1);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.NextOverride = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return CatalogSnapshot.Create(new CatalogVersion(2, 1), [SqlTable(Customers), SqlTable(Orders)], []);
        };

        version.Current = new CatalogVersion(2, 1);
        metadata.Version = version.Current;
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(2, 1, time.GetUtcNow()));
        await entered.Task.WaitAsync(WaitHelper.HangGuard);

        var v12 = new CatalogVersion(12, 1);
        version.Current = v12;
        metadata.Version = v12;
        snapshot.NextSnapshot = CatalogSnapshot.Create(v12, [SqlTable(Customers), SqlTable(Orders), SqlTable(Invoices)], []);
        for (var i = 3; i <= 12; i++)
        {
            await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(i, 1, time.GetUtcNow()));
        }

        release.SetResult();
        await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);

        snapshot.GetCount.ShouldBe(3);
        (await map.GetTableAsync(Invoices)).ShouldNotBeNull().QueryField.ShouldBe("erp_dbo_invoices");
    }

    [Fact]
    public async Task TenMinuteBackstop_RebuildsInBackgroundEvenWhenVersionUnchanged()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var snapshot = new FakeSnapshotProvider(CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []));
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = version.Current };

        await using var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            refreshInterval: TimeSpan.FromSeconds(30),
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();
        var initialReads = snapshot.GetCount;

        snapshot.NextSnapshot = CatalogSnapshot.Create(version.Current, [SqlTable(Customers), SqlTable(Orders)], []);
        time.Advance(TimeSpan.FromMinutes(10));
        await map.WaitForIdleAsync().WaitAsync(WaitHelper.HangGuard);

        snapshot.GetCount.ShouldBe(initialReads + 1);
        (await map.GetTableAsync(Orders)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TypeModuleIntegration_ReusesCurrentSchemaModelOnColdStart_AndAdoptsPushedRebuilds()
    {
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = new CatalogVersion(3, 1) };
        var relations = new FakeRelationRepo();
        var version = new FakeVersionProvider(new CatalogVersion(3, 1));
        using var typeModule = new CatalogGraphQlTypeModule(metadata, relations, versionProvider: version);

        await typeModule.CreateTypesAsync(null!, CancellationToken.None);

        var failingMeta = new FakeMetadataRepo([]) { FailReadsRemaining = 10, Version = new CatalogVersion(3, 1) };
        var failingSnap = new FakeSnapshotProvider(CatalogSnapshot.Empty()) { FailNextRefresh = true };
        await using var map = new CatalogGraphQlMap(
            failingMeta,
            relations,
            versionProvider: version,
            snapshotProvider: failingSnap,
            typeModule: typeModule);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_customers");
        failingMeta.ReadCount.ShouldBe(0);
        failingSnap.GetCount.ShouldBe(0);

        metadata.SetTables([SqlTable(Customers), SqlTable(Orders)], new CatalogVersion(4, 1));
        version.Current = new CatalogVersion(4, 1);
        await typeModule.CreateTypesAsync(null!, CancellationToken.None);

        (await map.GetTableAsync(Orders)).ShouldNotBeNull().QueryField.ShouldBe("crm_dbo_orders");

        // Monotonicity: an older model version push cannot downgrade the active map state.
        var staleMeta = new FakeMetadataRepo([SqlTable(Customers)]) { Version = new CatalogVersion(2, 1) };
        var staleVersion = new FakeVersionProvider(new CatalogVersion(2, 1));
        using var staleModule = new CatalogGraphQlTypeModule(staleMeta, relations, versionProvider: staleVersion);
        await staleModule.CreateTypesAsync(null!, CancellationToken.None);
        staleModule.TryGetCurrentSchemaModel(out var staleModel, out var staleVer).ShouldBeTrue();
        map.AdoptModelForTesting(staleModel!, staleVer);

        (await map.GetTableAsync(Orders)).ShouldNotBeNull("older pushed version must not replace newer map state");
    }

    [Fact]
    public async Task CrossSourceEnabled_ExposesCrossSourceRelationsMatchingGraphQlSchema()
    {
        var metadata = new FakeMetadataRepo([SqlTable(Customers, "pg1"), SqlTable(Invoices, "pg2")]);
        var rel = new TableRelation
        {
            Id = Guid.NewGuid(),
            RelationName = "customer_invoices",
            ParentTableIdentifier = Customers,
            ChildTableIdentifier = Invoices,
            Cardinality = RelationCardinality.OneToMany,
            AllowCrossDataSource = true,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["id"]
        };
        var relations = new FakeRelationRepo(rel);

        await using var mapDisabled = new CatalogGraphQlMap(metadata, relations, crossSourceEnabled: false, refreshInterval: TimeSpan.Zero);
        await using var mapEnabled = new CatalogGraphQlMap(metadata, relations, crossSourceEnabled: true, refreshInterval: TimeSpan.Zero);

        (await mapDisabled.GetTableAsync(Customers))!.Relations.ShouldBeEmpty();
        (await mapEnabled.GetTableAsync(Customers))!.Relations.ShouldHaveSingleItem().Target.ShouldBe(Invoices);
    }

    [Fact]
    public async Task DisposeAsync_UnsubscribesEventsStopsTimerAndDrainsInFlightBuild()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        var version = new FakeVersionProvider(new CatalogVersion(1, 1));
        var snapshot = new FakeSnapshotProvider(CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []));
        var bus = new FakeEventBus();
        var metadata = new FakeMetadataRepo([SqlTable(Customers)]) { Version = version.Current };

        var map = new CatalogGraphQlMap(
            metadata,
            new FakeRelationRepo(),
            versionProvider: version,
            snapshotProvider: snapshot,
            eventBus: bus,
            refreshInterval: TimeSpan.FromSeconds(30),
            timeProvider: time);

        (await map.GetTableAsync(Customers)).ShouldNotBeNull();
        bus.SubscriptionCount.ShouldBe(1);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CatalogSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.NextOverride = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        version.Current = new CatalogVersion(2, 1);
        metadata.Version = version.Current;
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(2, 1, time.GetUtcNow()));
        await entered.Task.WaitAsync(WaitHelper.HangGuard);

        var disposeTask = map.DisposeAsync().AsTask();
        disposeTask.IsCompleted.ShouldBeFalse();
        release.SetResult(CatalogSnapshot.Create(version.Current, [SqlTable(Customers)], []));
        await disposeTask.WaitAsync(WaitHelper.HangGuard);

        bus.SubscriptionCount.ShouldBe(0);
        await Should.ThrowAsync<ObjectDisposedException>(() => map.GetTableAsync(Customers));

        var countAtDispose = snapshot.GetCount;
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new CatalogChangedEvent(3, 1, time.GetUtcNow()));
        time.Advance(TimeSpan.FromMinutes(1));
        snapshot.GetCount.ShouldBe(countAtDispose);
    }

    private sealed class FakeMetadataRepo(IReadOnlyList<TableMetadata> initial) : ITableMetadataRepository
    {
        private IReadOnlyList<TableMetadata> _tables = initial;
        private int _reads;

        public CatalogVersion Version { get; set; } = new(1, 1);
        public int FailReadsRemaining { get; set; }
        public Func<CancellationToken, Task>? BlockRead { get; set; }
        public int ReadCount => Volatile.Read(ref _reads);

        public void SetTables(IReadOnlyList<TableMetadata> tables, CatalogVersion version)
        {
            _tables = tables;
            Version = version;
        }

        public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _reads);
            if (BlockRead is { } block)
            {
                await block(ct);
            }

            if (FailReadsRemaining > 0)
            {
                FailReadsRemaining--;
                throw new InvalidOperationException("catalog read failed");
            }

            return _tables;
        }

        public Task<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => Task.FromResult(Version);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.FirstOrDefault(t => t.Identifier == table));

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeRelationRepo(params TableRelation[] relations) : ITableRelationRepository
    {
        public bool BulkRelationsAuthoritative => true;

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(relations);

        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>(relations.Where(r => r.ParentTableIdentifier == parentTable).ToList());

        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Current.Formatted);
    }

    private sealed class FakeSnapshotProvider(CatalogSnapshot initial) : ICatalogSnapshotProvider
    {
        private int _getCount;
        private CatalogSnapshot _current = initial;

        public bool IsInitialized { get; private set; } = true;
        public CatalogSnapshot CurrentSnapshot => _current;
        public CatalogSnapshot? NextSnapshot { get; set; }
        public Func<CancellationToken, Task<CatalogSnapshot>>? NextOverride { get; set; }
        public bool FailNextRefresh { get; set; }
        public int GetCount => Volatile.Read(ref _getCount);

        public async ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getCount);
            if (FailNextRefresh)
            {
                throw new InvalidOperationException("snapshot read failed");
            }

            if (NextOverride is { } nextOverride)
            {
                NextOverride = null;
                _current = await nextOverride(ct);
                return _current;
            }

            if (NextSnapshot is { } next)
            {
                _current = next;
            }

            return _current;
        }

        public Task RefreshAsync(CancellationToken ct = default)
        {
            if (NextSnapshot is { } next)
            {
                _current = next;
            }

            IsInitialized = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly List<(string Channel, Func<object, Task> Handler)> _handlers = [];

        public event Action? ConnectionRestored;
        public int SubscriptionCount => _handlers.Count;

        public void RaiseConnectionRestored() => ConnectionRestored?.Invoke();

        public async Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
        {
            foreach (var (c, h) in _handlers.ToArray())
            {
                if (string.Equals(c, channel, StringComparison.Ordinal))
                {
                    await h(message!);
                }
            }
        }

        public IDisposable Subscribe<T>(string channel, Func<T, Task> handler)
        {
            var entry = (channel, (Func<object, Task>)(o => o is T t ? handler(t) : Task.CompletedTask));
            _handlers.Add(entry);
            return new Subscription(() => _handlers.Remove(entry));
        }

        public Task<long> IncrementCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> GetCounterAsync(string key, CancellationToken ct = default) => Task.FromResult(0L);

        private sealed class Subscription(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}

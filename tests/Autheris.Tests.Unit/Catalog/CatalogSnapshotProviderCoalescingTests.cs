#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CatalogSnapshotProviderCoalescingTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static TableMetadata CreateTable(string name, Guid? id = null) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            Id = id ?? Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "public",
            TableName = name,
            DisplayName = name,
            IsActive = true
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
    };

    private static TableRelation CreateRelation(TableMetadata parent, TableMetadata child) => new()
    {
        Id = Guid.NewGuid(),
        ParentTableId = parent.Table.Id,
        ChildTableId = child.Table.Id,
        RelationName = $"{parent.Identifier.TableName}_{child.Identifier.TableName}",
        Cardinality = RelationCardinality.OneToMany,
        ParentTableIdentifier = parent.Identifier,
        ChildTableIdentifier = child.Identifier,
        JoinKeysParent = ["id"],
        JoinKeysChild = [$"{parent.Identifier.TableName}_id"]
    };

    [Fact]
    public async Task Burst1000Events_AtMostOneRefreshPerInterval()
    {
        var time = new FakeTimeProvider(StartUtc);
        var bus = new TestEventBus();
        var version = new CatalogVersion(1, 1);
        var refreshTimestamps = new List<DateTimeOffset>();
        var tableLoadCount = 0;

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<CatalogVersion>(version));

        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(_ =>
            {
                lock (refreshTimestamps)
                {
                    refreshTimestamps.Add(time.GetUtcNow());
                }

                Interlocked.Increment(ref tableLoadCount);
                return Task.FromResult<IReadOnlyList<TableMetadata>>([CreateTable($"t_{version.StructureVersion}")]);
            });
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));

        await using var provider = new CatalogSnapshotProvider(repo, versionProvider, bus, timeProvider: time);
        await provider.RefreshAsync();
        Volatile.Read(ref tableLoadCount).ShouldBe(1);

        // Fire 1000 rapid events inside a single interval right after the initial refresh.
        for (var i = 2; i <= 1001; i++)
        {
            version = new CatalogVersion(i, i);
            await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        }

        await WaitHelper.WaitUntil(() => provider.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        Volatile.Read(ref tableLoadCount).ShouldBe(1);

        time.Advance(CatalogSnapshotProvider.MinimumRefreshInterval);
        await WaitHelper.WaitUntil(() => Volatile.Read(ref tableLoadCount) == 2, TimeSpan.FromSeconds(5));

        provider.CurrentSnapshot.Version.ShouldBe(new CatalogVersion(1001, 1001));
        refreshTimestamps.Count.ShouldBe(2);
        (refreshTimestamps[1] - refreshTimestamps[0]).ShouldBeGreaterThanOrEqualTo(CatalogSnapshotProvider.MinimumRefreshInterval);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task LastEventOfBurst_IsNotLost()
    {
        var time = new FakeTimeProvider(StartUtc);
        var bus = new TestEventBus();
        var version = new CatalogVersion(1, 1);
        var finalVersion = new CatalogVersion(1005, 1005);

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<CatalogVersion>(version));

        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(_ => Task.FromResult<IReadOnlyList<TableMetadata>>([CreateTable($"t_{version.StructureVersion}")]));
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));

        await using var provider = new CatalogSnapshotProvider(repo, versionProvider, bus, timeProvider: time);

        // Emit a 1000-event burst where the last event carries the final version.
        for (var i = 6; i <= 1005; i++)
        {
            version = new CatalogVersion(i, i);
            await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        }

        await WaitHelper.WaitUntil(() => provider.CurrentSnapshot.Version == finalVersion || provider.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        if (provider.CurrentSnapshot.Version != finalVersion)
        {
            time.Advance(CatalogSnapshotProvider.MinimumRefreshInterval);
            await WaitHelper.WaitUntil(() => provider.CurrentSnapshot.Version == finalVersion, TimeSpan.FromSeconds(5));
        }

        provider.CurrentSnapshot.Version.ShouldBe(finalVersion);
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("t_1005");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task EventDuringRefresh_TriggersFollowUpRefreshAfterMinimumInterval()
    {
        var time = new FakeTimeProvider(StartUtc);
        var bus = new TestEventBus();
        var version = new CatalogVersion(2, 1);
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstRefreshToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadCallCount = 0;

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<CatalogVersion>(version));

        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(async _ =>
            {
                var call = Interlocked.Increment(ref loadCallCount);
                if (call == 1)
                {
                    firstRefreshStarted.TrySetResult();
                    await allowFirstRefreshToFinish.Task;
                }

                return (IReadOnlyList<TableMetadata>)[CreateTable($"orders_v{version.StructureVersion}")];
            });
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));

        await using var provider = new CatalogSnapshotProvider(repo, versionProvider, bus, timeProvider: time);

        // Event 1 begins refresh #1.
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());
        await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Event 2 arrives while refresh #1 is still in flight.
        version = new CatalogVersion(3, 1);
        await bus.PublishAsync(ICatalogVersionProvider.CatalogChangedEventChannel, new object());

        allowFirstRefreshToFinish.TrySetResult();
        await WaitHelper.WaitUntil(() => provider.CurrentSnapshot.Version == new CatalogVersion(2, 1), TimeSpan.FromSeconds(5));
        await WaitHelper.WaitUntil(() => provider.IsWaitingForCooldown, TimeSpan.FromSeconds(5));
        Volatile.Read(ref loadCallCount).ShouldBe(1);

        // Advance time by MinimumRefreshInterval to allow the follow-up refresh to converge to v(3,1).
        time.Advance(CatalogSnapshotProvider.MinimumRefreshInterval);
        await WaitHelper.WaitUntil(() => Volatile.Read(ref loadCallCount) == 2 && provider.CurrentSnapshot.Version == new CatalogVersion(3, 1), TimeSpan.FromSeconds(5));

        provider.CurrentSnapshot.Version.ShouldBe(new CatalogVersion(3, 1));
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders_v3");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ThreeConsecutiveRefreshFailures_MarksProviderDegraded_AndKeepsPreviousSnapshot()
    {
        var time = new FakeTimeProvider(StartUtc);
        var initialTable = CreateTable("orders");
        var version = new CatalogVersion(1, 1);
        var versionFailure = (Exception?)null;
        var tableFailure = (Exception?)null;
        var relationFailure = (Exception?)null;

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => versionFailure is null
                ? new ValueTask<CatalogVersion>(version)
                : ValueTask.FromException<CatalogVersion>(versionFailure));

        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(_ => tableFailure is null
                ? Task.FromResult<IReadOnlyList<TableMetadata>>([initialTable])
                : Task.FromException<IReadOnlyList<TableMetadata>>(tableFailure));
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => relationFailure is null
                ? Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>())
                : Task.FromException<IReadOnlyList<TableRelation>>(relationFailure));

        await using var provider = new CatalogSnapshotProvider(repo, versionProvider, timeProvider: time);
        await provider.RefreshAsync();
        provider.IsDegraded.ShouldBeFalse();
        provider.CurrentSnapshot.Version.ShouldBe(new CatalogVersion(1, 1));
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders");

        version = new CatalogVersion(2, 1);

        // Failure 1: version read fails.
        versionFailure = new InvalidOperationException("version outage");
        await provider.RefreshAsync();
        provider.IsDegraded.ShouldBeFalse();
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders");

        // Failure 2: tables load fails.
        versionFailure = null;
        tableFailure = new InvalidOperationException("tables outage");
        await provider.RefreshAsync();
        provider.IsDegraded.ShouldBeFalse();
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders");

        // Failure 3: relations load fails -> hits threshold (3) and marks degraded while keeping previous snapshot.
        tableFailure = null;
        relationFailure = new InvalidOperationException("relations outage");
        await provider.RefreshAsync();
        provider.IsDegraded.ShouldBeTrue();
        provider.CurrentSnapshot.Version.ShouldBe(new CatalogVersion(1, 1));
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders");

        // Recovery clears degraded status and publishes updated snapshot.
        relationFailure = null;
        await provider.RefreshAsync();
        provider.IsDegraded.ShouldBeFalse();
        provider.CurrentSnapshot.Version.ShouldBe(new CatalogVersion(2, 1));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task FullReloadBackstop_ForcesReloadAfterTenMinutesEvenWhenVersionUnchanged()
    {
        var time = new FakeTimeProvider(StartUtc);
        var version = new CatalogVersion(1, 1);
        var loadCount = 0;

        var versionProvider = Substitute.For<ICatalogVersionProvider>();
        versionProvider.GetCatalogVersionAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<CatalogVersion>(version));

        var repo = Substitute.For<IGovernanceRepository>();
        repo.StubAllTables(_ =>
            {
                var n = Interlocked.Increment(ref loadCount);
                return Task.FromResult<IReadOnlyList<TableMetadata>>([CreateTable($"orders_{n}")]);
            });
        repo.GetAllRelationsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>()));

        await using var provider = new CatalogSnapshotProvider(repo, versionProvider, timeProvider: time);
        await provider.RefreshAsync();
        Volatile.Read(ref loadCount).ShouldBe(1);

        // Just before 10 minutes: same version skips reload.
        time.Advance(CatalogSnapshotProvider.FullReloadBackstopInterval - TimeSpan.FromSeconds(1));
        await provider.RefreshAsync();
        Volatile.Read(ref loadCount).ShouldBe(1);

        // At 10 minutes: backstop forces a full reload even though dbVersion did not change.
        time.Advance(TimeSpan.FromSeconds(1));
        await provider.RefreshAsync();
        Volatile.Read(ref loadCount).ShouldBe(2);
        provider.CurrentSnapshot.Tables.Single().Identifier.TableName.ShouldBe("orders_2");
        provider.CurrentSnapshot.CapturedAt.ShouldBe(StartUtc + CatalogSnapshotProvider.FullReloadBackstopInterval);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Entries_AreImmutable()
    {
        var t1 = CreateTable("orders");
        var t2 = CreateTable("order_items");
        var rel = CreateRelation(t1, t2);

        var callerTables = new List<TableMetadata> { t1, t2 };
        var callerRelations = new List<TableRelation> { rel };

        var snapshot = CatalogSnapshot.Create(new CatalogVersion(2, 1), callerTables, callerRelations);

        // Mutating caller-owned lists must not alter the snapshot.
        callerTables.Add(CreateTable("extra"));
        callerRelations.Clear();

        snapshot.Tables.Count.ShouldBe(2);
        snapshot.Relations.Count.ShouldBe(1);

        var tablesList = (IList<TableMetadata>)snapshot.Tables;
        tablesList.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => tablesList[0] = t2);
        Should.Throw<NotSupportedException>(() => tablesList.Add(t1));

        var relationsList = (IList<TableRelation>)snapshot.Relations;
        relationsList.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => relationsList.Clear());

        var parentRels = (IList<TableRelation>)snapshot.GetParentRelations(t1.Table.Id);
        parentRels.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => parentRels[0] = rel);

        var childRels = (IList<TableRelation>)snapshot.GetChildRelations(t2.Table.Id);
        childRels.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => childRels.Clear());

        var emptyRels = (IList<TableRelation>)snapshot.GetParentRelations(Guid.NewGuid());
        emptyRels.IsReadOnly.ShouldBeTrue();
        Should.Throw<NotSupportedException>(() => emptyRels.Add(rel));

        var directCtor = new CatalogSnapshot(
            new CatalogVersion(3, 1),
            StartUtc,
            callerTables,
            new Dictionary<CatalogEntryKey, int> { [CatalogEntryKey.ForTable(t1.Identifier)] = 0 },
            new Dictionary<Guid, int> { [t1.Table.Id] = 0 },
            new Dictionary<TableIdentifier, int> { [t1.Identifier] = 0 },
            callerRelations,
            new Dictionary<Guid, IReadOnlyList<TableRelation>> { [t1.Table.Id] = new List<TableRelation> { rel } },
            new Dictionary<Guid, IReadOnlyList<TableRelation>>());

        ((IList<TableMetadata>)directCtor.Tables).IsReadOnly.ShouldBeTrue();
        ((IList<TableRelation>)directCtor.Relations).IsReadOnly.ShouldBeTrue();
        ((IList<TableRelation>)directCtor.GetParentRelations(t1.Table.Id)).IsReadOnly.ShouldBeTrue();
    }

    private sealed class TestEventBus : IEventBus
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
}

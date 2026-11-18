namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class CatalogSnapshotDeltaReloadTests
{
    private static readonly DateTimeOffset StartUtc = new(2026, 11, 15, 6, 0, 0, TimeSpan.Zero);

    private static TableMetadata CreateTable(
        string name,
        bool isActive = true,
        CatalogView visibility = CatalogView.Unmarked,
        Guid? id = null) => new()
    {
        Identifier = new TableIdentifier("sales", "public", name),
        Table = new Table
        {
            Id = id ?? Guid.NewGuid(),
            SourceName = "sales",
            SchemaName = "public",
            TableName = name,
            DisplayName = name,
            IsActive = isActive
        },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
        CatalogView = visibility
    };

    [Trait("Category", "Security")]
    [Fact]
    public async Task SingleTableDeactivated_RemovedOnDeltaReload()
    {
        var t1 = CreateTable("orders");
        var t2 = CreateTable("customers");
        var t3 = CreateTable("invoices");

        var repo = new InMemoryDeltaGovernanceRepo(new[] { t1, t2, t3 });
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        var initial = await provider.GetSnapshotAsync();
        initial.TableCount.ShouldBe(3);
        repo.GetAllTablesCallCount.ShouldBe(1);

        // Deactivate t2 and bump version to (1, 2) -> combined = 3, with change_version = 3.
        var t2Deactivated = t2 with { Table = t2.Table with { IsActive = false } };
        repo.SetTable(t2Deactivated, changeVersion: 3);
        versionProvider.Current = new CatalogVersion(1, 2);

        await provider.RefreshAsync();
        var updated = provider.CurrentSnapshot;

        updated.TableCount.ShouldBe(2);
        updated.FindTable(t2.Identifier).ShouldBeNull();
        updated.FindTable(t1.Identifier).ShouldNotBeNull();
        updated.FindTable(t3.Identifier).ShouldNotBeNull();
        repo.GetAllTablesCallCount.ShouldBe(1);
        repo.GetTablesChangedSinceCallCount.ShouldBe(1);
        repo.GetTablesBatchCallCount.ShouldBe(1);
    }


    [Trait("Category", "Security")]
    [Fact]
    public async Task ConcurrentWriteDuringDeltaRead_NotSkippedOnNextReload()
    {
        var t1 = CreateTable("orders");
        var t2 = CreateTable("customers");

        var repo = new InMemoryDeltaGovernanceRepo(new[] { t1, t2 });
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        await provider.GetSnapshotAsync();

        // Commit version (1, 2) for t1; during GetTablesChangedSinceAsync, a concurrent writer commits (1, 3) for t2.
        var t1V2 = t1 with { Table = t1.Table with { DisplayName = "orders_v2" } };
        var t2V3 = t2 with { Table = t2.Table with { DisplayName = "customers_v3" } };
        repo.SetTable(t1V2, changeVersion: 3);
        versionProvider.Current = new CatalogVersion(1, 2);

        var concurrentFired = false;
        repo.OnGetTablesChangedSince = () =>
        {
            if (concurrentFired)
            {
                return;
            }

            concurrentFired = true;
            repo.SetTable(t2V3, changeVersion: 4);
            versionProvider.Current = new CatalogVersion(1, 3);
        };

        await provider.RefreshAsync();
        provider.CurrentSnapshot.FindTable(t1.Identifier)!.Table.DisplayName.ShouldBe("orders_v2");

        // Next refresh must still see version (1, 3) > _lastLoadedVersion (3) and load t2V3.
        await provider.RefreshAsync();
        provider.CurrentSnapshot.FindTable(t2.Identifier)!.Table.DisplayName.ShouldBe("customers_v3");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task DeltaExceeding20Percent_FallsBackToFullReload()
    {
        var tables = Enumerable.Range(1, 10).Select(i => CreateTable($"t{i}")).ToList();
        var repo = new InMemoryDeltaGovernanceRepo(tables);
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        await provider.GetSnapshotAsync();
        repo.GetAllTablesCallCount.ShouldBe(1);

        // Change 3 of 10 tables (30% >= 20%).
        for (var i = 0; i < 3; i++)
        {
            var updated = tables[i] with { Table = tables[i].Table with { DisplayName = $"updated_{i}" } };
            repo.SetTable(updated, changeVersion: 3);
        }

        versionProvider.Current = new CatalogVersion(1, 2);
        await provider.RefreshAsync();

        repo.GetTablesChangedSinceCallCount.ShouldBe(1);
        repo.GetAllTablesCallCount.ShouldBe(2);
        repo.GetTablesBatchCallCount.ShouldBe(0);
        provider.CurrentSnapshot.FindTable(tables[0].Identifier)!.Table.DisplayName.ShouldBe("updated_0");
    }

    [Fact]
    public async Task VersionGapAndDeltaReadFailure_FallBackToFullReload()
    {
        var tables = Enumerable.Range(1, 5).Select(i => CreateTable($"t{i}")).ToList();
        var repo = new InMemoryDeltaGovernanceRepo(tables);
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        await provider.GetSnapshotAsync();
        repo.GetAllTablesCallCount.ShouldBe(1);

        // Delta read failure falls back cleanly to full reload.
        repo.ThrowOnGetTablesChangedSince = true;
        var t0Updated = tables[0] with { Table = tables[0].Table with { DisplayName = "recovered" } };
        repo.SetTable(t0Updated, changeVersion: 3);
        versionProvider.Current = new CatalogVersion(1, 2);
        await provider.RefreshAsync();

        repo.GetAllTablesCallCount.ShouldBe(2);
        provider.CurrentSnapshot.FindTable(tables[0].Identifier)!.Table.DisplayName.ShouldBe("recovered");

        // Large version gap beyond MaxDeltaVersionSpan skips delta query and runs full reload.
        repo.ThrowOnGetTablesChangedSince = false;
        var prevDeltaCalls = repo.GetTablesChangedSinceCallCount;
        var t1Updated = tables[1] with { Table = tables[1].Table with { DisplayName = "after_gap" } };
        repo.SetTable(t1Updated, changeVersion: 1000);
        versionProvider.Current = new CatalogVersion(1, 999);
        await provider.RefreshAsync();

        repo.GetTablesChangedSinceCallCount.ShouldBe(prevDeltaCalls);
        repo.GetAllTablesCallCount.ShouldBe(3);
        provider.CurrentSnapshot.FindTable(tables[1].Identifier)!.Table.DisplayName.ShouldBe("after_gap");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task VisibilityChanged_ReflectedOnDeltaReload()
    {
        var t1 = CreateTable("orders", visibility: CatalogView.Unmarked);
        var t2 = CreateTable("customers", visibility: CatalogView.Granted);

        var repo = new InMemoryDeltaGovernanceRepo(new[] { t1, t2 });
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        await provider.GetSnapshotAsync();
        provider.CurrentSnapshot.FindTable(t1.Identifier)!.CatalogView.ShouldBe(CatalogView.Unmarked);

        var t1Full = t1 with { CatalogView = CatalogView.Granted };
        repo.SetTable(t1Full, changeVersion: 3);
        versionProvider.Current = new CatalogVersion(1, 2);

        await provider.RefreshAsync();
        provider.CurrentSnapshot.FindTable(t1.Identifier)!.CatalogView.ShouldBe(CatalogView.Granted);
        provider.CurrentSnapshot.FindTable(t2.Identifier)!.CatalogView.ShouldBe(CatalogView.Granted);
        repo.GetAllTablesCallCount.ShouldBe(1);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task InFlightReader_SeesConsistentSnapshotDuringDeltaSwap()
    {
        var t1 = CreateTable("orders", visibility: CatalogView.Unmarked);
        var t2 = CreateTable("customers", visibility: CatalogView.Granted);

        var repo = new InMemoryDeltaGovernanceRepo(new[] { t1, t2 });
        var versionProvider = new FakeVersionProvider(new CatalogVersion(1, 1));
        var time = new FakeTimeProvider(StartUtc);

        await using var provider = new CatalogSnapshotProvider(repo.Proxy, versionProvider, null, null, time);
        var readerSnapshot = await provider.GetSnapshotAsync();
        readerSnapshot.TableCount.ShouldBe(2);
        readerSnapshot.FindTable(t1.Identifier)!.CatalogView.ShouldBe(CatalogView.Unmarked);

        var t1Updated = t1 with { CatalogView = CatalogView.Granted };
        var t2Removed = t2 with { Table = t2.Table with { IsActive = false } };
        repo.SetTable(t1Updated, changeVersion: 3);
        versionProvider.Current = new CatalogVersion(1, 2);

        await provider.RefreshAsync();

        var swapped = provider.CurrentSnapshot;
        swapped.ShouldNotBeSameAs(readerSnapshot);
        swapped.FindTable(t1.Identifier)!.CatalogView.ShouldBe(CatalogView.Granted);

        // Previously captured snapshot remains completely immutable and unchanged.
        readerSnapshot.TableCount.ShouldBe(2);
        readerSnapshot.FindTable(t1.Identifier)!.CatalogView.ShouldBe(CatalogView.Unmarked);
        readerSnapshot.FindTable(t2.Identifier).ShouldNotBeNull();

        // Also remove t2 cleanly via WithDelta directly and verify earlier snapshot is untouched.
        var third = swapped.WithDelta(new CatalogVersion(1, 3), [t2Removed.Identifier], Array.Empty<TableMetadata>());
        third.TableCount.ShouldBe(1);
        swapped.TableCount.ShouldBe(2);
        readerSnapshot.TableCount.ShouldBe(2);
    }


    private sealed class FakeVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);

        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Current.Formatted);
    }

    internal sealed class InMemoryDeltaGovernanceRepo
    {
        private readonly Dictionary<TableIdentifier, TableMetadata> _tables = new();
        private readonly Dictionary<TableIdentifier, long> _changeVersions = new();

        public InMemoryDeltaGovernanceRepo(IEnumerable<TableMetadata> seed)
        {
            foreach (var table in seed)
            {
                _tables[table.Identifier] = table;
                _changeVersions[table.Identifier] = 2;
            }

            Proxy = GovernanceProxy.Create(this);
        }

        public IGovernanceRepository Proxy { get; }
        public int GetAllTablesCallCount { get; private set; }
        public int GetTablesChangedSinceCallCount { get; private set; }
        public int GetTablesBatchCallCount { get; private set; }
        public bool ThrowOnGetTablesChangedSince { get; set; }
        public Action? OnGetTablesChangedSince { get; set; }

        public void SetTable(TableMetadata meta, long changeVersion)
        {
            _tables[meta.Identifier] = meta;
            _changeVersions[meta.Identifier] = changeVersion;
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllTablesCallCount++;
            IReadOnlyList<TableMetadata> active = _tables.Values.Where(t => t.Table.IsActive).ToList();
            return Task.FromResult(active);
        }

        /// <summary>A full scan (one page holding every active table); counted like a full load.</summary>
        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            GetAllTablesCallCount++;
            IReadOnlyList<TableMetadata> active = _tables.Values.Where(t => t.Table.IsActive).ToList();
            return Task.FromResult(new CatalogTablePage(active, null));
        }

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableRelation>>(Array.Empty<TableRelation>());

        public Task<IReadOnlyList<TableIdentifier>> GetTablesChangedSinceAsync(long sinceVersion, CancellationToken ct = default)
        {
            GetTablesChangedSinceCallCount++;
            if (ThrowOnGetTablesChangedSince)
            {
                throw new InvalidOperationException("Simulated delta read failure.");
            }

            IReadOnlyList<TableIdentifier> changed = _changeVersions
                .Where(kv => kv.Value > sinceVersion)
                .Select(kv => kv.Key)
                .ToList();

            OnGetTablesChangedSince?.Invoke();
            return Task.FromResult(changed);
        }

        public Task<IReadOnlyList<TableMetadata>> GetTablesAsync(IReadOnlyCollection<TableIdentifier> identifiers, CancellationToken ct = default)
        {
            GetTablesBatchCallCount++;
            var list = new List<TableMetadata>();
            foreach (var id in identifiers)
            {
                if (_tables.TryGetValue(id, out var meta) && meta.Table.IsActive)
                {
                    list.Add(meta);
                }
            }

            return Task.FromResult<IReadOnlyList<TableMetadata>>(list);
        }
    }

    public class GovernanceProxy : DispatchProxy
    {
        private InMemoryDeltaGovernanceRepo? _target;

        internal static IGovernanceRepository Create(InMemoryDeltaGovernanceRepo target)
        {
            var proxy = Create<IGovernanceRepository, GovernanceProxy>();
            ((GovernanceProxy)(object)proxy)._target = target;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            var ct = args is { Length: > 0 } && args[^1] is CancellationToken token ? token : CancellationToken.None;
            return targetMethod.Name switch
            {
                nameof(ITableMetadataRepository.GetAllTablesAsync) => _target!.GetAllTablesAsync(ct),
                nameof(ITableMetadataRepository.QueryTablesAsync) => _target!.QueryTablesAsync((CatalogTableQuery)args![0]!, ct),
                nameof(ITableRelationRepository.GetAllRelationsAsync) => _target!.GetAllRelationsAsync(ct),
                nameof(ITableMetadataRepository.GetTablesChangedSinceAsync) => _target!.GetTablesChangedSinceAsync((long)args![0]!, ct),
                nameof(ITableMetadataRepository.GetTablesAsync) => _target!.GetTablesAsync((IReadOnlyCollection<TableIdentifier>)args![0]!, ct),
                _ => throw new NotImplementedException($"Method {targetMethod.Name} is not implemented on InMemoryDeltaGovernanceRepo.")
            };
        }
    }

}

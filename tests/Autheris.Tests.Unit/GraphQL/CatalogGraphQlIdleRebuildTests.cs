using Autheris.Api.Extensions;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Common;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>Issue #786: idle ticks, coalesced rebuilds, quiet period, build logs and cheaper relation reads.</summary>
[Collection("CatalogRelation")]
public sealed class CatalogGraphQlIdleRebuildTests
{
    public sealed class Query
    {
        public string Ping() => "pong";
    }

    private static TableMetadata MakeTable(string name, bool hidden = false, string domain = "sales", params string[] columns)
    {
        var cols = new List<TableColumn> { new() { ColumnName = "id", DataType = "integer" } };
        cols.AddRange(columns.Select(c => new TableColumn { ColumnName = c, DataType = "text" }));
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", name),
            Table = new Table
            {
                SourceName = "pg_main",
                SourceType = "PostgreSql",
                SchemaName = "public",
                TableName = name,
                IsActive = true,
                DiscoveryHidden = hidden
            },
            Columns = cols,
            PrimaryKeyColumns = ["id"]
        };
    }

    internal static List<TableMetadata> MakeCatalog(int tables, int columns) =>
        Enumerable.Range(0, tables)
            .Select(i => MakeTable($"t{i}", columns: Enumerable.Range(0, columns).Select(c => $"c{c}").ToArray()))
            .ToList();

    private static IRequestExecutorProvider Provider(
        CatalogGraphQlTypeModule module,
        IRequestExecutorWarmupTask? warmup = null,
        CatalogSchemaBuildDiagnosticListener? listener = null)
    {
        var builder = new ServiceCollection().AddGraphQLServer().AddQueryType<Query>().AddTypeModule(_ => module);
        if (warmup is not null)
        {
            builder.AddWarmupTask(_ => warmup, skipIf: false);
        }

        if (listener is not null)
        {
            builder.AddDiagnosticEventListener(_ => listener);
        }

        return builder.Services.BuildServiceProvider().GetRequiredService<IRequestExecutorProvider>();
    }

    private static async Task<IRequestExecutor> WaitForAsync(IRequestExecutorProvider provider, Func<IRequestExecutor, bool> predicate)
    {
        var executor = await provider.GetExecutorAsync();
        await WaitHelper.WaitUntil(
            async () =>
            {
                executor = await provider.GetExecutorAsync();
                return predicate(executor);
            },
            TimeSpan.FromSeconds(20),
            pollInterval: TimeSpan.FromMilliseconds(25));
        return executor;
    }

    private static int Decisions(CapturingLogger<CatalogGraphQlTypeModule> log) =>
        log.Entries.Count(e => e.Message.Contains("rebuilding the GraphQL schema", StringComparison.Ordinal) ||
                               e.Message.Contains("coalesced", StringComparison.Ordinal));

    [Fact]
    public async Task DiComposedModule_IdleTicks_DoNotReadCatalog()
    {
        var meta = new Fakes.MetadataRepository(MakeCatalog(50, 3));
        var rel = new Fakes.RelationRepository { Authoritative = true };
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITableMetadataRepository>(meta);
        services.AddSingleton<ITableRelationRepository>(rel);
        services.AddSingleton<ICatalogVersionProvider>(version);
        services.AddSingleton(TimeProvider.System);
        using var sp = services.BuildServiceProvider();

        using var module = GatewayServiceCollectionExtensions.CreateCatalogGraphQlTypeModule(sp, new GatewayOptions());
        await Provider(module).GetExecutorAsync();
        var tables = meta.GetAllTablesCalls;
        var relationReads = rel.GetForTableCalls;

        for (var i = 0; i < 5; i++)
        {
            (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        }

        meta.GetAllTablesCalls.ShouldBe(tables);
        rel.GetForTableCalls.ShouldBe(relationReads);
        relationReads.ShouldBe(0, "an authoritative empty bulk read must not fall back to one read per table");
    }

    [Fact]
    public async Task VersionUnchanged_BackstopElapsed_RunsOneFullCheck()
    {
        var time = new FakeTimeProvider();
        var meta = new Fakes.MetadataRepository(MakeCatalog(3, 1));
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), versionProvider: version, timeProvider: time);
        await Provider(module).GetExecutorAsync();
        var calls = meta.GetAllTablesCalls;

        time.Advance(TimeSpan.FromMinutes(9));
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesCalls.ShouldBe(calls);

        time.Advance(TimeSpan.FromMinutes(2));
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesCalls.ShouldBe(calls + 1);

        // the backstop check restarts the interval
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesCalls.ShouldBe(calls + 1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenTableWithoutVersionBump_RemovedAfterBackstop()
    {
        var time = new FakeTimeProvider();
        var meta = new Fakes.MetadataRepository([MakeTable("orders"), MakeTable("salaries")]);
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), versionProvider: version, timeProvider: time);
        var provider = Provider(module);
        (await provider.GetExecutorAsync()).Schema.Types.ContainsName("sales_public_salaries").ShouldBeTrue();

        meta.SetTables([MakeTable("orders"), MakeTable("salaries", hidden: true)]);
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();

        time.Advance(CatalogGraphQlTypeModule.FullCheckBackstopInterval + TimeSpan.FromSeconds(1));
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        var rebuilt = await WaitForAsync(provider, e => !e.Schema.Types.ContainsName("sales_public_salaries"));
        rebuilt.Schema.Types.ContainsName("sales_public_orders").ShouldBeTrue();
    }

    [Fact]
    public async Task ChangesDuringRebuild_AtMostOneTrailingRebuild()
    {
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository([MakeTable("t0")]);
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        var warmup = new BlockingWarmup();
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log, versionProvider: version);
        var provider = Provider(module, warmup);
        await provider.GetExecutorAsync();
        warmup.Block();

        await ChangeAsync(module, meta, version, 1, 2);
        await warmup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        for (var n = 3; n <= 7; n++)
        {
            await ChangeAsync(module, meta, version, n - 1, n);
        }

        // one quiet check: the burst is over
        await module.CheckForCatalogChangesAsync();
        warmup.Release();

        var newest = await WaitForAsync(provider, e => e.Schema.Types.ContainsName("sales_public_t6"));
        newest.Schema.Types.ContainsName("sales_public_t0").ShouldBeTrue();

        await Task.Delay(300);
        log.Entries.Count(e => e.Message.Contains("schema build started", StringComparison.Ordinal)).ShouldBe(3, "startup plus at most two rebuilds");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HideTableDuringRebuild_FinalSchemaOmitsTable()
    {
        var meta = new Fakes.MetadataRepository([MakeTable("orders"), MakeTable("salaries")]);
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        var warmup = new BlockingWarmup();
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), versionProvider: version);
        var provider = Provider(module, warmup);
        await provider.GetExecutorAsync();
        warmup.Block();

        meta.SetTables([MakeTable("orders"), MakeTable("salaries"), MakeTable("extra")]);
        version.Current = new CatalogVersion(2, 1);
        await module.CheckForCatalogChangesAsync();
        await warmup.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        meta.SetTables([MakeTable("orders"), MakeTable("salaries", hidden: true), MakeTable("extra")]);
        version.Current = new CatalogVersion(2, 2);
        await module.CheckForCatalogChangesAsync(); // deferred (burst)
        await module.CheckForCatalogChangesAsync(); // quiet: raises the trailing rebuild
        warmup.Release();

        var final = await WaitForAsync(provider, e => !e.Schema.Types.ContainsName("sales_public_salaries"));
        final.Schema.Types.ContainsName("sales_public_extra").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RebuildFailure_ResetsLatch_NextChangeRebuilds()
    {
        var meta = new Fakes.MetadataRepository([MakeTable("orders")]) { Fail = true };
        var version = new Fakes.VersionProvider(new CatalogVersion(1, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), versionProvider: version);
        var provider = Provider(module);

        // startup build fails closed: no catalog tables are served
        (await provider.GetExecutorAsync()).Schema.Types.ContainsName("sales_public_orders").ShouldBeFalse();

        meta.Fail = false;
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        var rebuilt = await WaitForAsync(provider, e => e.Schema.Types.ContainsName("sales_public_orders"));
        rebuilt.ShouldNotBeNull();
    }

    [Fact]
    public async Task NoSubscriber_ChangeIsNotLatched()
    {
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository([MakeTable("t0")]);
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log);

        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        Decisions(log).ShouldBe(0);

        var raised = 0;
        module.TypesChanged += (_, _) => raised++;
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        raised.ShouldBe(1, "the change seen without a subscriber must not block the first real request");

        meta.SetTables([MakeTable("t0"), MakeTable("t1")]);
        await module.CheckForCatalogChangesAsync();
        raised.ShouldBe(1, "a request is already outstanding, the newer model is only stored");
    }

    [Fact]
    public async Task SingleChange_RebuildsWithoutAddedDelay()
    {
        var time = new FakeTimeProvider();
        var meta = new Fakes.MetadataRepository([MakeTable("t0")]);
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), timeProvider: time);
        var raised = 0;
        module.TypesChanged += (_, _) => raised++;

        await module.CheckForCatalogChangesAsync();
        raised.ShouldBe(1);
    }

    [Fact]
    public async Task ChangeEveryTick_RebuildsAtMostEveryFiveMinutes()
    {
        var time = new FakeTimeProvider();
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository([MakeTable("t0")]);
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log, timeProvider: time);
        module.TypesChanged += (_, _) => { };

        for (var minute = 1; minute <= 12; minute++)
        {
            meta.SetTables(Enumerable.Range(0, minute + 1).Select(i => MakeTable($"t{i}")).ToList());
            await module.CheckForCatalogChangesAsync();
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Decisions(log).ShouldBe(2, "leading edge plus one deferral cut-off, not one per tick");
    }

    [Fact]
    public async Task BurstEnds_TrailingRebuildOnFirstQuietTick()
    {
        var time = new FakeTimeProvider();
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository([MakeTable("t0")]);
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log, timeProvider: time);
        module.TypesChanged += (_, _) => { };

        for (var i = 1; i <= 3; i++)
        {
            meta.SetTables(Enumerable.Range(0, i + 1).Select(n => MakeTable($"t{n}")).ToList());
            await module.CheckForCatalogChangesAsync();
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Decisions(log).ShouldBe(1);
        await module.CheckForCatalogChangesAsync(); // same tables as the last change: the burst is over
        Decisions(log).ShouldBe(2);
    }

    [Fact]
    public async Task TimerTicks_WhileCheckRuns_DoNotQueue()
    {
        var time = new FakeTimeProvider();
        var meta = new Fakes.MetadataRepository(MakeCatalog(2, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), refreshInterval: TimeSpan.FromMinutes(1), timeProvider: time);
        await Provider(module).GetExecutorAsync();
        var baseline = meta.GetAllTablesCalls;

        meta.BlockNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitHelper.WaitUntil(() => meta.GetAllTablesCalls == baseline + 1, TimeSpan.FromSeconds(10));
        for (var i = 0; i < 5; i++)
        {
            time.Advance(TimeSpan.FromMinutes(1));
        }

        await Task.Delay(200);
        meta.GetAllTablesCalls.ShouldBe(baseline + 1, "ticks while a check runs are skipped, not queued");
        meta.BlockNext.SetResult();
    }

    [Fact]
    public async Task Rebuild_LogsStartedAndFinishedWithDuration()
    {
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository(MakeCatalog(3, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log);
        await Provider(module, listener: new CatalogSchemaBuildDiagnosticListener(module)).GetExecutorAsync();

        log.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message.Contains("schema build started: trigger=startup", StringComparison.Ordinal));
        var finished = log.Entries.Single(e => e.Message.Contains("schema build finished", StringComparison.Ordinal));
        finished.Message.ShouldContain("tables=3");
        finished.Message.ShouldContain("omitted=0");
        finished.Message.ShouldContain("elapsedMs=");
        finished.Message.ShouldContain("phase=rebuild");
    }

    [Fact]
    public async Task UnchangedCheck_LogsAtDebugOnly()
    {
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository(MakeCatalog(2, 1));
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log);
        await Provider(module).GetExecutorAsync();
        var before = log.Entries.Count;

        await module.CheckForCatalogChangesAsync();

        var added = log.Entries.Skip(before).ToList();
        added.ShouldContain(e => e.Level == LogLevel.Debug && e.Message.Contains("catalog check finished", StringComparison.Ordinal));
        added.ShouldNotContain(e => e.Level >= LogLevel.Information);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task BuildLogs_CarryNoCatalogNames()
    {
        var log = new CapturingLogger<CatalogGraphQlTypeModule>();
        var meta = new Fakes.MetadataRepository([MakeTable("secret_salaries", columns: "ssn_value")]);
        var time = new FakeTimeProvider();
        using var module = new CatalogGraphQlTypeModule(meta, new Fakes.RelationRepository(), log, timeProvider: time);
        await Provider(module, listener: new CatalogSchemaBuildDiagnosticListener(module)).GetExecutorAsync();
        meta.SetTables([MakeTable("secret_salaries", columns: "ssn_value"), MakeTable("secret_bonus")]);
        await module.CheckForCatalogChangesAsync();
        await Task.Delay(300);

        foreach (var entry in log.Entries)
        {
            entry.Message.ShouldNotContain("secret_", Case.Insensitive);
            entry.Message.ShouldNotContain("ssn_value", Case.Insensitive);
        }
    }

    [Fact]
    public async Task AuthoritativeEmptyBulk_NoPerTableReads()
    {
        var meta = new Fakes.MetadataRepository(MakeCatalog(20, 2));
        var rel = new Fakes.RelationRepository { Authoritative = true };
        var model = await CatalogSchemaModel.BuildAsync(meta, rel);
        model.Tables.Count.ShouldBe(20);
        rel.GetForTableCalls.ShouldBe(0);

        var legacy = new Fakes.RelationRepository();
        await CatalogSchemaModel.BuildAsync(meta, legacy);
        legacy.GetForTableCalls.ShouldBe(20, "test doubles keep the per-table fallback");
    }

    [Fact]
    public async Task ReverseRelations_IndexedLookup_FieldNamesUnchanged()
    {
        // Two parents named "orders" in different domains plus one named "a"; the child has a column that collides with a reverse name.
        var ordersSales = MakeTable("orders", domain: "sales");
        var ordersHr = MakeTable("orders", domain: "hr");
        var a = MakeTable("a", domain: "x");
        var child = MakeTable("child", false, "x", "orders_by_link_rel");
        var meta = new Fakes.MetadataRepository([ordersSales, ordersHr, a, child]);

        TableRelation Rel(TableMetadata parent) => new()
        {
            RelationName = "link",
            ParentTableIdentifier = parent.Identifier,
            ChildTableIdentifier = child.Identifier,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["id"]
        };

        var rel = new Fakes.RelationRepository { All = [Rel(ordersSales), Rel(a), Rel(ordersHr)], Authoritative = true };
        var model = await CatalogSchemaModel.BuildAsync(meta, rel);

        var childType = model.Tables.Single(t => t.Identifier.Equals(child.Identifier));
        // order = table order, then relation order; collisions get "_rel" and numeric suffixes.
        childType.Relations.Select(r => r.FieldName).ShouldBe(["orders_by_link", "orders_by_link_rel_2", "a_by_link"]);
        childType.Relations.Select(r => r.TargetTableIdentifier).ShouldBe([ordersHr.Identifier, ordersSales.Identifier, a.Identifier]);
    }

    private static async Task ChangeAsync(CatalogGraphQlTypeModule module, Fakes.MetadataRepository meta, Fakes.VersionProvider version, int fromCount, int toCount)
    {
        _ = fromCount;
        meta.SetTables(Enumerable.Range(0, toCount).Select(i => MakeTable($"t{i}")).ToList());
        version.Current = new CatalogVersion(toCount, 1);
        await module.CheckForCatalogChangesAsync();
    }

    private sealed class BlockingWarmup : IRequestExecutorWarmupTask
    {
        private volatile TaskCompletionSource? _release;

        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ApplyOnlyOnStartup => false;

        public void Block()
        {
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Release() => _release?.TrySetResult();

        public async Task WarmupAsync(IRequestExecutor executor, CancellationToken cancellationToken)
        {
            var gate = _release;
            if (gate is null || gate.Task.IsCompleted)
            {
                return;
            }

            Entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
        }
    }

    private static class Fakes
    {
        public sealed class MetadataRepository(IReadOnlyList<TableMetadata> initial) : ITableMetadataRepository
        {
            private IReadOnlyList<TableMetadata> _tables = initial;
            private int _calls;

            public bool Fail { get; set; }

            public TaskCompletionSource? BlockNext { get; set; }

            public int GetAllTablesCalls => Volatile.Read(ref _calls);

            public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;

            public async Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            {
                Interlocked.Increment(ref _calls);
                if (Fail)
                {
                    throw new InvalidOperationException("catalog unreachable");
                }

                var gate = BlockNext;
                if (gate is not null)
                {
                    await gate.Task.ConfigureAwait(false);
                }

                return _tables;
            }

            public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
                => Task.FromResult(_tables.FirstOrDefault(t => t.Identifier.Equals(table)));

            public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
                => Task.FromResult(metadata);

            public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
                TableIdentifier table,
                IReadOnlyList<string> primaryKey,
                IReadOnlyList<TableIndexDefinition> indexes,
                IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
                CancellationToken ct = default)
                => throw new NotSupportedException();
        }

        public sealed class RelationRepository : ITableRelationRepository
        {
            private int _forTable;

            public bool Authoritative { get; init; }

            public IReadOnlyList<TableRelation> All { get; init; } = [];

            public int GetForTableCalls => Volatile.Read(ref _forTable);

            public bool BulkRelationsAuthoritative => Authoritative;

            public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) => Task.FromResult(All);

            public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _forTable);
                return Task.FromResult<IReadOnlyList<TableRelation>>([]);
            }

            public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
                => throw new NotSupportedException();

            public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
                => throw new NotSupportedException();
        }

        public sealed class VersionProvider(CatalogVersion initial) : ICatalogVersionProvider
        {
            public CatalogVersion Current { get; set; } = initial;

            public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);

            public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(Current.Formatted);
        }
    }
}

using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.GraphQL.Catalog;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.DataSources;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

[Collection("CatalogRelation")]
public sealed class CatalogGraphQlTypeModuleScaleTests
{
    public sealed class Query
    {
        public string Ping() => "pong";
    }

    private static IRequestExecutorProvider CreateExecutorProvider(CatalogGraphQlTypeModule module) =>
        new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeModule(_ => module)
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IRequestExecutorProvider>();

    private static TableMetadata MakeTable(
        string name,
        bool discoveryHidden = false,
        string sourceName = "pg_main",
        params string[] extraColumns)
    {
        var columns = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "integer" }
        };

        foreach (var col in extraColumns)
        {
            columns.Add(new TableColumn { ColumnName = col, DataType = "text" });
        }

        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", name),
            Table = new Table
            {
                SourceName = sourceName,
                SourceType = "PostgreSql",
                SchemaName = "public",
                TableName = name,
                IsActive = true,
                DiscoveryHidden = discoveryHidden
            },
            Columns = columns,
            PrimaryKeyColumns = ["id"]
        };
    }

    [Fact]
    public async Task StructureChange_BuildsModelOnce()
    {
        var meta = new FakeMetadataRepository([MakeTable("orders")]);
        var rel = new FakeRelationRepository();
        var versionProvider = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));
        var epochService = new FakeEpochValidationService();

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            versionProvider: versionProvider,
            epochService: epochService);

        var resolver = CreateExecutorProvider(module);
        var initial = await resolver.GetExecutorAsync();
        initial.Schema.Types.ContainsName("sales_public_orders").ShouldBeTrue();
        meta.GetAllTablesCallCount.ShouldBe(1);

        // No change at same version -> early exit without reading metadata repository.
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesCallCount.ShouldBe(1);

        // Bump structure version and add a new table -> CheckForCatalogChangesAsync builds once,
        // CreateTypesAsync reuses the prebuilt model without a second BuildAsync call.
        meta.SetTables([MakeTable("orders"), MakeTable("invoices")]);
        versionProvider.Current = new CatalogVersion(2, 1);

        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        meta.GetAllTablesCallCount.ShouldBe(2);

        var rebuilt = await WaitForExecutorConditionAsync(
            resolver,
            e => e.Schema.Types.ContainsName("sales_public_invoices"));

        rebuilt.Schema.Types.ContainsName("sales_public_orders").ShouldBeTrue();
        rebuilt.Schema.Types.ContainsName("sales_public_invoices").ShouldBeTrue();
        meta.GetAllTablesCallCount.ShouldBe(2);

        // Subsequent poll at the new built version does not rebuild again.
        (await module.CheckForCatalogChangesAsync()).ShouldBeFalse();
        meta.GetAllTablesCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task UnchangedTables_NotReHashed()
    {
        var orders = MakeTable("orders");
        var customers = MakeTable("customers");
        var products = MakeTable("products");

        var meta = new FakeMetadataRepository([orders, customers, products]);
        var rel = new FakeRelationRepository();
        var versionProvider = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));
        var epochService = new FakeEpochValidationService();
        epochService.SetEpoch(orders.Identifier, 10);
        epochService.SetEpoch(customers.Identifier, 20);
        epochService.SetEpoch(products.Identifier, 30);

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            versionProvider: versionProvider,
            epochService: epochService);

        var resolver = CreateExecutorProvider(module);
        await resolver.GetExecutorAsync();

        module.FingerprintCache.LastBuildRehashCount.ShouldBe(3);
        module.FingerprintCache.TotalRehashCount.ShouldBe(3);

        // Change only customers (add column + bump epoch); orders & products remain unchanged.
        meta.SetTables([orders, MakeTable("customers", extraColumns: "email"), products]);
        epochService.SetEpoch(customers.Identifier, 21);
        versionProvider.Current = new CatalogVersion(2, 1);

        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        module.FingerprintCache.LastBuildRehashCount.ShouldBe(1);
        module.FingerprintCache.TotalRehashCount.ShouldBe(4);

        var rebuilt = await WaitForExecutorConditionAsync(
            resolver,
            e => e.Schema.Types.TryGetType<HotChocolate.Types.ObjectType>("sales_public_customers", out var obj)
                 && obj.Fields.ContainsField("email"));

        rebuilt.Schema.Types.TryGetType<HotChocolate.Types.ObjectType>("sales_public_customers", out var customerType)
            .ShouldBeTrue();
        customerType!.Fields.ContainsField("email").ShouldBeTrue();
        module.FingerprintCache.TotalRehashCount.ShouldBe(4);

        // Even if a column changes without an epoch bump, structural mismatch still re-hashes only the changed table.
        meta.SetTables([orders, MakeTable("customers", extraColumns: "email"), MakeTable("products", extraColumns: "sku")]);
        versionProvider.Current = new CatalogVersion(3, 1);

        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();
        module.FingerprintCache.LastBuildRehashCount.ShouldBe(1);
        module.FingerprintCache.TotalRehashCount.ShouldBe(5);
    }

    [Fact]
    public async Task StartupBuild_LogsProgress()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var logger = new RecordingLogger<CatalogGraphQlTypeModule>();
        var meta = new FakeMetadataRepository([MakeTable("orders"), MakeTable("customers"), MakeTable("invoices")]);
        var rel = new FakeRelationRepository
        {
            OnGetRelationsForTable = callIndex =>
            {
                if (callIndex == 2)
                {
                    fakeTime.Advance(TimeSpan.FromMilliseconds(5200));
                }
            }
        };

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            logger: logger,
            timeProvider: fakeTime);

        await new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeModule(_ => module)
            .BuildRequestExecutorAsync();

        var progressLog = logger.Entries.FirstOrDefault(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("GraphQL catalog schema build progress:", StringComparison.Ordinal));

        progressLog.ShouldNotBeNull();
        progressLog.Message.ShouldContain("tablesProcessed=");
        progressLog.Message.ShouldContain("elapsedMs=5200");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenTableAfterChange_RootFieldRemoved()
    {
        var meta = new FakeMetadataRepository([MakeTable("orders"), MakeTable("salaries")]);
        var rel = new FakeRelationRepository();
        var versionProvider = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            versionProvider: versionProvider);

        var resolver = CreateExecutorProvider(module);
        var initial = await resolver.GetExecutorAsync();
        initial.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _).ShouldBeTrue();
        initial.Schema.QueryType.Fields.TryGetField("sales_public_salaries", out _).ShouldBeTrue();

        // SetDatasetDiscoveryAsync bumps ContentVersion (not StructureVersion).
        meta.SetTables([MakeTable("orders"), MakeTable("salaries", discoveryHidden: true)]);
        versionProvider.Current = new CatalogVersion(1, 2);

        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();

        var rebuilt = await WaitForExecutorConditionAsync(
            resolver,
            e => !e.Schema.QueryType.Fields.TryGetField("sales_public_salaries", out _));

        rebuilt.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _).ShouldBeTrue();
        rebuilt.Schema.QueryType.Fields.TryGetField("sales_public_salaries", out _).ShouldBeFalse();
        rebuilt.Schema.Types.ContainsName("sales_public_salaries").ShouldBeFalse();

        await using var result = await rebuilt.ExecuteAsync("{ sales_public_salaries { id } }");
        result.ExpectOperationResult().Errors.ShouldNotBeNull();
        result.ExpectOperationResult().Errors!.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task TransportVisibilityChange_ForcesFullRebuild()
    {
        var monitor = new ReloadableDataSourceMonitor(ConfigWithTransport("pg_sec", DataSourceTransportFlags.GraphQL));
        using var transportResolver = new HierarchicalDataSourceOptionsResolver(monitor);

        var meta = new FakeMetadataRepository([MakeTable("orders", sourceName: "pg_sec")]);
        var rel = new FakeRelationRepository();
        var versionProvider = new FakeCatalogVersionProvider(new CatalogVersion(1, 1));
        var epochService = new FakeEpochValidationService();

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            versionProvider: versionProvider,
            transportResolver: transportResolver,
            epochService: epochService);

        var resolver = CreateExecutorProvider(module);
        var initial = await resolver.GetExecutorAsync();
        initial.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _).ShouldBeTrue();
        module.FingerprintCache.TotalRehashCount.ShouldBe(1);

        // Transport policy changes WITHOUT bumping catalog version -> must still rebuild.
        monitor.Reload(ConfigWithTransport("pg_sec", DataSourceTransportFlags.WebSql));
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();

        var hiddenExecutor = await WaitForExecutorConditionAsync(
            resolver,
            e => !e.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _));
        hiddenExecutor.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _).ShouldBeFalse();

        // Re-enabling GraphQL transport forces a full rebuild (fingerprint cache was cleared).
        int rehashesBeforeRestore = module.FingerprintCache.TotalRehashCount;
        monitor.Reload(ConfigWithTransport("pg_sec", DataSourceTransportFlags.GraphQL));
        (await module.CheckForCatalogChangesAsync()).ShouldBeTrue();

        var restoredExecutor = await WaitForExecutorConditionAsync(
            resolver,
            e => e.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _));
        restoredExecutor.Schema.QueryType.Fields.TryGetField("sales_public_orders", out _).ShouldBeTrue();
        module.FingerprintCache.TotalRehashCount.ShouldBeGreaterThan(rehashesBeforeRestore);
    }

    private static DataSourceConfigurationOptions ConfigWithTransport(string sourceName, DataSourceTransportFlags flags)
    {
        var options = new DataSourceConfigurationOptions();
        options.Profiles[sourceName] = new DataSourceProfileOptions { AllowedTransports = flags };
        return options;
    }

    private static async Task<IRequestExecutor> WaitForExecutorConditionAsync(
        IRequestExecutorProvider provider,
        Func<IRequestExecutor, bool> predicate)
    {
        var executor = await provider.GetExecutorAsync();
        await WaitHelper.WaitUntil(
            async () =>
            {
                if (predicate(executor))
                {
                    return true;
                }

                executor = await provider.GetExecutorAsync();
                return predicate(executor);
            },
            TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(25));

        return executor;
    }

    private sealed class FakeMetadataRepository(IReadOnlyList<TableMetadata> initialTables) : ITableMetadataRepository
    {
        private IReadOnlyList<TableMetadata> _tables = initialTables;
        private int _getAllTablesCallCount;

        public int GetAllTablesCallCount => Volatile.Read(ref _getAllTablesCallCount);

        public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _getAllTablesCallCount);
            return Task.FromResult(_tables);
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

    private sealed class FakeRelationRepository : ITableRelationRepository
    {
        private int _getForTableCalls;

        public Action<int>? OnGetRelationsForTable { get; init; }

        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableRelation>>([]);

        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
        {
            var call = Interlocked.Increment(ref _getForTableCalls);
            OnGetRelationsForTable?.Invoke(call);
            return Task.FromResult<IReadOnlyList<TableRelation>>([]);
        }

        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeCatalogVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;

        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default)
            => ValueTask.FromResult(Current);

        public Task<string> GetVersionAsync(CancellationToken ct = default)
            => Task.FromResult(Current.Formatted);
    }

    private sealed class FakeEpochValidationService : IEpochValidationService
    {
        private readonly Dictionary<TableIdentifier, long> _epochs = new();

        public void SetEpoch(TableIdentifier table, long epoch) => _epochs[table] = epoch;

        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
            => Task.FromResult(GetEpoch(table) == cachedEpoch);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
        {
            _epochs[table] = GetEpoch(table) + 1;
            return Task.CompletedTask;
        }

        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(GetEpoch(table));

        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
        {
            var result = new Dictionary<TableIdentifier, long>();
            foreach (var table in tables)
            {
                result[table] = GetEpoch(table);
            }

            return Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(result);
        }

        private long GetEpoch(TableIdentifier table) => _epochs.GetValueOrDefault(table, 0L);
    }

    private sealed class ReloadableDataSourceMonitor(DataSourceConfigurationOptions initial)
        : IOptionsMonitor<DataSourceConfigurationOptions>
    {
        private DataSourceConfigurationOptions _value = initial;
        private Action<DataSourceConfigurationOptions, string?>? _listener;

        public DataSourceConfigurationOptions CurrentValue => _value;
        public DataSourceConfigurationOptions Get(string? name) => _value;

        public IDisposable OnChange(Action<DataSourceConfigurationOptions, string?> listener)
        {
            _listener += listener;
            return NullDisposable.Instance;
        }

        public void Reload(DataSourceConfigurationOptions next)
        {
            _value = next;
            _listener?.Invoke(next, null);
        }

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public sealed record LogEntry(LogLevel Level, string Message);

        private readonly List<LogEntry> _entries = [];
        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

namespace Autheris.Benchmarks;

public static class GraphQlSchemaScaleBenchmark
{
    public sealed class BenchQuery
    {
        public string Ping() => "pong";
    }

    public static async Task RunAsync(int[]? counts = null)
    {
        counts ??= [1_000, 10_000, 50_000];
        Console.WriteLine("mode,tableCount,columnsPerTable,buildMs,allocatedMb,retainedMb,delta1TableMs,delta1TableAllocMb");

        foreach (var count in counts)
        {
            if (count >= 50_000)
            {
                await MeasureCountAsync(count, columnsPerTable: 50, partitioned: true);
            }
            else
            {
                await MeasureCountAsync(count, columnsPerTable: 50, partitioned: false);
            }
        }
    }

    private static async Task MeasureCountAsync(int tableCount, int columnsPerTable, bool partitioned)
    {
        var tables = GenerateTables(tableCount, columnsPerTable, domainCount: partitioned ? 50 : 1);
        var meta = new InMemoryBenchMetadataRepo(tables);
        var rel = new EmptyBenchRelationRepo();
        var versionProvider = new MutableVersionProvider(new CatalogVersion(1, 1));
        var options = partitioned
            ? new GraphQLOptions
            {
                SchemaPartitioning = GraphQlSchemaPartitioningMode.ByPermissionTier,
                PartitionAllowedDomains = ["domain_00"]
            }
            : new GraphQLOptions();

        ForceGc();
        long memBefore = GC.GetTotalMemory(true);
        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();

        using var module = new CatalogGraphQlTypeModule(
            meta,
            rel,
            versionProvider: versionProvider,
            graphQlOptions: options);

        var services = new ServiceCollection()
            .AddGraphQLServer()
            .AddQueryType<BenchQuery>()
            .AddTypeModule(_ => module)
            .Services
            .BuildServiceProvider();

        var executorProvider = services.GetRequiredService<IRequestExecutorProvider>();
        var executor = await executorProvider.GetExecutorAsync();
        sw.Stop();

        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        ForceGc();
        long memAfter = GC.GetTotalMemory(true);

        double buildMs = sw.Elapsed.TotalMilliseconds;
        double allocatedMb = Math.Max(0, allocAfter - allocBefore) / (1024.0 * 1024.0);
        double retainedMb = Math.Max(0, memAfter - memBefore) / (1024.0 * 1024.0);

        // Measure 1-table incremental update
        var deltaDomain = partitioned ? "domain_00" : "sales";
        var updatedTables = new List<TableMetadata>(tables) { GenerateTable(tableCount, columnsPerTable, deltaDomain) };
        meta.SetTables(updatedTables);
        versionProvider.Current = new CatalogVersion(2, 1);

        long deltaAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
        var swDelta = Stopwatch.StartNew();
        await module.CheckForCatalogChangesAsync();
        var newTypeName = $"{deltaDomain}_public_t_{tableCount:D6}";
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMinutes(3))
        {
            var nextExec = await executorProvider.GetExecutorAsync();
            if (nextExec.Schema.Types.ContainsName(newTypeName))
            {
                break;
            }
            await Task.Delay(5);
        }
        swDelta.Stop();
        long deltaAllocAfter = GC.GetTotalAllocatedBytes(precise: true);

        double deltaMs = swDelta.Elapsed.TotalMilliseconds;
        double deltaAllocMb = Math.Max(0, deltaAllocAfter - deltaAllocBefore) / (1024.0 * 1024.0);
        var mode = partitioned ? "partitioned(50domains)" : "unpartitioned";

        Console.WriteLine($"{mode},{tableCount},{columnsPerTable},{buildMs:F1},{allocatedMb:F1},{retainedMb:F1},{deltaMs:F1},{deltaAllocMb:F1}");

        GC.KeepAlive(executor);
        await services.DisposeAsync();
    }

    private static void ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static List<TableMetadata> GenerateTables(int count, int columnsPerTable, int domainCount = 1)
    {
        var list = new List<TableMetadata>(count);
        for (var i = 0; i < count; i++)
        {
            var domain = domainCount <= 1 ? "sales" : $"domain_{i % domainCount:D2}";
            list.Add(GenerateTable(i, columnsPerTable, domain));
        }
        return list;
    }

    private static TableMetadata GenerateTable(int index, int columnsPerTable, string domain = "sales")
    {
        var cols = new List<TableColumn>(columnsPerTable);
        cols.Add(new TableColumn { ColumnName = "id", DataType = "integer" });
        for (var c = 1; c < columnsPerTable; c++)
        {
            cols.Add(new TableColumn
            {
                ColumnName = $"col_{c:D2}",
                DataType = c % 3 == 0 ? "decimal(18,2)" : (c % 2 == 0 ? "timestamp" : "text")
            });
        }

        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, "public", $"t_{index:D6}"),
            Table = new Table
            {
                SourceName = "pg_main",
                SourceType = "PostgreSql",
                SchemaName = "public",
                TableName = $"t_{index:D6}",
                IsActive = true
            },
            Columns = cols,
            PrimaryKeyColumns = ["id"]
        };
    }

    private sealed class MutableVersionProvider(CatalogVersion initial) : ICatalogVersionProvider
    {
        public CatalogVersion Current { get; set; } = initial;
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Current);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult($"{Current.StructureVersion}.{Current.ContentVersion}");
    }

    private sealed class InMemoryBenchMetadataRepo(IReadOnlyList<TableMetadata> initial) : ITableMetadataRepository
    {
        private IReadOnlyList<TableMetadata> _tables = initial;
        public void SetTables(IReadOnlyList<TableMetadata> tables) => _tables = tables;
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(_tables);
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class EmptyBenchRelationRepo : ITableRelationRepository
    {
        public bool BulkRelationsAuthoritative => true;
        public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>([]);
        public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableRelation>>([]);
        public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default) =>
            Task.FromResult(new RelationWriteResult(Guid.NewGuid(), true, false));
        public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}

namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Modules.Dbt;
using Autheris.Modules.OpenMetadata;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Dbt;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

internal sealed class CountingBatchTableMetadataRepository : ITableMetadataRepository
{
    private readonly ConcurrentDictionary<TableIdentifier, TableMetadata> _tables = new();
    private int _batchCalls;
    private int _singleCalls;

    public int BatchCalls => Volatile.Read(ref _batchCalls);
    public int SingleCalls => Volatile.Read(ref _singleCalls);
    public List<int> BatchSizes { get; } = [];
    public int StoredCount => _tables.Count;

    public void Seed(TableMetadata metadata) => _tables[metadata.Identifier] = metadata;

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
        Task.FromResult(_tables.TryGetValue(table, out var existing) ? existing : null);

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

    public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _singleCalls);
        _tables[metadata.Identifier] = metadata;
        return Task.FromResult(metadata);
    }

    public Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(
        IReadOnlyList<TableMetadata> batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Increment(ref _batchCalls);
        lock (BatchSizes)
        {
            BatchSizes.Add(batch.Count);
        }

        var results = new CatalogTableUpsertResult[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            var created = !_tables.ContainsKey(batch[i].Identifier);
            _tables[batch[i].Identifier] = batch[i];
            results[i] = new CatalogTableUpsertResult(
                batch[i],
                created ? CatalogUpsertKind.Created : CatalogUpsertKind.Updated,
                CatalogWriteClassification.Restrictive);
        }

        return Task.FromResult<IReadOnlyList<CatalogTableUpsertResult>>(results);
    }

    public Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default) =>
        Task.FromResult<TableKeyDefinitionChange?>(null);
}

public sealed class IngestionCallersBatchUpsertTests
{
    private sealed class FakeDataCatalogClient(IReadOnlyList<CatalogTableAsset> assets) : IDataCatalogClient
    {
        public DataCatalogProviderType ProviderType => DataCatalogProviderType.OpenMetadata;
        public Task<IReadOnlyList<CatalogTableAsset>> GetTablesAsync(string? filter = null, CancellationToken ct = default) =>
            Task.FromResult(assets);
        public Task<CatalogTableAsset?> GetTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(assets.FirstOrDefault(a => a.Identifier.Equals(table)));
    }

    private sealed class NoopAuditWriter : IAuditWriter
    {
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task OpenApiIngestionService_UpsertsAllSchemasInSingleBatch()
    {
        var repo = new CountingBatchTableMetadataRepository();
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        const string spec = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Orders API", "version": "1" },
          "servers": [{ "url": "https://api.internal.corp" }],
          "paths": {},
          "components": {
            "schemas": {
              "Order": { "type": "object", "properties": { "id": { "type": "integer" } } },
              "Customer": { "type": "object", "properties": { "id": { "type": "integer" } } },
              "Invoice": { "type": "object", "properties": { "id": { "type": "integer" } } }
            }
          }
        }
        """;

        var result = await service.IngestOpenApiJsonAsync(spec, domain: "sales");

        result.Success.ShouldBeTrue();
        result.IngestedTablesCount.ShouldBe(3);
        repo.BatchCalls.ShouldBe(1);
        repo.SingleCalls.ShouldBe(0);
        repo.BatchSizes.ShouldBe([3]);
    }

    [Fact]
    public async Task DbtSemanticManifestIngestionService_UpsertsAllVirtualTablesInSingleBatch()
    {
        var single = SemanticCatalogTestKit.Mode(TenancyShape.Single);
        var catalog = new InMemorySemanticCatalog(single);
        var repo = new CountingBatchTableMetadataRepository();
        repo.Seed(SemanticManifestValidatorTests.Orders());
        var service = new DbtSemanticManifestIngestionService(
            catalog,
            NullLogger<DbtSemanticManifestIngestionService>.Instance,
            repo,
            gatewayOptions: single);

        const string manifestJson = """
        {
          "semantic_models": [
            {
              "name": "orders_source",
              "description": "Orders",
              "node_relation": { "relation_name": "analytics.public.orders" },
              "entities": [{ "name": "order_id", "type": "primary" }],
              "measures": [{ "name": "order_count", "agg": "count", "expr": "1" }],
              "dimensions": [{ "name": "status", "type": "categorical" }]
            },
            {
              "name": "orders_daily",
              "description": "Daily Orders",
              "node_relation": { "relation_name": "analytics.public.orders" },
              "entities": [{ "name": "order_id", "type": "primary" }],
              "measures": [{ "name": "daily_count", "agg": "count", "expr": "1" }],
              "dimensions": [{ "name": "status", "type": "categorical" }]
            }
          ],
          "metrics": []
        }
        """;

        var manifest = await service.ParseAndIngestAsync(manifestJson, TestTenants.Default);

        manifest.SemanticModels.Count.ShouldBe(2);
        repo.BatchCalls.ShouldBe(1);
        repo.SingleCalls.ShouldBe(0);
        repo.BatchSizes.ShouldBe([2]);
    }

    [Fact]
    public async Task DbtMetadataIngestionService_UpsertsAllUpdatedModelsInSingleBatch()
    {
        var repo = new CountingBatchTableMetadataRepository();
        var nodes = new Dictionary<string, object>();
        for (var i = 0; i < 3; i++)
        {
            var id = Guid.NewGuid();
            var name = $"model_{i}";
            repo.Seed(new TableMetadata
            {
                Identifier = new TableIdentifier("warehouse", "public", name),
                Table = new Table
                {
                    Id = id,
                    SourceName = "warehouse",
                    SourceType = "PostgreSql",
                    SchemaName = "public",
                    TableName = name,
                    DisplayName = name,
                    IsActive = true
                },
                Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" }]
            });
            nodes[$"model.proj.{name}"] = new
            {
                name,
                database = "warehouse",
                schema = "public",
                description = $"Updated model {i}",
                config = new { materialized = "table" },
                tags = Array.Empty<string>(),
                meta = new { },
                columns = new Dictionary<string, object>
                {
                    ["id"] = new { name = "id", data_type = "integer", description = "id", tags = Array.Empty<string>(), meta = new { } }
                },
                depends_on = new { nodes = Array.Empty<string>() }
            };
        }

        var gatewayOptions = new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = false, ActivateNewTables = false },
            DataSources = new SqlDataSourceOptions
            {
                Connections = { ["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" } }
            }
        };
        var service = new DbtMetadataIngestionService(
            proposalRepository: new InMemoryDbtProposalRepository(),
            metadataRepository: repo,
            lineageGraphStore: new UnusedLineageGraphStore(),
            epochRepository: null,
            gatewayOptions: Options.Create(gatewayOptions),
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            tagImport: null,
            artifactStore: null,
            testTargetMap: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance,
            auditLog: new NullAuditLogRepository());

        var manifestJson = JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes, sources = new Dictionary<string, object>() });
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifestJson));

        var result = await service.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(3);
        repo.BatchCalls.ShouldBe(1);
        repo.SingleCalls.ShouldBe(0);
        repo.BatchSizes.ShouldBe([3]);
    }

    [Fact]
    public async Task OpenMetadataSyncService_UpsertsAllTablesInSingleBatch()
    {
        var repo = new CountingBatchTableMetadataRepository();
        var omTables = Enumerable.Range(0, 3).Select(i => new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = $"table_{i}",
            FullyQualifiedName = $"svc.corp.dbo.table_{i}",
            Service = new OpenMetadataEntityReference { Name = "svc" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        }).ToList();

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { Enabled = true, DefaultTenantId = "default-tenant" }
        });
        var service = new OpenMetadataSyncService(
            new FakeOpenMetadataClient(omTables),
            repo,
            new FakeConsentRepository(),
            new NoopPolicyEpochRepository(),
            options,
            NullLogger<OpenMetadataSyncService>.Instance);

        var result = await service.SyncPermissionsAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTables.ShouldBe(3);
        repo.BatchCalls.ShouldBe(1);
        repo.SingleCalls.ShouldBe(0);
        repo.BatchSizes.ShouldBe([3]);
    }

    [Fact]
    public async Task DataCatalogSyncService_UpsertsAllTablesInSingleBatch_AndInvalidatesAllEpochs()
    {
        var repo = new CountingBatchTableMetadataRepository();
        var epochs = new FakeEpochValidationService();
        var tables = new[]
        {
            new TableIdentifier("sales", "dbo", "orders"),
            new TableIdentifier("sales", "dbo", "customers"),
            new TableIdentifier("sales", "dbo", "invoices")
        };
        var assets = tables.Select(id => new CatalogTableAsset
        {
            Identifier = id,
            SourceType = "PostgreSql",
            Columns = [new CatalogColumnAsset { ColumnName = "id", DataType = "int" }]
        }).ToList();

        var service = new DataCatalogSyncService(
            new FakeDataCatalogClient(assets),
            repo,
            epochs,
            Options.Create(new GatewayOptions()),
            new NoopAuditWriter(),
            NullLogger<DataCatalogSyncService>.Instance);

        var result = await service.SyncCatalogAsync(dryRun: false);

        result.Success.ShouldBeTrue();
        result.SyncedTablesCount.ShouldBe(3);
        repo.BatchCalls.ShouldBe(1);
        repo.SingleCalls.ShouldBe(0);
        repo.BatchSizes.ShouldBe([3]);
        foreach (var id in tables)
        {
            (await epochs.GetCurrentEpochAsync(id)).ShouldBe(2L);
        }
    }
}

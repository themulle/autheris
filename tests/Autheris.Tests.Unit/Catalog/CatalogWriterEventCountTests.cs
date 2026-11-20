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
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Dbt;
using Autheris.Modules.OpenMetadata;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Dbt;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Metadata repository double that follows the real repository's write protocol: every table write increments its own epoch,
/// the catalog version bump (and its event) is deferred to an active write scope for additive writes and published per write otherwise.
/// </summary>
internal sealed class ProtocolMetadataRepository : ITableMetadataRepository
{
    private readonly ConcurrentDictionary<TableIdentifier, TableMetadata> _tables = new();
    private int _versionBumps;
    private int _epochs;

    public int VersionBumps => Volatile.Read(ref _versionBumps);

    public int EpochIncrements => Volatile.Read(ref _epochs);

    public int TableCount => _tables.Count;

    public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
        Task.FromResult(_tables.TryGetValue(table, out var metadata) ? metadata : null);

    public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

    public void Seed(TableMetadata metadata) => _tables[metadata.Identifier] = metadata;

    public async Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
    {
        var existing = _tables.TryGetValue(metadata.Identifier, out var current) ? current : null;
        _tables[metadata.Identifier] = metadata;
        Interlocked.Increment(ref _epochs);

        var scope = CatalogWriteScopeContext.TryGetActive(this);
        var restrictive = existing is not null && (existing.Table.IsActive != metadata.Table.IsActive || metadata.ReplaceKeyDefinition);
        if (scope is not null && !restrictive)
        {
            await scope.RecordAdditiveWriteAsync(1, 0, ct).ConfigureAwait(false);
        }
        else
        {
            await FlushCatalogVersionAsync(1, 0, ct).ConfigureAwait(false);
            if (scope is not null)
            {
                await scope.FlushPendingAsync(ct).ConfigureAwait(false);
            }
        }

        return metadata;
    }

    public Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _versionBumps);
        return Task.CompletedTask;
    }

    public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
        TableIdentifier table,
        IReadOnlyList<string> primaryKey,
        IReadOnlyList<TableIndexDefinition> indexes,
        IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
        CancellationToken ct = default) =>
        Task.FromResult<TableKeyDefinitionChange?>(null);
}

public sealed class OpenApiIngestionCatalogEventCountTests
{
    private static string BuildSpec(int schemaCount)
    {
        var sb = new StringBuilder();
        sb.Append("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"Bulk API\",\"version\":\"1\"},\"servers\":[{\"url\":\"https://bulk.internal.corp\"}],\"paths\":{},\"components\":{\"schemas\":{");
        for (var i = 0; i < schemaCount; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($"\"Entity{i}\":{{\"type\":\"object\",\"description\":\"Entity {i}\",\"properties\":{{\"id\":{{\"type\":\"integer\"}}}}}}");
        }

        sb.Append("}}}");
        return sb.ToString();
    }

    [Fact]
    public async Task Ingest1200Schemas_ProducesOneVersionBumpPerChunk()
    {
        var repo = new ProtocolMetadataRepository();
        var dirty = new CatalogDirtyFlag();
        var factory = new CatalogWriteScopeFactory(repo, dirty);
        var service = new OpenApiIngestionService(
            repo,
            NullLogger<OpenApiIngestionService>.Instance,
            catalogWriteScopeFactory: factory);

        var result = await service.IngestOpenApiJsonAsync(BuildSpec(1200), domain: "bulk");

        result.Success.ShouldBeTrue();
        result.IngestedTablesCount.ShouldBe(1200);
        repo.EpochIncrements.ShouldBe(1200);
        // 1200 additive tables = two full chunks of 500 plus the remainder flushed when the scope ends.
        repo.VersionBumps.ShouldBe(3);
        dirty.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task IngestWithoutScopeFactory_PublishesPerTable()
    {
        var repo = new ProtocolMetadataRepository();
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);

        var result = await service.IngestOpenApiJsonAsync(BuildSpec(7), domain: "bulk");

        result.Success.ShouldBeTrue();
        repo.VersionBumps.ShouldBe(7);
    }
}

internal sealed class UnusedLineageGraphStore : ILineageGraphStore
{
    public int Count => 0;
    public LineageNode? GetNode(string nodeId) => null;
    public bool ContainsNode(string nodeId) => false;
    public void UpdateGraph(IEnumerable<LineageNode> nodes) { }
    public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
}

internal sealed class NoopPolicyEpochRepository : IPolicyEpochRepository
{
    public Task<long> GetTableEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(0L);
    public Task<long> IncrementTableEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
}

internal sealed class FakeOpenMetadataClient(IReadOnlyList<OpenMetadataTable> tables) : IOpenMetadataClient
{
    public Task<IReadOnlyList<OpenMetadataTable>> GetTablesAsync(string? service = null, CancellationToken ct = default) =>
        Task.FromResult(tables);

    public Task<OpenMetadataTable?> GetTableByFqnAsync(string fqn, CancellationToken ct = default) =>
        Task.FromResult<OpenMetadataTable?>(null);

    public Task<IReadOnlyList<OpenMetadataPolicy>> GetPoliciesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([]);

    public Task<IReadOnlyList<OpenMetadataRole>> GetRolesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpenMetadataRole>>([]);

    public Task<IReadOnlyList<OpenMetadataTeam>> GetTeamsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]);

    public Task<IReadOnlyList<OpenMetadataUser>> GetUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]);
}

public sealed class DbtSyncCatalogEventCountTests
{
    private static string BuildManifest(int modelCount)
    {
        var nodes = new Dictionary<string, object>(modelCount);
        for (var i = 0; i < modelCount; i++)
        {
            var name = $"model_{i}";
            nodes[$"model.proj.{name}"] = new
            {
                name,
                database = "warehouse",
                schema = "public",
                description = $"Model {i}",
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

        return JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes, sources = new Dictionary<string, object>() });
    }

    private static DbtMetadataIngestionService CreateService(
        ITableMetadataRepository repo,
        ICatalogWriteScopeFactory? scopeFactory)
    {
        var gatewayOptions = new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = false, ActivateNewTables = false },
            DataSources = new SqlDataSourceOptions
            {
                Connections = { ["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" } }
            }
        };

        return new DbtMetadataIngestionService(
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
            auditLog: new NullAuditLogRepository(),
            catalogWriteScopeFactory: scopeFactory);
    }

    [Fact]
    public async Task Sync1000Tables_ProducesOneVersionBumpPerChunk()
    {
        var repo = new ProtocolMetadataRepository();
        for (var i = 0; i < 1000; i++)
        {
            var id = Guid.NewGuid();
            repo.Seed(new TableMetadata
            {
                Identifier = new TableIdentifier("warehouse", "public", $"model_{i}"),
                Table = new Table
                {
                    Id = id,
                    SourceName = "warehouse",
                    SourceType = "PostgreSql",
                    SchemaName = "public",
                    TableName = $"model_{i}",
                    DisplayName = $"model_{i}",
                    IsActive = true
                },
                Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" }]
            });
        }

        var dirty = new CatalogDirtyFlag();
        var factory = new CatalogWriteScopeFactory(repo, dirty);
        var service = CreateService(repo, factory);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(BuildManifest(1000)));
        var result = await service.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(1000);
        repo.EpochIncrements.ShouldBe(1000);
        // 1000 additive tables = 2 chunks of 500 -> 2 version bumps (plus 0 on dispose).
        repo.VersionBumps.ShouldBe(2);
        dirty.IsDirty.ShouldBeFalse();
    }
}

public sealed class OpenMetadataSyncCatalogEventCountTests
{
    private static IReadOnlyList<OpenMetadataTable> BuildTables(int count) =>
        Enumerable.Range(0, count).Select(i => new OpenMetadataTable
        {
            Id = Guid.NewGuid(),
            Name = $"table_{i}",
            FullyQualifiedName = $"svc.corp.dbo.table_{i}",
            Service = new OpenMetadataEntityReference { Name = "svc" },
            DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
            Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
        }).ToList();

    private static OpenMetadataSyncService CreateService(
        IReadOnlyList<OpenMetadataTable> tables,
        ITableMetadataRepository repo,
        ICatalogWriteScopeFactory? scopeFactory)
    {
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { Enabled = true, DefaultTenantId = "default-tenant" }
        });

        return new OpenMetadataSyncService(
            new FakeOpenMetadataClient(tables),
            repo,
            new FakeConsentRepository(),
            new NoopPolicyEpochRepository(),
            options,
            NullLogger<OpenMetadataSyncService>.Instance,
            catalogWriteScopeFactory: scopeFactory);
    }

    [Fact]
    public async Task SyncManyTables_ProducesSingleVersionBump()
    {
        var repo = new ProtocolMetadataRepository();
        var dirty = new CatalogDirtyFlag();
        var factory = new CatalogWriteScopeFactory(repo, dirty);
        var service = CreateService(BuildTables(50), repo, factory);

        await service.SyncPermissionsAsync(dryRun: false);

        repo.TableCount.ShouldBe(50);
        repo.EpochIncrements.ShouldBe(50);
        repo.VersionBumps.ShouldBe(1);
        dirty.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncWithoutScopeFactory_PublishesPerTable()
    {
        var repo = new ProtocolMetadataRepository();
        var service = CreateService(BuildTables(5), repo, scopeFactory: null);

        await service.SyncPermissionsAsync(dryRun: false);

        repo.TableCount.ShouldBe(5);
        repo.VersionBumps.ShouldBe(5);
    }
}

public sealed class SemanticManifestCatalogEventCountTests
{
    private static string BuildSemanticManifest(int modelCount)
    {
        var models = Enumerable.Range(0, modelCount).Select(i => new
        {
            name = $"orders_model_{i}",
            description = $"Semantic model {i}",
            node_relation = new { relation_name = $"analytics.public.orders_{i}" },
            entities = new[] { new { name = "order_id", type = "primary" } },
            measures = new[] { new { name = "order_total", agg = "sum", expr = "amount", description = "total" } },
            dimensions = new[] { new { name = "status", type = "categorical", description = "status" } }
        }).ToArray();

        return JsonSerializer.Serialize(new { semantic_models = models, metrics = Array.Empty<object>() });
    }

    [Fact]
    public async Task IngestManySemanticModels_ProducesSingleVersionBump()
    {
        var single = SemanticCatalogTestKit.Mode(TenancyShape.Single);
        var catalog = new InMemorySemanticCatalog(single);
        var repo = new ProtocolMetadataRepository();
        var dirty = new CatalogDirtyFlag();
        var factory = new CatalogWriteScopeFactory(repo, dirty);
        var service = new DbtSemanticManifestIngestionService(
            catalog,
            NullLogger<DbtSemanticManifestIngestionService>.Instance,
            repo,
            gatewayOptions: single,
            catalogWriteScopeFactory: factory);

        var manifest = await service.ParseAndIngestAsync(BuildSemanticManifest(50), TestTenants.Default);

        manifest.SemanticModels.Count.ShouldBe(50);
        repo.TableCount.ShouldBe(50);
        repo.EpochIncrements.ShouldBe(50);
        repo.VersionBumps.ShouldBe(1);
        dirty.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task IngestWithoutScopeFactory_PublishesPerTable()
    {
        var single = SemanticCatalogTestKit.Mode(TenancyShape.Single);
        var catalog = new InMemorySemanticCatalog(single);
        var repo = new ProtocolMetadataRepository();
        var service = new DbtSemanticManifestIngestionService(
            catalog,
            NullLogger<DbtSemanticManifestIngestionService>.Instance,
            repo,
            gatewayOptions: single);

        var manifest = await service.ParseAndIngestAsync(BuildSemanticManifest(5), TestTenants.Default);

        manifest.SemanticModels.Count.ShouldBe(5);
        repo.VersionBumps.ShouldBe(5);
    }
}

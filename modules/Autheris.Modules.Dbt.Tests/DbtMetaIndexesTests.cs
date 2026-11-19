namespace Autheris.Modules.Dbt.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Modules.Dbt;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>WP-SIMPL-44 PR 4 (#459): dbt config.meta.indexes / meta.indexes parsing and declared-catalog ingestion.</summary>
public sealed class DbtMetaIndexesTests
{
    private static readonly TableIdentifier OrdersId = new("warehouse", "dbo", "orders");
    private static readonly TableIdentifier ContactsSourceId = new("raw_lake", "crm", "contacts_raw");

    [Fact]
    public async Task MetaIndexes_OnSource_DeclaresPkAndIndexes()
    {
        const string manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": {
                "materialized": "table",
                "meta": {
                  "indexes": [
                    { "columns": ["tenant_id ASC", "id DESC"], "primary_key": true, "clustered": true },
                    { "columns": ["tenant_id", "created_at DESC"], "unique": false }
                  ]
                }
              },
              "columns": {
                "tenant_id": { "name": "tenant_id", "data_type": "varchar", "meta": {}, "tags": [] },
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "created_at": { "name": "created_at", "data_type": "timestamp", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          },
          "sources": {
            "source.proj.raw_lake.contacts_raw": {
              "name": "contacts_raw", "database": "raw_lake", "schema": "crm", "description": "contacts",
              "meta": {
                "indexes": [
                  { "columns": ["contact_id"], "primary_key": true, "clustered": true },
                  { "columns": ["email"], "unique": true },
                  { "columns": ["region", "created_at DESC"], "name": "ix_contacts_region_created" }
                ]
              },
              "columns": {
                "contact_id": { "name": "contact_id", "data_type": "varchar", "meta": {}, "tags": [] },
                "email": { "name": "email", "data_type": "varchar", "meta": {}, "tags": [] },
                "region": { "name": "region", "data_type": "varchar", "meta": {}, "tags": [] },
                "created_at": { "name": "created_at", "data_type": "timestamp", "meta": {}, "tags": [] }
              }
            }
          }
        }
        """;

        var repo = new InMemoryTableRepo();
        repo.Seed(ExistingTable(OrdersId, "tenant_id", "id", "created_at"));
        repo.Seed(ExistingTable(ContactsSourceId, "contact_id", "email", "region", "created_at"));
        var logger = new RecordingLogger<DbtMetadataIngestionService>();
        var service = CreateService(repo, logger);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        await service.IngestManifestStreamAsClusterAdminAsync(stream, dryRun: false);

        var source = repo.Stored(ContactsSourceId);
        source.ReplaceKeyDefinition.ShouldBeTrue();
        source.PrimaryKeyColumns.ShouldBe(["contact_id"]);
        source.Indexes.Count.ShouldBe(2);
        source.Indexes[0].IsUnique.ShouldBeTrue();
        source.Indexes[0].Columns.ShouldBe(["email"]);
        source.Indexes[1].Name.ShouldBe("ix_contacts_region_created");
        source.Indexes[1].IsUnique.ShouldBeFalse();
        source.Indexes[1].Columns.ShouldBe(["region", "created_at"]);
        source.Indexes[1].Directions.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);

        var model = repo.Stored(OrdersId);
        model.ReplaceKeyDefinition.ShouldBeTrue();
        model.PrimaryKeyColumns.ShouldBe(["tenant_id", "id"]);
        model.PrimaryKeyDirections.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
        model.Indexes.Single().IsUnique.ShouldBeFalse();
        model.Indexes.Single().Columns.ShouldBe(["tenant_id", "created_at"]);
        model.Indexes.Single().Directions.ShouldBe([KeyColumnDirection.Ascending, KeyColumnDirection.Descending]);
    }

    [Fact]
    public async Task MetaIndexes_WinsOverKeyTests()
    {
        const string manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": {
                "materialized": "table",
                "meta": {
                  "indexes": [
                    { "columns": ["id"], "primary_key": true },
                    { "columns": ["tenant_id", "id"], "unique": false }
                  ]
                }
              },
              "columns": {
                "tenant_id": { "name": "tenant_id", "data_type": "int", "meta": {}, "tags": [] },
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "email": { "name": "email", "data_type": "varchar", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            },
            "test.proj.unique_orders_email.1": {
              "attached_node": "model.proj.orders",
              "column_name": "email",
              "test_metadata": { "name": "unique", "kwargs": { "column_name": "email" } }
            },
            "test.proj.not_null_orders_email.2": {
              "attached_node": "model.proj.orders",
              "column_name": "email",
              "test_metadata": { "name": "not_null", "kwargs": { "column_name": "email" } }
            }
          }
        }
        """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var parsed = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);
        var model = parsed.Single(m => m.Name == "orders");

        model.DeclaredKeys.ShouldNotBeNull();
        model.DeclaredKeys!.Invalid.ShouldBeFalse();
        model.DeclaredKeys.PrimaryKey.ShouldBe(["id"]);
        model.DeclaredKeys.UniqueConstraints.ShouldBeEmpty();
        model.DeclaredKeys.DeclaredIndexes.ShouldNotBeNull();
        model.DeclaredKeys.DeclaredIndexes!.Count.ShouldBe(1);
        model.DeclaredKeys.DeclaredIndexes[0].IsUnique.ShouldBeFalse();
        model.DeclaredKeys.DeclaredIndexes[0].Columns.ShouldBe(["tenant_id", "id"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task InvalidEntry_DropsDeclaration_LogsNoValues()
    {
        const string badIndexManifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": {
                "materialized": "table",
                "meta": {
                  "indexes": [
                    { "columns": ["id"], "primary_key": true },
                    { "columns": ["secret_col_value"], "name": "bad$idx;drop" }
                  ]
                }
              },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "secret_col_value": { "name": "secret_col_value", "data_type": "varchar", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        const string unknownColumnManifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "meta": {
                "indexes": [
                  { "columns": ["id"], "primary_key": true },
                  { "columns": ["missing_secret_col"], "name": "ix_secret_lookup" }
                ]
              },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        using var parseStream = new MemoryStream(Encoding.UTF8.GetBytes(badIndexManifest));
        var parsed = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(parseStream);
        var model = parsed.Models.Single(m => m.Name == "orders");
        model.DeclaredKeys.ShouldNotBeNull();
        model.DeclaredKeys!.Invalid.ShouldBeTrue();
        parsed.Warnings.ShouldNotBeEmpty();
        parsed.Warnings.ShouldAllBe(w =>
            !w.Contains("secret_col_value", StringComparison.OrdinalIgnoreCase) &&
            !w.Contains("bad$idx;drop", StringComparison.OrdinalIgnoreCase));

        var repo = new InMemoryTableRepo();
        repo.Seed(ExistingTable(OrdersId, "id"));
        var logger = new RecordingLogger<DbtMetadataIngestionService>();
        var service = CreateService(repo, logger);

        using var ingestBadStream = new MemoryStream(Encoding.UTF8.GetBytes(badIndexManifest));
        await service.IngestManifestStreamAsClusterAdminAsync(ingestBadStream, dryRun: false);
        using var ingestUnknownColStream = new MemoryStream(Encoding.UTF8.GetBytes(unknownColumnManifest));
        await service.IngestManifestStreamAsClusterAdminAsync(ingestUnknownColStream, dryRun: false);

        var stored = repo.Stored(OrdersId);
        stored.ReplaceKeyDefinition.ShouldBeFalse();
        stored.PrimaryKeyColumns.ShouldBe(["id"]);
        stored.Indexes.ShouldBeEmpty();

        var warningLogs = logger.Warnings.ToList();
        warningLogs.Count.ShouldBeGreaterThanOrEqualTo(2);
        warningLogs.ShouldAllBe(m =>
            !m.Contains("secret_col_value", StringComparison.OrdinalIgnoreCase) &&
            !m.Contains("bad$idx;drop", StringComparison.OrdinalIgnoreCase) &&
            !m.Contains("missing_secret_col", StringComparison.OrdinalIgnoreCase) &&
            !m.Contains("ix_secret_lookup", StringComparison.OrdinalIgnoreCase));
        warningLogs.ShouldContain(m => m.Contains("INDEX_NAME_INVALID", StringComparison.Ordinal));
        warningLogs.ShouldContain(m => m.Contains("INDEX_UNKNOWN_COLUMN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoPrimaryKeys_Rejected()
    {
        const string manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": {
                "materialized": "table",
                "meta": {
                  "indexes": [
                    { "columns": ["id"], "primary_key": true },
                    { "columns": ["tenant_id"], "primary_key": true }
                  ]
                }
              },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "tenant_id": { "name": "tenant_id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        using var parseStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        var parsed = await DbtArtifactStreamingParser.ParseManifestWithRelationshipsAsync(parseStream);
        var model = parsed.Models.Single(m => m.Name == "orders");
        model.DeclaredKeys.ShouldNotBeNull();
        model.DeclaredKeys!.Invalid.ShouldBeTrue();
        model.DeclaredKeys.InvalidRule.ShouldBe("DUPLICATE_PRIMARY_KEY");

        var repo = new InMemoryTableRepo();
        repo.Seed(ExistingTable(OrdersId, "id", "tenant_id"));
        var logger = new RecordingLogger<DbtMetadataIngestionService>();
        var service = CreateService(repo, logger);

        using var ingestStream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
        await service.IngestManifestStreamAsClusterAdminAsync(ingestStream, dryRun: false);

        repo.Stored(OrdersId).ReplaceKeyDefinition.ShouldBeFalse();
        repo.Stored(OrdersId).PrimaryKeyColumns.ShouldBe(["id"]);
    }

    private static DbtMetadataIngestionService CreateService(InMemoryTableRepo repo, ILogger<DbtMetadataIngestionService> logger) =>
        new(new InMemoryProposalRepo(), repo, new InMemoryLineageStore(), logger);

    private static TableMetadata ExistingTable(TableIdentifier id, params string[] columns) => new()
    {
        Identifier = id,
        Table = new Table
        {
            SourceName = id.Domain,
            SchemaName = id.Schema,
            TableName = id.TableName,
            SourceType = "PostgreSql",
            IsActive = true
        },
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList(),
        PrimaryKeyColumns = [columns[0]]
    };

    private sealed class InMemoryTableRepo : ITableMetadataRepository
    {
        private readonly ConcurrentDictionary<TableIdentifier, TableMetadata> _tables = new();

        public void Seed(TableMetadata metadata) => _tables[metadata.Identifier] = metadata;
        public TableMetadata Stored(TableIdentifier id) => _tables[id];

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(_tables.TryGetValue(table, out var m) ? m : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>(_tables.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            _tables[metadata.Identifier] = metadata;
            return Task.FromResult(metadata);
        }

        public Task<IReadOnlyList<CatalogTableUpsertResult>> UpsertTableMetadataBatchAsync(IReadOnlyList<TableMetadata> batch, CancellationToken ct = default)
        {
            var results = new List<CatalogTableUpsertResult>(batch.Count);
            foreach (var item in batch)
            {
                var created = !_tables.ContainsKey(item.Identifier);
                _tables[item.Identifier] = item;
                results.Add(new CatalogTableUpsertResult(item, created ? CatalogUpsertKind.Created : CatalogUpsertKind.Updated, CatalogWriteClassification.Restrictive));
            }

            return Task.FromResult<IReadOnlyList<CatalogTableUpsertResult>>(results);
        }

        public Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) =>
            Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class InMemoryProposalRepo : IDbtProposalRepository
    {
        public Task<DbtMetadataProposal> AddProposalAsync(DbtMetadataProposal proposal, CancellationToken ct = default) => Task.FromResult(proposal);
        public Task<IReadOnlyList<DbtMetadataProposal>> GetPendingProposalsAsync(TableIdentifier? table = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DbtMetadataProposal>>([]);
        public Task<DbtMetadataProposal?> GetProposalByIdAsync(Guid proposalId, CancellationToken ct = default) => Task.FromResult<DbtMetadataProposal?>(null);
        public Task<DbtMetadataProposal> UpdateProposalStatusAsync(Guid proposalId, DbtProposalStatus status, string reviewedBy, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryLineageStore : ILineageGraphStore
    {
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) { }
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
        public int Count => 0;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

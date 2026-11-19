namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Dbt;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryArtifactServingTests
{
    private sealed class CapturingArtifactStore : IDiscoveryArtifactStore
    {
        public Dictionary<Guid, DiscoveryArtifacts> Items { get; } = [];
        public List<(DiscoveryArtifacts Item, Sid Actor)> UpsertCalls { get; } = [];

        public Task<DiscoveryArtifacts?> GetAsync(Guid tableId, CancellationToken ct = default)
            => Task.FromResult(Items.TryGetValue(tableId, out var a) ? a : null);

        public Task<bool> UpsertAsync(DiscoveryArtifacts artifacts, Sid actor, CancellationToken ct = default)
        {
            Items[artifacts.TableId] = artifacts;
            UpsertCalls.Add((artifacts, actor));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(Guid tableId, Sid actor, CancellationToken ct = default)
            => Task.FromResult(Items.Remove(tableId));
    }

    private sealed class NoopAuditLogRepository : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class NoopLineageGraphStore : ILineageGraphStore
    {
        public LineageNode? GetNode(string nodeId) => null;
        public bool ContainsNode(string nodeId) => false;
        public void UpdateGraph(IEnumerable<LineageNode> nodes) { }
        public IReadOnlyCollection<LineageNode> GetAllNodes() => [];
        public int Count => 0;
    }

    [Fact]
    public async Task GetSamples_StripsNewlyHiddenColumns_CapsRows_AndAuditsRead()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: "tenant-a",
            columns:
            [
                ("id", false, false),
                ("secret_note", true, true),
                ("total", false, false)
            ]);
        tables.Tables.Add(orders);

        var store = new CapturingArtifactStore();
        store.Items[orders.Table.Id] = new DiscoveryArtifacts(
            orders.Table.Id,
            "tenant-a",
            SamplesJson: """[{"id":1,"secret_note":"hidden_leak","total":10},{"id":2,"secret_note":"hidden2","total":20},{"id":3,"secret_note":"hidden3","total":30}]""",
            StatisticsJson: null,
            ContentSha256: "abc",
            UploadedBy: "S-UPLOADER",
            UploadedAt: DateTimeOffset.UtcNow,
            SchemaVersion: 1);

        var audit = new FakeAuditWriter();
        var service = new DiscoveryArtifactService(
            tables,
            store,
            new FakeDiscoveryOwnershipRepository(),
            audit,
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { MaxSampleRows = 2 } }));

        var caller = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var res = await service.GetSamplesAsync(caller, orders.Identifier.ToString());

        res.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        res.Payload.ShouldNotBeNull();
        res.Payload!.Available.ShouldBeTrue();
        res.Payload.Samples.Count.ShouldBe(2);
        res.Payload.Samples[0].ContainsKey("secret_note").ShouldBeFalse();
        res.Payload.Samples[0]["id"].ShouldBe(1L);
        res.Payload.Samples[0]["total"].ShouldBe(10L);

        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.MetadataSampleRead);

        // GraphQL channel surfaces identical sample payload
        var gqlAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };
        var gql = await new Query().GetCatalogSamplesAsync("sales", "dbo", "orders", limit: 1, service, gqlAccessor);
        gql.Available.ShouldBeTrue();
        gql.Samples.Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetSamples_Hidden_Disabled_CrossTenant_AndMissingArtifact_AllReturnUnavailableIdentically()
    {
        var tables = new FakeDiscoveryTableRepository();
        var hidden = DiscoveryTestBuilder.MakeTable("sales", "hidden_t", ownerTenantId: "tenant-a", hidden: true);
        var disabled = DiscoveryTestBuilder.MakeTable("sales", "disabled_t", ownerTenantId: "tenant-a", samplesDisabled: true);
        var foreign = DiscoveryTestBuilder.MakeTable("sales", "foreign_t", ownerTenantId: "tenant-b");
        var noArtifact = DiscoveryTestBuilder.MakeTable("sales", "empty_t", ownerTenantId: "tenant-a");
        tables.Tables.AddRange([hidden, disabled, foreign, noArtifact]);

        var store = new CapturingArtifactStore();
        foreach (var t in new[] { hidden, disabled, foreign })
        {
            store.Items[t.Table.Id] = new DiscoveryArtifacts(
                t.Table.Id, t.OwnerTenantId!, """[{"id":1}]""", null, "sha", "up", DateTimeOffset.UtcNow, 1);
        }

        var service = new DiscoveryArtifactService(
            tables,
            store,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()));

        var caller = DiscoveryTestBuilder.User("S-USER", "tenant-a");

        foreach (var target in new[]
        {
            hidden.Identifier.ToString(),
            disabled.Identifier.ToString(),
            foreign.Identifier.ToString(),
            noArtifact.Identifier.ToString(),
            "sales.dbo.does_not_exist"
        })
        {
            var r = await service.GetSamplesAsync(caller, target);
            r.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
            r.Payload!.Available.ShouldBeFalse();
            r.Payload.Samples.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task DiscoveryService_ServesStatistics_HidesHiddenAndNoAccessColumns_AndReflectsSamplesAvailable()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: "tenant-a",
            columns:
            [
                ("id", false, false),
                ("secret_hidden", true, true),
                ("denied_col", true, false)
            ]);
        tables.Tables.Add(orders);

        var store = new FakeDiscoveryArtifactStore();
        store.Artifacts[orders.Table.Id] = new DiscoveryArtifacts(
            orders.Table.Id,
            "tenant-a",
            SamplesJson: """[{"id":1}]""",
            StatisticsJson: JsonSerializer.Serialize(new
            {
                table = new
                {
                    approxRowCount = 500,
                    partitionColumns = new[] { "id", "secret_hidden" }
                },
                columns = new Dictionary<string, object>
                {
                    ["id"] = new { nullRatio = 0.0, approxDistinctCount = 500 },
                    ["secret_hidden"] = new { nullRatio = 0.0, approxDistinctCount = 10 },
                    ["denied_col"] = new { nullRatio = 0.25, topValues = new[] { "secret" } }
                }
            }),
            ContentSha256: "sha1",
            UploadedBy: "up",
            UploadedAt: DateTimeOffset.UtcNow,
            SchemaVersion: 1);

        var pdp = new FakeAccessDecisionPoint(req => TableAccessDecision.Allowed(
            req.Object.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["denied_col"] = ColumnAccessLevel.Deny
            }));

        var discovery = DiscoveryTestBuilder.CreateService(tables: tables, pdp: pdp, artifacts: store);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var obj = await discovery.GetAsync(orders.Identifier, caller);
        obj.ShouldNotBeNull();
        obj!.SamplesAvailable.ShouldBeTrue();
        obj.Statistics.ShouldNotBeNull();
        obj.Statistics!.PartitionColumns.ShouldBe(["id"]);
        obj.Columns.Any(c => c.Name == "secret_hidden").ShouldBeFalse();
        obj.Columns.Single(c => c.Name == "id").Statistics.ShouldNotBeNull();
        obj.Columns.Single(c => c.Name == "denied_col").Statistics.ShouldBeNull();
    }

    [Fact]
    public async Task DbtIngestion_WithSyntheticSamples_WritesValidatedArtifactsToStoreAsDbtIngestion()
    {
        var manifest = """
        {
          "nodes": {
            "model.warehouse.orders": {
              "name": "orders",
              "database": "warehouse",
              "schema": "public",
              "config": { "materialized": "table" },
              "meta": {
                "synthetic_samples": [
                  { "id": 1, "note": "row1" },
                  { "id": 2, "note": "row2" }
                ]
              },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "note": { "name": "note", "data_type": "varchar", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        var storedTables = new Dictionary<TableIdentifier, TableMetadata>();
        var repo = new InMemoryDbtTableRepo(storedTables);
        var artifactStore = new CapturingArtifactStore();
        var options = new GatewayOptions
        {
            Tenancy = TenancyShape.Single.Options(),
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sales"] = new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = "Host=localhost" }
                }
            },
            Dbt = new DbtOptions
            {
                CreateMissingTables = true,
                ActivateNewTables = true,
                DatabaseMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["warehouse"] = "sales"
                }
            }
        };

        var audit = new FakeAuditWriter();
        var svc = new DbtMetadataIngestionService(
            new InMemoryDbtProposalRepository(),
            repo,
            new NoopLineageGraphStore(),
            epochRepository: null,
            gatewayOptions: Options.Create(options),
            sqlEndpointLoader: null,
            relationRepository: null,
            virtualFilterAdmin: null,
            tagImport: null,
            artifactStore: artifactStore,
            testTargetMap: null,
            logger: NullLogger<DbtMetadataIngestionService>.Instance,
            auditLog: new NoopAuditLogRepository(),
            auditWriter: audit,
            discoveryOptions: Options.Create(new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { MaxSampleRows = 2 } }));

        using var ms = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest));
        await svc.IngestManifestStreamAsClusterAdminAsync(ms, dryRun: false);

        artifactStore.UpsertCalls.Count.ShouldBe(1);
        artifactStore.UpsertCalls[0].Actor.Value.ShouldBe("dbt-ingestion");
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.CatalogDiscoveryArtifactsChanged);
        var saved = artifactStore.UpsertCalls[0].Item;
        saved.UploadedBy.ShouldBe("dbt-ingestion");
        saved.SamplesJson.ShouldNotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(saved.SamplesJson!);
        doc.RootElement.GetArrayLength().ShouldBe(2);

        // Exceeding MaxSampleRows fails validation cleanly with a warning and does not overwrite existing artifacts
        var oversizedManifest = manifest.Replace(
            """{ "id": 2, "note": "row2" }""",
            """{ "id": 2, "note": "row2" }, { "id": 3, "note": "row3" }""",
            StringComparison.Ordinal);
        using var msOversized = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(oversizedManifest));
        var oversizedResult = await svc.IngestManifestStreamAsClusterAdminAsync(msOversized, dryRun: false);
        oversizedResult.Warnings.ShouldContain(w => w.Contains("SAMPLE_ROW_LIMIT_EXCEEDED", StringComparison.Ordinal));
        artifactStore.UpsertCalls.Count.ShouldBe(1);
    }

    private sealed class InMemoryDbtTableRepo(Dictionary<TableIdentifier, TableMetadata> store) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(store.TryGetValue(table, out var m) ? m : null);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableMetadata>>(store.Values.ToList());

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            var effective = metadata.Table.Id == Guid.Empty
                ? metadata with { Table = metadata.Table with { Id = Guid.NewGuid() } }
                : metadata;
            store[effective.Identifier] = effective;
            return Task.FromResult(effective);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}

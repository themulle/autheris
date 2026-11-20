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
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Modules.Dbt;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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

    [Fact]
    public async Task RestrictedScope_WithholdsValueBearingArtifacts_ForNonClearColumns_AcrossRestGraphQlAndMcp()
    {
        var tables = new FakeDiscoveryTableRepository();
        var orders = DiscoveryTestBuilder.MakeTable(
            "sales",
            "orders",
            ownerTenantId: "tenant-a",
            columns:
            [
                ("id", false, false),
                ("ssn", true, false),
                ("cleared_secret", true, false),
                ("denied_col", true, false)
            ]);
        tables.Tables.Add(orders);

        const string canaryTop = "CANARY_TOP_VAL";
        const string canaryMin = "CANARY_MIN_VAL";
        const string canaryMax = "CANARY_MAX_VAL";
        const string canarySample = "CANARY_SAMPLE_VAL";

        var store = new FakeDiscoveryArtifactStore();
        var artifact = new DiscoveryArtifacts(
            orders.Table.Id,
            "tenant-a",
            SamplesJson: JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = 1,
                    ["ssn"] = canarySample,
                    ["cleared_secret"] = "CLEARED_SAMPLE_VAL",
                    ["denied_col"] = "DENIED_SAMPLE_VAL"
                }
            }),
            StatisticsJson: JsonSerializer.Serialize(new
            {
                table = new { approxRowCount = 500, sizeBytes = 4096 },
                columns = new Dictionary<string, object>
                {
                    ["id"] = new
                    {
                        nullable = false,
                        nullRatio = 0.0,
                        approxDistinctCount = 500,
                        minLength = 1,
                        maxLength = 4,
                        minValue = "1",
                        maxValue = "500",
                        topValues = new[] { "1", "2" }
                    },
                    ["ssn"] = new
                    {
                        nullable = false,
                        nullRatio = 0.05,
                        approxDistinctCount = 450,
                        minLength = 11,
                        maxLength = 11,
                        minValue = canaryMin,
                        maxValue = canaryMax,
                        topValues = new[] { canaryTop }
                    },
                    ["cleared_secret"] = new
                    {
                        nullable = true,
                        nullRatio = 0.1,
                        approxDistinctCount = 50,
                        minLength = 4,
                        maxLength = 16,
                        minValue = "CLEARED_MIN",
                        maxValue = "CLEARED_MAX",
                        topValues = new[] { "CLEARED_TOP" }
                    },
                    ["denied_col"] = new
                    {
                        nullRatio = 0.25,
                        topValues = new[] { "DENIED_TOP" }
                    }
                }
            }),
            ContentSha256: "sha1",
            UploadedBy: "up",
            UploadedAt: DateTimeOffset.UtcNow,
            SchemaVersion: 1);
        store.Artifacts[orders.Table.Id] = artifact;

        var capturingStore = new CapturingArtifactStore();
        capturingStore.Items[orders.Table.Id] = artifact;

        var restrictedPolicy = RestrictedScopePolicy.FromOptions(new RestrictedScopeOptions
        {
            Enabled = true,
            Scopes = ["Agent.Read"]
        });

        var pdp = new FakeAccessDecisionPoint(req =>
        {
            var isRestricted = req.Subject.Claims?.Any(c =>
                (c.Type == "scp" || c.Type == "scope") &&
                c.Value.Contains("Agent.Read", StringComparison.OrdinalIgnoreCase)) == true;

            var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["ssn"] = ColumnAccessLevel.Clear,
                ["cleared_secret"] = ColumnAccessLevel.Clear,
                ["denied_col"] = ColumnAccessLevel.Deny
            };

            return new TableAccessDecision(
                req.Object.Identifier,
                IsAllowed: true,
                ColumnAccess: colAccess,
                CombinedRowFilterSql: null,
                DeniedReasons: [])
            {
                ScopeClearance = isRestricted
                    ? new RestrictedScopeClearance(
                        new HashSet<string>(["Agent.Read"], StringComparer.OrdinalIgnoreCase),
                        new HashSet<string>(["cleared_secret"], StringComparer.OrdinalIgnoreCase))
                    : null
            };
        });

        var discovery = DiscoveryTestBuilder.CreateService(tables: tables, pdp: pdp, artifacts: store);
        var artifactService = new DiscoveryArtifactService(
            tables,
            capturingStore,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            decisionPoint: pdp,
            restrictedScopes: restrictedPolicy);
        var mcp = new McpDatasetCatalog(
            tables,
            new FakeDiscoveryConsentRepository(),
            Substitute.For<IGatewayExecutionService>(),
            discoveryService: discovery);

        var restrictedUser = DiscoveryTestBuilder.User("S-AGENT", "tenant-a", scopes: "api://app/Agent.Read");
        var restrictedCaller = DiscoveryTestBuilder.Caller(restrictedUser, new TenantId("tenant-a"));

        // 1. REST Discovery statistics: non-value stats remain served, value-bearing members withheld for non-Clear columns
        var restObj = await discovery.GetAsync(orders.Identifier, restrictedCaller);
        restObj.ShouldNotBeNull();
        restObj!.SamplesAvailable.ShouldBeTrue();
        var ssnRest = restObj.Columns.Single(c => c.Name == "ssn");
        ssnRest.State.ShouldBe(DiscoveryColumnState.Masked);
        ssnRest.Statistics.ShouldNotBeNull();
        ssnRest.Statistics!.Nullable.ShouldBe(false);
        ssnRest.Statistics.NullRatio.ShouldBe(0.05);
        ssnRest.Statistics.ApproxDistinctCount.ShouldBe(450L);
        ssnRest.Statistics.MinLength.ShouldBe(11);
        ssnRest.Statistics.MaxLength.ShouldBe(11);
        ssnRest.Statistics.MinValue.ShouldBeNull();
        ssnRest.Statistics.MaxValue.ShouldBeNull();
        ssnRest.Statistics.TopValues.ShouldBeNull();

        var clearedRest = restObj.Columns.Single(c => c.Name == "cleared_secret");
        clearedRest.State.ShouldBe(DiscoveryColumnState.Clear);
        clearedRest.Statistics!.MinValue.ShouldBe("CLEARED_MIN");
        clearedRest.Statistics.MaxValue.ShouldBe("CLEARED_MAX");
        clearedRest.Statistics.TopValues.ShouldBe(["CLEARED_TOP"]);

        var idRest = restObj.Columns.Single(c => c.Name == "id");
        idRest.State.ShouldBe(DiscoveryColumnState.Clear);
        idRest.Statistics!.MinValue.ShouldBe("1");
        idRest.Statistics.TopValues.ShouldBe(["1", "2"]);

        restObj.Columns.Single(c => c.Name == "denied_col").Statistics.ShouldBeNull();

        // 2. GraphQL Discovery statistics
        var gqlDto = Query.ToTableMetadataDto(restObj);
        var ssnGql = gqlDto.Columns.Single(c => c.Name == "ssn");
        ssnGql.Statistics!.MinValue.ShouldBeNull();
        ssnGql.Statistics.MaxValue.ShouldBeNull();
        ssnGql.Statistics.TopValues.ShouldBeNull();
        ssnGql.Statistics.NullRatio.ShouldBe(0.05);

        // 3. MCP Discovery value profile
        var mcpDesc = await mcp.DescribeDatasetAsync(restrictedUser, orders.Identifier.ToString());
        mcpDesc.ShouldNotBeNull();
        var ssnMcp = mcpDesc!.Columns.Single(c => c.Name == "ssn");
        ssnMcp.ValueProfile.ShouldNotBeNull();
        ssnMcp.ValueProfile!.MinValue.ShouldBeNull();
        ssnMcp.ValueProfile.MaxValue.ShouldBeNull();
        ssnMcp.ValueProfile.AllowedValues.ShouldBeNull();
        ssnMcp.ValueProfile.EmptyShare.ShouldBe(0.05);
        ssnMcp.ValueProfile.TypicalLength.ShouldBe(11);
        ssnMcp.ValueProfile.ApproximateDistinctCount.ShouldBe(450L);

        var serializedMcp = JsonSerializer.Serialize(mcpDesc);
        serializedMcp.ShouldNotContain(canaryTop);
        serializedMcp.ShouldNotContain(canaryMin);
        serializedMcp.ShouldNotContain(canaryMax);
        serializedMcp.ShouldNotContain(canarySample);

        // 4. REST Samples: only Clear columns are served; masked/denied columns are stripped
        var restSamples = await artifactService.GetSamplesAsync(restrictedUser, orders.Identifier.ToString(), surface: DiscoverySurface.Rest);
        restSamples.Outcome.ShouldBe(DiscoveryArtifactOutcome.Ok);
        restSamples.Payload!.Available.ShouldBeTrue();
        restSamples.Payload.Samples.Count.ShouldBe(1);
        restSamples.Payload.Samples[0].ContainsKey("id").ShouldBeTrue();
        restSamples.Payload.Samples[0].ContainsKey("cleared_secret").ShouldBeTrue();
        restSamples.Payload.Samples[0].ContainsKey("ssn").ShouldBeFalse();
        restSamples.Payload.Samples[0].ContainsKey("denied_col").ShouldBeFalse();

        // 5. GraphQL Samples: no canary in serialized sample output
        var gqlAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = restrictedUser } };
        var gqlSamples = await new Query().GetCatalogSamplesAsync("sales", "dbo", "orders", limit: 5, artifactService, gqlAccessor);
        gqlSamples.Available.ShouldBeTrue();
        var gqlSamplesJson = JsonSerializer.Serialize(gqlSamples);
        gqlSamplesJson.ShouldNotContain(canarySample);
        gqlSamplesJson.ShouldNotContain("DENIED_SAMPLE_VAL");
        gqlSamplesJson.ShouldContain("CLEARED_SAMPLE_VAL");

        // 6. Unrestricted caller still receives full value-bearing artifacts
        var normalUser = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var normalObj = await discovery.GetAsync(orders.Identifier, DiscoveryTestBuilder.Caller(normalUser));
        normalObj!.Columns.Single(c => c.Name == "ssn").Statistics!.TopValues.ShouldBe([canaryTop]);
        var normalSamples = await artifactService.GetSamplesAsync(normalUser, orders.Identifier.ToString());
        normalSamples.Payload!.Samples[0].ContainsKey("ssn").ShouldBeTrue();
    }

    [Fact]
    public async Task RestrictedScope_AllColumnsMaskedOrDenied_ReturnsSamplesUnavailable_AndFailsClosedWithoutPdp()
    {
        var tables = new FakeDiscoveryTableRepository();
        var secrets = DiscoveryTestBuilder.MakeTable(
            "sales",
            "secrets",
            ownerTenantId: "tenant-a",
            columns: [("ssn", true, false)]);
        tables.Tables.Add(secrets);

        var artifact = new DiscoveryArtifacts(
            secrets.Table.Id,
            "tenant-a",
            SamplesJson: """[{"ssn":"CANARY_ONLY_MASKED"}]""",
            StatisticsJson: JsonSerializer.Serialize(new
            {
                columns = new Dictionary<string, object>
                {
                    ["ssn"] = new { nullRatio = 0.0, minValue = "CANARY", topValues = new[] { "CANARY" } }
                }
            }),
            ContentSha256: "sha2",
            UploadedBy: "up",
            UploadedAt: DateTimeOffset.UtcNow,
            SchemaVersion: 1);

        var store = new FakeDiscoveryArtifactStore();
        store.Artifacts[secrets.Table.Id] = artifact;
        var capturingStore = new CapturingArtifactStore();
        capturingStore.Items[secrets.Table.Id] = artifact;

        var pdp = new FakeAccessDecisionPoint(req => new TableAccessDecision(
            req.Object.Identifier,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = ColumnAccessLevel.Mask
            },
            CombinedRowFilterSql: null,
            DeniedReasons: [])
        {
            ScopeClearance = new RestrictedScopeClearance(
                new HashSet<string>(["Agent.Read"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        });

        var discovery = DiscoveryTestBuilder.CreateService(tables: tables, pdp: pdp, artifacts: store);
        var restrictedUser = DiscoveryTestBuilder.User("S-AGENT", "tenant-a", scopes: "Agent.Read");
        var obj = await discovery.GetAsync(secrets.Identifier, DiscoveryTestBuilder.Caller(restrictedUser));
        obj.ShouldNotBeNull();
        obj!.SamplesAvailable.ShouldBeFalse();
        obj.Columns.Single(c => c.Name == "ssn").Statistics!.TopValues.ShouldBeNull();
        obj.Columns.Single(c => c.Name == "ssn").Statistics!.MinValue.ShouldBeNull();
        obj.Columns.Single(c => c.Name == "ssn").Statistics!.NullRatio.ShouldBe(0.0);

        var withPdp = new DiscoveryArtifactService(
            tables,
            capturingStore,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            decisionPoint: pdp);
        var resWithPdp = await withPdp.GetSamplesAsync(restrictedUser, secrets.Identifier.ToString());
        resWithPdp.Payload!.Available.ShouldBeFalse();
        resWithPdp.Payload.Samples.ShouldBeEmpty();

        // Fail-closed when IAccessDecisionPoint is not provided for a restricted-scope caller
        var withoutPdp = new DiscoveryArtifactService(
            tables,
            capturingStore,
            new FakeDiscoveryOwnershipRepository(),
            new FakeAuditWriter(),
            new GatewayRoleEvaluator(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()));
        var resWithoutPdp = await withoutPdp.GetSamplesAsync(restrictedUser, secrets.Identifier.ToString());
        resWithoutPdp.Payload!.Available.ShouldBeFalse();
        resWithoutPdp.Payload.Samples.ShouldBeEmpty();
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

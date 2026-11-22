namespace Autheris.Modules.Dbt.Tests;

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
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Modules.Dbt;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Golden queries from dbt <c>meta.golden_queries</c>: validation, sanitising, caps, replacement per dataset and zero-value diagnostics.</summary>
public sealed class GoldenQueryMetaImportTests
{
    private const string TenantA = "tenant-a";
    private static readonly TableIdentifier OrdersId = new("warehouse", "dbo", "orders");
    private static readonly Guid OrdersTableId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Valid_entries_imported()
    {
        var fixture = new Fixture();
        var entries = new[]
        {
            Entry("Open orders", "{ orders { id amount } }", description: "All open orders.", variables: """{ "limit": 10 }"""),
            Entry("Orders by id", "query ById { orders(filter: { id: 1 }) { id } }")
        };

        var result = await fixture.SyncAsync(Manifest(entries));

        result.Warnings.ShouldBeEmpty();
        var stored = fixture.Store.Stored(TenantA, OrdersTableId.ToString());
        stored.Count.ShouldBe(2);
        stored.Select(e => e.Title).ShouldBe(["Open orders", "Orders by id"], ignoreOrder: true);
        stored.Single(e => e.Title == "Open orders").VariablesJson.ShouldBe("""{"limit":10}""");
        stored.Select(e => e.SourceKey).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task Removed_in_dbt_removed_after_sync()
    {
        var fixture = new Fixture();
        await fixture.SyncAsync(Manifest([Entry("Open orders", "{ orders { id } }")]));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Count.ShouldBe(1);

        await fixture.SyncAsync(Manifest(null));

        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Unchanged_declaration_is_not_rewritten()
    {
        var fixture = new Fixture();
        var manifest = Manifest([Entry("Open orders", "{ orders { id } }")]);
        await fixture.SyncAsync(manifest);
        await fixture.SyncAsync(manifest);
        await fixture.SyncAsync(Manifest(null));
        await fixture.SyncAsync(Manifest(null));

        fixture.Store.ReplaceCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Empty_query_rejected_with_reason_code()
    {
        var fixture = new Fixture();

        var result = await fixture.SyncAsync(Manifest([Entry("Broken", "   "), Entry("Fine", "{ orders { id } }")]));

        result.Warnings.ShouldContain(w => w.Contains("entry 0") && w.Contains("QUERY_EMPTY"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Select(e => e.Title).ShouldBe(["Fine"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Query_over_other_dataset_rejected()
    {
        var fixture = new Fixture();

        var result = await fixture.SyncAsync(Manifest(
        [
            Entry("Foreign", "{ payroll { salary } }"),
            Entry("Mixed", "{ orders { id } payroll { salary } }"),
            Entry("Introspection", "{ __schema { types { name } } }"),
            Entry("Mutation", "mutation { orders { id } }"),
            Entry("Subscription", "subscription { orders { id } }"),
            Entry("Own", "{ orders { id } }")
        ]));

        var reasons = result.Warnings.Where(w => w.Contains("golden query entry")).ToList();
        reasons.ShouldContain(w => w.Contains("entry 0") && w.Contains("QUERY_FOREIGN_DATASET"));
        reasons.ShouldContain(w => w.Contains("entry 1") && w.Contains("QUERY_FOREIGN_DATASET"));
        reasons.ShouldContain(w => w.Contains("entry 2") && w.Contains("QUERY_FOREIGN_DATASET"));
        reasons.ShouldContain(w => w.Contains("entry 3") && w.Contains("QUERY_NOT_QUERY_OPERATION"));
        reasons.ShouldContain(w => w.Contains("entry 4") && w.Contains("QUERY_NOT_QUERY_OPERATION"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Select(e => e.Title).ShouldBe(["Own"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Invalid_graphql_and_over_complex_queries_rejected()
    {
        var fixture = new Fixture();
        var deep = "{ orders " + string.Concat(Enumerable.Repeat("{ a ", 12)) + "{ id }" + string.Concat(Enumerable.Repeat(" }", 12)) + " }";

        var result = await fixture.SyncAsync(Manifest([Entry("Syntax", "{ orders { id "), Entry("Deep", deep)]));

        result.Warnings.ShouldContain(w => w.Contains("entry 0") && w.Contains("QUERY_SYNTAX"));
        result.Warnings.ShouldContain(w => w.Contains("entry 1") && w.Contains("QUERY_TOO_COMPLEX"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Fragments_directives_and_deep_nesting_rejected()
    {
        var fixture = new Fixture();
        var braces = new string('{', 40) + new string('}', 40);

        var result = await fixture.SyncAsync(Manifest(
        [
            Entry("Fragment", "{ orders { ...F } } fragment F on Order { id }"),
            Entry("Inline", "{ orders { ... on Order { id } } }"),
            Entry("Directive", "{ orders { id @include(if: false) } }"),
            Entry("Operation directive", "query Q @live { orders { id } }"),
            Entry("Braces", braces),
            Entry("Alias own", "{ o: orders { identifier: id } }"),
            Entry("Alias foreign", "{ o: payroll { id } }"),
            Entry("Connection", "{ ordersConnection { edges { node { id } } } }")
        ]));

        result.Warnings.ShouldContain(w => w.Contains("entry 0") && w.Contains("QUERY_FRAGMENTS_NOT_ALLOWED"));
        result.Warnings.ShouldContain(w => w.Contains("entry 1") && w.Contains("QUERY_FRAGMENTS_NOT_ALLOWED"));
        result.Warnings.ShouldContain(w => w.Contains("entry 2") && w.Contains("QUERY_DIRECTIVES_NOT_ALLOWED"));
        result.Warnings.ShouldContain(w => w.Contains("entry 3") && w.Contains("QUERY_DIRECTIVES_NOT_ALLOWED"));
        result.Warnings.ShouldContain(w => w.Contains("entry 4") && w.Contains("QUERY_TOO_COMPLEX"));
        result.Warnings.ShouldContain(w => w.Contains("entry 6") && w.Contains("QUERY_FOREIGN_DATASET"));
        result.Warnings.ShouldContain(w => w.Contains("entry 7") && w.Contains("QUERY_CONNECTION_ROOT_NOT_ALLOWED"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Select(e => e.Title).ShouldBe(["Alias own"]);
    }

    [Fact]
    public async Task Oversized_text_rejected()
    {
        var fixture = new Fixture();
        var entries = new[]
        {
            Entry(new string('t', 257), "{ orders { id } }"),
            Entry("Long description", "{ orders { id } }", description: new string('d', 2049)),
            Entry("Big query", "{ orders { id } } # " + new string('q', 16 * 1024)),
            Entry("Big variables", "{ orders { id } }", variables: JsonSerializer.Serialize(new { v = new string('x', 5000) })),
            Entry("Nested variables", "{ orders { id } }", variables: """{ "a": { "b": { "c": { "d": 1 } } } }"""),
            Entry("Ok", "{ orders { id } }", variables: """{ "a": { "b": 1 } }""")
        };

        var result = await fixture.SyncAsync(Manifest(entries));

        result.Warnings.ShouldContain(w => w.Contains("entry 0") && w.Contains("TITLE_TOO_LONG"));
        result.Warnings.ShouldContain(w => w.Contains("entry 1") && w.Contains("DESCRIPTION_TOO_LONG"));
        result.Warnings.ShouldContain(w => w.Contains("entry 2") && w.Contains("QUERY_TOO_LARGE"));
        result.Warnings.ShouldContain(w => w.Contains("entry 3") && w.Contains("VARIABLES_TOO_LARGE"));
        result.Warnings.ShouldContain(w => w.Contains("entry 4") && w.Contains("VARIABLES_INVALID"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Select(e => e.Title).ShouldBe(["Ok"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Warning_contains_no_values()
    {
        var fixture = new Fixture();
        const string secretTitle = "SECRET-TITLE-VALUE";
        const string secretDescription = "SECRET-DESCRIPTION-VALUE";
        const string secretQuery = "SECRETQUERYFIELD";
        const string secretVariable = "SECRET-VARIABLE-VALUE";
        var entries = new[]
        {
            Entry(secretTitle, "{ " + secretQuery + " { id } }", description: secretDescription, variables: $$"""{ "v": "{{secretVariable}}" }"""),
            Entry(secretTitle + "-2", "mutation { orders { id } }", description: secretDescription),
            Entry(secretTitle + "-3", "{ orders { id", description: secretDescription, variables: $$"""{ "v": "{{secretVariable}}" """),
            Entry(new string('x', 300) + secretTitle, "{ orders { id } }")
        };

        var result = await fixture.SyncAsync(Manifest(entries));

        var everything = string.Join('\n', result.Warnings.Concat(fixture.Logger.Messages));
        result.Warnings.Count(w => w.Contains("golden query entry")).ShouldBe(4);
        foreach (var secret in new[] { secretTitle, secretDescription, secretQuery, secretVariable })
        {
            everything.ShouldNotContain(secret);
        }

        fixture.Logger.Messages.ShouldContain(m => m.Contains("GOLDEN_QUERY_ENTRY_SKIPPED") && m.Contains("entry 0"));
    }

    [Fact]
    public async Task More_than_20_entries_truncated_with_warning()
    {
        var fixture = new Fixture();
        var entries = Enumerable.Range(0, 25).Select(i => Entry($"Entry {i}", "{ orders { id } }")).ToArray();

        var result = await fixture.SyncAsync(Manifest(entries));

        result.Warnings.ShouldContain(w => w.Contains("GOLDEN_QUERIES_TRUNCATED"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Count.ShouldBe(20);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Text_is_sanitised_before_storage()
    {
        var fixture = new Fixture();

        await fixture.SyncAsync(Manifest([Entry("Ti\u200Btle\u202E", "{ orders { id } }", description: "Des\u0000c\u200Bription", variables: """{ "k\u200B": "va\u202Elue" }""")]));

        var stored = fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Single();
        stored.Title.ShouldBe("Title");
        stored.Description.ShouldBe("Description");
        stored.VariablesJson.ShouldBe("""{"k":"value"}""");
    }

    [Fact]
    public async Task Duplicate_titles_keep_the_first_entry()
    {
        var fixture = new Fixture();

        var result = await fixture.SyncAsync(Manifest([Entry("Same", "{ orders { id } }"), Entry("same", "{ orders { amount } }")]));

        result.Warnings.ShouldContain(w => w.Contains("entry 1") && w.Contains("TITLE_DUPLICATE"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Single().QueryText.ShouldBe("{ orders { id } }");
    }

    [Fact]
    public async Task Declaration_that_is_not_a_list_leaves_stored_entries_untouched()
    {
        var fixture = new Fixture();
        await fixture.SyncAsync(Manifest([Entry("Open orders", "{ orders { id } }")]));

        var result = await fixture.SyncAsync(ManifestRaw("""{ "title": "not a list" }"""));

        result.Warnings.ShouldContain(w => w.Contains("GOLDEN_QUERIES_INVALID_SHAPE"));
        fixture.Store.Stored(TenantA, OrdersTableId.ToString()).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Entries_are_stored_under_the_table_owner_tenant()
    {
        var fixture = new Fixture();

        await fixture.SyncAsync(Manifest([Entry("Open orders", "{ orders { id } }")]));

        fixture.Store.Tenants.ShouldBe([TenantA]);
    }

    [Fact]
    public async Task Without_repository_nothing_is_stored_and_sync_succeeds()
    {
        var fixture = new Fixture(withStore: false);

        var result = await fixture.SyncAsync(Manifest([Entry("Open orders", "{ orders { id } }")]));

        result.Success.ShouldBeTrue();
        fixture.Store.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Tables_without_golden_queries_and_no_default_tenant_emit_zero_tenant_unresolved_warnings()
    {
        var fixture = new Fixture(ownerTenantId: null);
        fixture.Tables.Seed(new TableMetadata
        {
            Identifier = new TableIdentifier("warehouse", "dbo", "customers"),
            Table = new Table
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SourceName = "warehouse",
                SchemaName = "dbo",
                TableName = "customers",
                SourceType = "PostgreSql",
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"],
            OwnerTenantId = null
        });

        var manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": { "materialized": "table", "meta": {} },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            },
            "model.proj.customers": {
              "name": "customers", "database": "warehouse", "schema": "dbo", "description": "customers",
              "config": { "materialized": "table" },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        var result = await fixture.SyncAsync(manifest);

        result.Success.ShouldBeTrue();
        result.Warnings.ShouldNotContain(w => w.Contains("TENANT_UNRESOLVED", StringComparison.Ordinal));
        fixture.Logger.Messages.ShouldNotContain(m => m.Contains("TENANT_UNRESOLVED", StringComparison.Ordinal));
        fixture.Store.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Explicit_golden_queries_without_resolved_tenant_emit_tenant_unresolved_warning()
    {
        var fixture = new Fixture(ownerTenantId: null);
        fixture.Tables.Seed(new TableMetadata
        {
            Identifier = new TableIdentifier("warehouse", "dbo", "customers"),
            Table = new Table
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SourceName = "warehouse",
                SchemaName = "dbo",
                TableName = "customers",
                SourceType = "PostgreSql",
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"],
            OwnerTenantId = null
        });

        // customers omits meta.golden_queries -> skipped silently; orders explicitly declares golden_queries -> warns once for orders.
        var manifest = """
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": { "materialized": "table", "meta": { "golden_queries": [ { "title": "Open", "query": "{ orders { id } }" } ] } },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            },
            "model.proj.customers": {
              "name": "customers", "database": "warehouse", "schema": "dbo", "description": "customers",
              "config": { "materialized": "table", "meta": {} },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;

        var result = await fixture.SyncAsync(manifest);

        var unresolvedWarnings = result.Warnings.Where(w => w.Contains("TENANT_UNRESOLVED", StringComparison.Ordinal)).ToList();
        unresolvedWarnings.Count.ShouldBe(1);
        unresolvedWarnings[0].ShouldContain("orders");
        fixture.Logger.Messages.Count(m => m.Contains("TENANT_UNRESOLVED", StringComparison.Ordinal)).ShouldBe(1);
        fixture.Store.ReplaceCalls.ShouldBe(0);
    }

    [Fact]
    public void Meta_reader_distinguishes_absent_declaration_from_explicit_empty_or_null()
    {
        var absent = JsonDocument.Parse("""{ "config": { "meta": {} } }""").RootElement;
        var explicitEmpty = JsonDocument.Parse("""{ "config": { "meta": { "golden_queries": [] } } }""").RootElement;
        var explicitNull = JsonDocument.Parse("""{ "config": { "meta": { "golden_queries": null } } }""").RootElement;

        var collector = new RelationCollector();
        DbtGoldenQueryMetaReader.Read(absent, "model.a", collector).HasDeclaration.ShouldBeFalse();
        DbtGoldenQueryMetaReader.Read(explicitEmpty, "model.b", collector).HasDeclaration.ShouldBeTrue();
        DbtGoldenQueryMetaReader.Read(explicitNull, "model.c", collector).HasDeclaration.ShouldBeTrue();
    }

    private static (string Title, string Query, string? Description, string? Variables) Entry(string title, string query, string? description = null, string? variables = null) =>
        (title, query, description, variables);

    private static string Manifest((string Title, string Query, string? Description, string? Variables)[]? entries)
    {
        if (entries is null)
        {
            return ManifestRaw(null);
        }

        var json = entries.Select(e =>
        {
            var node = new Dictionary<string, object?> { ["title"] = e.Title, ["query"] = e.Query };
            if (e.Description is not null) node["description"] = e.Description;
            if (e.Variables is not null)
            {
                // A valid JSON object stays an object (as dbt YAML maps do); anything else is passed as the raw string.
                try { node["variables"] = JsonSerializer.Deserialize<JsonElement>(e.Variables); }
                catch (JsonException) { node["variables"] = e.Variables; }
            }

            return node;
        }).ToList();
        return ManifestRaw(JsonSerializer.Serialize(json));
    }

    private static string ManifestRaw(string? goldenQueriesJson)
    {
        var meta = goldenQueriesJson is null ? "{}" : $$"""{ "golden_queries": {{goldenQueriesJson}} }""";
        return $$"""
        {
          "metadata": { "dbt_version": "1.8.0" },
          "nodes": {
            "model.proj.orders": {
              "name": "orders", "database": "warehouse", "schema": "dbo", "description": "orders",
              "config": { "materialized": "table", "meta": {{meta}} },
              "columns": {
                "id": { "name": "id", "data_type": "int", "meta": {}, "tags": [] },
                "amount": { "name": "amount", "data_type": "int", "meta": {}, "tags": [] }
              },
              "depends_on": { "nodes": [] }
            }
          }
        }
        """;
    }

    private sealed class Fixture
    {
        public Fixture(bool withStore = true, string? ownerTenantId = TenantA)
        {
            Tables.Seed(new TableMetadata
            {
                Identifier = OrdersId,
                Table = new Table
                {
                    Id = OrdersTableId,
                    SourceName = OrdersId.Domain,
                    SchemaName = OrdersId.Schema,
                    TableName = OrdersId.TableName,
                    SourceType = "PostgreSql",
                    IsActive = true
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "amount", DataType = "int" }],
                PrimaryKeyColumns = ["id"],
                OwnerTenantId = ownerTenantId
            });
            Service = new DbtMetadataIngestionService(
                new InMemoryProposalRepo(), Tables, new InMemoryLineageStore(),
                epochRepository: null, gatewayOptions: null, sqlEndpointLoader: null, relationRepository: null, virtualFilterAdmin: null,
                tagImport: null, artifactStore: null, testTargetMap: null, logger: Logger,
                auditLog: Substitute.For<IAuditLogRepository>(),
                goldenQueryRepository: withStore ? Store : null);
        }

        public InMemoryTableRepo Tables { get; } = new();
        public FakeGoldenQueryRepository Store { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public DbtMetadataIngestionService Service { get; }

        public Task<DbtSyncResult> SyncAsync(string manifest)
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));
            return Service.IngestManifestStreamAsync(
                stream,
                new DbtGovernanceImportCaller(new Autheris.Application.VirtualFilters.VirtualFilterActor(new Sid("system:dbt-sync"), IsSync: true, null), Autheris.Tests.Common.TestTenants.Default, true),
                dryRun: false,
                CancellationToken.None);
        }
    }

    internal sealed class FakeGoldenQueryRepository : IGoldenQueryRepository
    {
        private readonly ConcurrentDictionary<(string Tenant, string TableId, GoldenQuerySource Source), IReadOnlyList<GoldenQueryStoreEntry>> _sets = new();
        private int _replaceCalls;

        public int ReplaceCalls => _replaceCalls;
        public IReadOnlyList<string> Tenants => _sets.Keys.Select(k => k.Tenant).Distinct().ToList();

        public IReadOnlyList<GoldenQueryStoreEntry> Stored(string tenant, string tableId) =>
            _sets.TryGetValue((tenant, tableId, GoldenQuerySource.Dbt), out var set) ? set : [];

        public Task ReplaceForTableAsync(TenantId tenant, string tableId, GoldenQuerySource source, IReadOnlyList<GoldenQueryStoreEntry> entries, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _replaceCalls);
            _sets[(tenant.Value, tableId, source)] = entries.ToList();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<GoldenQueryStoreRecord>> GetForTenantAsync(TenantId tenant, CancellationToken ct = default)
        {
            IReadOnlyList<GoldenQueryStoreRecord> rows = _sets
                .Where(kv => kv.Key.Tenant == tenant.Value)
                .SelectMany(kv => kv.Value.Select(e => new GoldenQueryStoreRecord(
                    Guid.NewGuid().ToString(), kv.Key.TableId, OrdersId.Domain, OrdersId.Schema, OrdersId.TableName, kv.Key.Source,
                    e.SourceKey, e.Title, e.Description, e.QueryText, e.VariablesJson, e.ComputeContentHash(), DateTimeOffset.UtcNow)))
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private sealed class InMemoryTableRepo : ITableMetadataRepository
    {
        private readonly ConcurrentDictionary<TableIdentifier, TableMetadata> _tables = new();

        public void Seed(TableMetadata metadata) => _tables[metadata.Identifier] = metadata;

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
                _tables[item.Identifier] = item;
                results.Add(new CatalogTableUpsertResult(item, CatalogUpsertKind.Updated, CatalogWriteClassification.Restrictive));
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

    internal sealed class RecordingLogger : ILogger<DbtMetadataIngestionService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                text += " | " + string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            Messages.Add(text);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

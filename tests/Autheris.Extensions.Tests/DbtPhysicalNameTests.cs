namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #666: the dbt sync registers the physical table (source identifier, model alias) and never lets an alias take over
/// an entry of another origin. The dbt unique id on the entry is internal to the catalog.
/// </summary>
public sealed class DbtPhysicalNameTests : IDisposable
{
    private readonly string _queries = Path.Combine(Path.GetTempPath(), "dbt_physical_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_queries, true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ manifest and harness

    private sealed record Node(string Name, string Kind = "model", string? Id = null, string Schema = "sales")
    {
        public Dictionary<string, object?> Extra { get; init; } = [];

        public string UniqueId => Id ?? (Kind == "source" ? $"source.proj.raw.{Name}" : $"{Kind}.proj.{Name}");

        public Node Alias(string? alias) => this with { Extra = new(Extra) { [Kind == "source" ? "identifier" : "alias"] = alias } };
    }

    private static Node Model(string name, string? alias = null, string? id = null) => alias is null ? new(name, "model", id) : new Node(name, "model", id).Alias(alias);

    private static Node Source(string name, string? identifier = null, string? id = null) => identifier is null ? new(name, "source", id) : new Node(name, "source", id).Alias(identifier);

    private static Dictionary<string, object?> Body(Node n, object[]? constraints = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = n.Name,
            ["database"] = "warehouse",
            ["schema"] = n.Schema,
            ["description"] = "from dbt",
            ["config"] = new { materialized = "table" },
            ["tags"] = Array.Empty<string>(),
            ["meta"] = new { },
            ["columns"] = new Dictionary<string, object> { ["id"] = new { name = "id", data_type = "varchar", tags = Array.Empty<string>(), meta = new { } } },
            ["depends_on"] = new { nodes = Array.Empty<string>() }
        };
        if (constraints is not null)
        {
            body["constraints"] = constraints;
        }

        foreach (var (k, v) in n.Extra)
        {
            body[k] = v;
        }

        return body;
    }

    private static MemoryStream Manifest(params Node[] nodes) => Manifest(nodes, null);

    private static MemoryStream Manifest(Node[] nodes, Dictionary<string, object[]>? constraintsByName)
    {
        var models = new Dictionary<string, object?>();
        var sources = new Dictionary<string, object?>();
        foreach (var n in nodes)
        {
            object[]? constraints = constraintsByName is not null && constraintsByName.TryGetValue(n.Name, out var c) ? c : null;
            (n.Kind == "source" ? sources : models)[n.UniqueId] = Body(n, constraints);
        }

        return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { metadata = new { dbt_version = "1.8.0" }, nodes = models, sources }));
    }

    private static GatewayOptions Options(string? queries = null, bool create = true)
    {
        var sources = new SqlDataSourceOptions();
        sources.Connections["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" };
        return new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = create },
            DataSources = sources,
            SqlEndpoints = queries is null ? new SqlEndpointsOptions() : new SqlEndpointsOptions { Enabled = true, AutoSyncFromDbt = true, Directory = queries }
        };
    }

    private sealed class Harness
    {
        public Dictionary<string, TableMetadata> Store { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TableMetadata> Upserts { get; } = [];
        public List<TableRelation> Written { get; } = [];
        public IDbtProposalRepository Proposals { get; } = Substitute.For<IDbtProposalRepository>();
        public DbtMetadataIngestionService Service { get; }

        public Harness(GatewayOptions options)
        {
            var repo = Substitute.For<ITableMetadataRepository>();
            repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Store.GetValueOrDefault(call.Arg<TableIdentifier>().ToString())));
            repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var m = call.Arg<TableMetadata>();
                    Upserts.Add(m);
                    Store[m.Identifier.ToString()] = m;
                    return Task.FromResult(m);
                });
            Proposals.GetPendingProposalsAsync(Arg.Any<TableIdentifier?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<DbtMetadataProposal>>([]));
            var relations = Substitute.For<ITableRelationRepository>();
            relations.CreateRelationAsync(Arg.Any<TableRelation>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var r = call.Arg<TableRelation>();
                    Written.Add(r);
                    return Task.FromResult(new RelationWriteResult(r.Id, true, false));
                });
            Service = new DbtMetadataIngestionService(
                Proposals, repo, Substitute.For<ILineageGraphStore>(), null,
                Microsoft.Extensions.Options.Options.Create(options),
                new SqlEndpointLoader(new InMemorySqlEndpointRegistry(), NullLogger<SqlEndpointLoader>.Instance),
                relations, null, null, NullLogger<DbtMetadataIngestionService>.Instance);
        }

        /// <summary>An entry as another origin (or an administrator) registered it; <paramref name="uniqueId"/> null means none recorded.</summary>
        public Table Seed(string table, string? uniqueId, string displayName = "", string description = "kept", string schema = "sales")
        {
            var id = new TableIdentifier("warehouse", schema, table);
            var entry = new Table
            {
                SourceName = "warehouse", SourceType = "PostgreSql", SchemaName = schema, TableName = table,
                DisplayName = displayName, Description = description, DbtUniqueId = uniqueId
            };
            Store[id.ToString()] = new TableMetadata
            {
                Identifier = id,
                Table = entry,
                Columns = [new TableColumn { Id = Guid.NewGuid(), TableId = entry.Id, ColumnName = "id", DataType = "varchar" }]
            };
            return entry;
        }

        public Task<DbtSyncResult> Sync(Stream manifest, bool dryRun = false) =>
            Service.IngestManifestStreamAsync(
                manifest,
                new DbtGovernanceImportCaller(new Autheris.Application.VirtualFilters.VirtualFilterActor(new Sid("system:dbt-sync"), IsSync: true, null), Autheris.Tests.Common.TestTenants.Default, true),
                dryRun);
    }

    private static string Key(string table, string schema = "sales") => $"warehouse.{schema}.{table}";

    // ------------------------------------------------------------------ parser and registration

    [Fact]
    public async Task Source_WithIdentifier_RegistersIdentifierAsTable()
    {
        var h = new Harness(Options());

        var result = await h.Sync(Manifest(Source("orders_raw", identifier: "ord_2024")));

        result.CreatedTables.ShouldHaveSingleItem();
        h.Store.Keys.ShouldBe([Key("ord_2024")]);
        var table = h.Store[Key("ord_2024")].Table;
        table.TableName.ShouldBe("ord_2024");
        table.DisplayName.ShouldBe("orders_raw");
        table.DbtUniqueId.ShouldBe("source.proj.raw.orders_raw");
    }

    [Fact]
    public async Task Source_WithoutIdentifier_FallsBackToName()
    {
        var h = new Harness(Options());

        await h.Sync(Manifest(Source("orders_raw")));

        h.Store.Keys.ShouldBe([Key("orders_raw")]);
        h.Store[Key("orders_raw")].Table.DbtUniqueId.ShouldBe("source.proj.raw.orders_raw");
    }

    [Fact]
    public async Task Model_WithAlias_RegistersAliasAsTable()
    {
        var h = new Harness(Options());

        await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        h.Store.Keys.ShouldBe([Key("ord_2024")]);
        var table = h.Store[Key("ord_2024")].Table;
        table.TableName.ShouldBe("ord_2024");
        table.DisplayName.ShouldBe("orders");
        table.DbtUniqueId.ShouldBe("model.proj.orders");
    }

    [Fact]
    public async Task Seed_WithAlias_UsesAlias()
    {
        var seed = new Node("customer_master", "seed").Alias("cust_master");
        using var stream = Manifest(seed);

        var models = await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream);

        var parsed = models.ShouldHaveSingleItem();
        parsed.Name.ShouldBe("customer_master");
        parsed.PhysicalName.ShouldBe("cust_master");
        parsed.ToTableIdentifier().TableName.ShouldBe("cust_master");
    }

    [Fact]
    public async Task DisplayName_SetToLogicalName_WhenDifferent()
    {
        var h = new Harness(Options());
        h.Seed("ord_2024", "model.proj.orders", displayName: "");

        await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        h.Store[Key("ord_2024")].Table.DisplayName.ShouldBe("orders");
    }

    // ------------------------------------------------------------------ SEC-OI-666-1 no takeover of another origin

    [Fact]
    [Trait("Category", "Security")]
    public async Task Alias_EqualToExistingEntryOfOtherOrigin_IsRejected()
    {
        var h = new Harness(Options());
        h.Seed("orders", "model.other.orders");

        var result = await h.Sync(Manifest(Model("reports", alias: "orders")));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.PhysicalNameCollision);
        result.SkippedItems[0].Name.ShouldBe("model.proj.reports");
        h.Upserts.ShouldBeEmpty();
        h.Store[Key("orders")].Table.Description.ShouldBe("kept");
        h.Store[Key("orders")].Table.DbtUniqueId.ShouldBe("model.other.orders");
        await h.Proposals.DidNotReceiveWithAnyArgs().AddProposalAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Alias_OntoEntryWithoutUniqueId_IsRejected()
    {
        var h = new Harness(Options());
        h.Seed("orders", null);

        var result = await h.Sync(Manifest(Model("reports", alias: "orders")));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.PhysicalNameCollision);
        h.Upserts.ShouldBeEmpty();
        h.Store[Key("orders")].Table.DbtUniqueId.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NoAlias_EntryOfOtherUniqueId_IsRejected()
    {
        var h = new Harness(Options());
        h.Seed("orders", "model.other.orders");

        var result = await h.Sync(Manifest(Model("orders")));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.PhysicalNameCollision);
        h.Upserts.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoAlias_EntryWithoutUniqueId_IsUpdatedAndRecordsUniqueId()
    {
        var h = new Harness(Options());
        h.Seed("orders", null);

        var result = await h.Sync(Manifest(Model("orders")));

        result.SkippedItems.ShouldBeEmpty();
        var stored = h.Upserts.ShouldHaveSingleItem();
        stored.Table.Description.ShouldBe("from dbt");
        stored.Table.DbtUniqueId.ShouldBe("model.proj.orders");
    }

    [Fact]
    public async Task SameUniqueId_IsUpdated()
    {
        var h = new Harness(Options());
        h.Seed("ord_2024", "model.proj.orders");

        var result = await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        result.SkippedItems.ShouldBeEmpty();
        h.Upserts.ShouldHaveSingleItem().Table.DbtUniqueId.ShouldBe("model.proj.orders");
        h.Store[Key("ord_2024")].Table.Description.ShouldBe("from dbt");
    }

    // ------------------------------------------------------------------ SEC-OI-666-2 collisions in one artifact

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Alias_CollisionBetweenTwoNodes_RejectsBoth_OrderIndependent(bool reversed)
    {
        var h = new Harness(Options());
        Node[] nodes = [Model("orders", alias: "ord_2024"), Model("ord_2024"), Model("customers")];
        if (reversed)
        {
            Array.Reverse(nodes);
        }

        var result = await h.Sync(Manifest(nodes));

        result.SkippedItems.Where(s => s.Reason == DbtSyncSkipReasons.PhysicalNameCollision).Select(s => s.Name)
            .Order().ShouldBe(["model.proj.ord_2024", "model.proj.orders"]);
        h.Store.Keys.ShouldBe([Key("customers")]);
        result.ParsedModelsCount.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PhysicalName_DoesNotCollapseTwoSourcesOntoOneCatalogEntry_ReportsCollision()
    {
        var h = new Harness(Options());

        var result = await h.Sync(Manifest(
            Source("customers_a", identifier: "customer_master", id: "source.proj.crm.customers_a"),
            Source("customers_b", identifier: "customer_master", id: "source.proj.erp.customers_b")));

        result.SkippedItems.Count(s => s.Reason == DbtSyncSkipReasons.PhysicalNameCollision).ShouldBe(2);
        result.CreatedTables.ShouldBeEmpty();
        h.Store.ShouldBeEmpty();
    }

    // ------------------------------------------------------------------ SEC-OI-666-3 validation and quoting

    [Theory]
    [Trait("Category", "Security")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Alias_NullOrEmpty_FallsBackButInvalid_Rejects(string? alias)
    {
        using var stream = Manifest(Model("orders").Alias(alias));
        var parsed = (await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream)).ShouldHaveSingleItem();
        parsed.PhysicalName.ShouldBe("orders", "an absent, null or blank alias falls back to the name");

        var h = new Harness(Options());
        var result = await h.Sync(Manifest(Model("orders").Alias("bad.alias")));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("bad alias")]
    [InlineData("ord;drop")]
    [InlineData("a\"b")]
    [InlineData("bad.alias")]
    public async Task Alias_WithInvalidIdentifier_RejectsNode_NoFallbackToName(string alias)
    {
        var h = new Harness(Options());

        var result = await h.Sync(Manifest(Model("orders", alias: alias), Model("customers")));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.InvalidIdentifier);
        h.Store.Keys.ShouldBe([Key("customers")]); // the node is not registered under its name either
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a\";b")]
    [InlineData("ord;drop")]
    [InlineData("ord 2024")]
    public void Identifier_WithQuoteOrSemicolon_NeverReachesGeneratedSql(string table)
    {
        var loader = new SqlEndpointLoader(new InMemorySqlEndpointRegistry(), NullLogger<SqlEndpointLoader>.Instance);

        Should.Throw<ArgumentException>(() => loader.SyncDbtModelDefinitionToFile(_queries, "orders", "sales", ["id"], table: table));

        (Directory.Exists(_queries) ? Directory.GetFiles(_queries) : []).ShouldBeEmpty();
    }

    [Fact]
    public async Task SqlEndpointAutoSync_SelectsFromPhysicalName()
    {
        var h = new Harness(Options(_queries));

        await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        var sql = File.ReadAllText(Path.Combine(_queries, "orders.sql"));
        sql.ShouldContain("FROM \"sales\".\"ord_2024\"");
        sql.ShouldNotContain("\"orders\"");
    }

    // ------------------------------------------------------------------ SEC-OI-666-4 relations and name fallback

    private static DbtModelDefinition Def(string uniqueId, string name, string schema, string? physical = null) =>
        new(uniqueId, name, "warehouse", schema, "table", null, [], new Dictionary<string, string>(), new Dictionary<string, DbtColumnDefinition>(), [], false)
        {
            PhysicalName = physical!
        };

    private static Dictionary<string, object[]> Fk(string childName, string parentRef) => new()
    {
        [childName] = [new Dictionary<string, object> { ["type"] = "foreign_key", ["columns"] = new[] { "id" }, ["to"] = $"ref('{parentRef}')", ["to_columns"] = new[] { "id" } }]
    };

    [Fact]
    public async Task Relationship_ByUniqueId_ResolvesToPhysicalNames()
    {
        var h = new Harness(Options());
        h.Seed("ord_2024", "model.proj.orders");
        h.Seed("lines_2024", "model.proj.lines");

        var result = await h.Sync(Manifest(
            [Model("orders", alias: "ord_2024"), Model("lines", alias: "lines_2024")],
            Fk("lines", "orders")));

        result.ImportedRelationsCount.ShouldBe(1);
        var rel = h.Written.ShouldHaveSingleItem();
        rel.ParentTableIdentifier.ShouldBe(new TableIdentifier("warehouse", "sales", "ord_2024"));
        rel.ChildTableIdentifier.ShouldBe(new TableIdentifier("warehouse", "sales", "lines_2024"));
    }

    [Fact]
    public void Relationship_ByNameFallback_MatchesLogicalOrPhysicalName()
    {
        var orders = Def("model.proj.orders", "orders", "sales", "ord_2024");
        var lines = Def("model.proj.lines", "lines", "sales");
        var lookup = new DbtRelationModelLookup([orders, lines]);

        lookup.TryResolve(new DbtRelationshipDefinition("r", "orders", "lines", ["id"], ["id"]), out var byLogical, out _).ShouldBeNull();
        lookup.TryResolve(new DbtRelationshipDefinition("r", "ord_2024", "lines", ["id"], ["id"]), out var byPhysical, out _).ShouldBeNull();

        byLogical.ShouldBe(orders);
        byPhysical.ShouldBe(orders);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Relation_ByNameFallback_Ambiguous_IsSkipped()
    {
        // "ord_2024" is the alias of one model and the plain name of another one in a different schema.
        var aliased = Def("model.proj.orders", "orders", "sales", "ord_2024");
        var plain = Def("model.proj.ord_2024", "ord_2024", "archive");
        var lines = Def("model.proj.lines", "lines", "sales");
        var lookup = new DbtRelationModelLookup([aliased, plain, lines]);

        var code = lookup.TryResolve(new DbtRelationshipDefinition("r", "ord_2024", "lines", ["id"], ["id"]), out var parent, out _);

        code.ShouldBe("AMBIGUOUS_MODEL");
        parent.ShouldBeNull();
        lookup.TryResolve(new DbtRelationshipDefinition("r", "orders", "lines", ["id"], ["id"]), out var unique, out _).ShouldBeNull();
        unique.ShouldBe(aliased);
    }

    [Fact]
    public async Task ForeignKeyConstraint_ResolvesToPhysicalName()
    {
        var h = new Harness(Options());
        h.Seed("ord_2024", "model.proj.orders");
        h.Seed("lines", null);

        await h.Sync(Manifest([Model("orders", alias: "ord_2024"), Model("lines")], Fk("lines", "orders")));

        h.Written.ShouldHaveSingleItem().ParentTableIdentifier.TableName.ShouldBe("ord_2024");
    }

    // ------------------------------------------------------------------ SEC-OI-666-5 warnings

    [Fact]
    public async Task Sync_ReportsPhysicalNameChanged_ForPreviouslyRegisteredLogicalName()
    {
        var h = new Harness(Options());
        h.Seed("orders", null);

        var result = await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        result.Warnings.ShouldContain(w => w.Contains("PHYSICAL_NAME_CHANGED") && w.Contains("orders") && w.Contains("ord_2024"));
        h.Store.ContainsKey(Key("ord_2024")).ShouldBeTrue();
    }

    [Fact]
    public async Task OldLogicalEntry_IsNeitherChangedNorReactivated()
    {
        var h = new Harness(Options());
        var old = h.Seed("orders", null);
        h.Store[Key("orders")] = h.Store[Key("orders")] with { Table = old with { IsActive = false } };

        await h.Sync(Manifest(Model("orders", alias: "ord_2024")));

        var after = h.Store[Key("orders")].Table;
        after.IsActive.ShouldBeFalse();
        after.Description.ShouldBe("kept");
        after.DbtUniqueId.ShouldBeNull();
        h.Upserts.ShouldAllBe(m => m.Identifier.TableName == "ord_2024");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Warning_ContainsOnlySanitizedIdentifiers()
    {
        var h = new Harness(Options());
        h.Seed("orders", "model.other.orders");
        var longId = "model.proj.reports_" + new string('x', 200);

        var result = await h.Sync(Manifest(Model("reports", alias: "orders", id: longId)));

        result.SkippedItems.ShouldHaveSingleItem().Reason.ShouldBe(DbtSyncSkipReasons.PhysicalNameCollision);
        result.SkippedItems[0].Name.Length.ShouldBeLessThanOrEqualTo(DbtIdentifierSanitizer.MaxLogLength);
        result.Warnings.ShouldNotBeEmpty();
        result.Warnings.ShouldAllBe(w => !w.Contains('\r') && !w.Contains('\n') && !w.Contains(new string('x', DbtIdentifierSanitizer.MaxLogLength + 1)));
    }
}

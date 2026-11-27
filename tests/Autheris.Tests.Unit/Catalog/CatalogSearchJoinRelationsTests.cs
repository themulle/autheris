namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>Join relations of a search hit come from the stored table relations, filtered by the caller's own visibility.</summary>
public sealed class CatalogSearchJoinRelationsTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    private static readonly TableIdentifier Invoices = new("sales", "dbo", "invoices");
    private static readonly TableIdentifier Other = new("hr", "other", "client");
    private static readonly CatalogCaller Caller = new(null, new TenantId("tenant-a"), CatalogChannel.Rest);

    private static TableMetadata Tbl(TableIdentifier id, string[] columns, string[]? hidden = null, string source = "src", Dictionary<string, string>? fkMeta = null) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = true, SourceName = source },
        Columns = columns.Select(c => new TableColumn
        {
            ColumnName = c,
            DataType = "int",
            DiscoveryHidden = hidden?.Contains(c) == true,
            Meta = fkMeta is not null && fkMeta.TryGetValue(c, out var target)
                ? new Dictionary<string, string> { ["foreign_key_table"] = target, ["foreign_key_column"] = "id" }
                : new Dictionary<string, string>()
        }).ToList()
    };

    private static Dictionary<TableIdentifier, CatalogVisibleRow> Rows(params TableMetadata[] tables) =>
        tables.ToDictionary(t => t.Identifier, t => new CatalogVisibleRow(t, CatalogOutputShape.Full));

    private static IReadOnlySet<string> Own(params string[] columns) => new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);

    private static GraphQlRelationMapping Rel(TableIdentifier target, string own, string theirs, bool isList = false) =>
        new("f", target, isList, [own], [theirs]);

    private static IReadOnlyList<TableRelationship> Resolve(
        IReadOnlyList<GraphQlRelationMapping>? stored,
        IReadOnlyList<TableRelationship>? declared,
        IReadOnlyDictionary<TableIdentifier, CatalogVisibleRow> rows,
        IReadOnlySet<string> own,
        int max = 3) => CatalogDiscoveryService.ResolveJoinRelations(Orders, stored, declared, own, rows, max);

    private static List<TableRelationship> Naming(params (string Column, TableIdentifier Target)[] items) =>
        items.Select(i => new TableRelationship(Orders, i.Column, i.Target, i.Column, TableRelationshipType.SemanticInferred)).ToList();

    private static TableRelationship Meta(string column, TableIdentifier target) =>
        new(Orders, column, target, "id", TableRelationshipType.ForeignKey);

    [Fact]
    public void StoredOutgoingRelation_Listed()
    {
        var rows = Rows(Tbl(Orders, ["id", "customer_id"]), Tbl(Customers, ["id"]));

        var result = Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("id", "customer_id"));

        var r = result.ShouldHaveSingleItem();
        (r.FromTable, r.FromColumn, r.ToTable, r.ToColumn, r.Incoming).ShouldBe((Orders, "customer_id", Customers, "id", false));
    }

    [Fact]
    public void StoredIncomingRelation_ListedFromHitPerspective_Incoming()
    {
        var rows = Rows(Tbl(Orders, ["id"]), Tbl(Invoices, ["order_id"]));

        var r = Resolve([Rel(Invoices, "id", "order_id", isList: true)], null, rows, Own("id")).ShouldHaveSingleItem();

        (r.FromTable, r.FromColumn, r.ToTable, r.ToColumn, r.Incoming).ShouldBe((Orders, "id", Invoices, "order_id", true));
    }

    [Fact]
    public void CrossSchemaStoredRelation_Listed()
    {
        var rows = Rows(Tbl(Orders, ["client_id"]), Tbl(Other, ["client_id"]));

        var r = Resolve([Rel(Other, "client_id", "client_id")], null, rows, Own("client_id")).ShouldHaveSingleItem();

        r.ToTable.ShouldBe(Other);
    }

    [Fact]
    public void NoStoredRelations_NamingRuleFallback()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["customer_id"]));

        var r = Resolve(null, Naming(("customer_id", Customers)), rows, Own("customer_id")).ShouldHaveSingleItem();

        r.ToTable.ShouldBe(Customers);
        r.Incoming.ShouldBeFalse();
    }

    [Fact]
    public void StoredPresent_NamingRuleSuppressed_ColumnMetaKept()
    {
        var rows = Rows(Tbl(Orders, ["customer_id", "region_id", "buyer"]), Tbl(Customers, ["id"]), Tbl(Invoices, ["id"]));

        var result = Resolve(
            [Rel(Customers, "customer_id", "id")],
            [.. Naming(("region_id", Invoices)), Meta("buyer", Invoices)],
            rows,
            Own("customer_id", "region_id", "buyer"));

        result.Select(r => r.FromColumn).ShouldBe(["customer_id", "buyer"]);
    }

    [Fact]
    public void DuplicatePair_ListedOnce_OutgoingWins()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["customer_id"]));

        var result = Resolve(
            [Rel(Customers, "customer_id", "customer_id"), Rel(Customers, "CUSTOMER_ID", "customer_id", isList: true)],
            Naming(("customer_id", Customers)),
            rows,
            Own("customer_id"));

        result.ShouldHaveSingleItem().Incoming.ShouldBeFalse();
    }

    [Fact]
    public void SelfRelation_ListedOnce()
    {
        var rows = Rows(Tbl(Orders, ["id", "parent_id"]));

        var result = Resolve([Rel(Orders, "parent_id", "id")], null, rows, Own("id", "parent_id"));

        result.ShouldHaveSingleItem().ToTable.ShouldBe(Orders);
    }

    [Fact]
    public void SeveralRelationsBetweenSameTables_OneEntryPerColumnPair()
    {
        var rows = Rows(Tbl(Orders, ["billing_id", "shipping_id"]), Tbl(Customers, ["id"]));

        var result = Resolve([Rel(Customers, "billing_id", "id"), Rel(Customers, "shipping_id", "id")], null, rows, Own("billing_id", "shipping_id"));

        result.Select(r => r.FromColumn).ShouldBe(["billing_id", "shipping_id"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CompositeRelation_Skipped()
    {
        var rows = Rows(Tbl(Orders, ["a", "b"]), Tbl(Customers, ["a", "b"]));

        Resolve([new GraphQlRelationMapping("f", Customers, true, ["a", "b"], ["a", "b"])], null, rows, Own("a", "b")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void CrossDataSourceRelation_Skipped()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"], source: "pg1"), Tbl(Customers, ["id"], source: "pg2"));

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldBeEmpty();
    }

    [Fact]
    public void MaxRelations_AppliedAfterVisibilityFilter()
    {
        var rows = Rows(Tbl(Orders, ["a", "b", "c", "d"]), Tbl(Customers, ["id"]));

        Resolve(
            [Rel(Customers, "a", "id"), Rel(Customers, "b", "id"), Rel(Customers, "c", "id"), Rel(Customers, "d", "id")],
            null, rows, Own("a", "b", "c", "d"), max: 2).Select(r => r.FromColumn).ShouldBe(["a", "b"]);
    }

    // ---- Security: visibility of the hit, the target and both join columns -------------------------------------------------------

    [Fact]
    [Trait("Category", "Security")]
    public void TargetTableInvisible_RelationDropped()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]));

        var result = Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id"));

        result.ShouldBeEmpty();
        JsonSerializer.Serialize(result).ShouldNotContain("customers");
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("summary")]
    [InlineData("requestable-not-granted")]
    public void TargetTableSummaryShape_RelationDropped(string kind)
    {
        // A Summary or requestable-not-granted target is never a Full row: it is either absent or not Full.
        var rows = Rows(Tbl(Orders, ["customer_id"]));
        if (kind == "summary")
        {
            rows[Customers] = default;
        }

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TargetTableRequestableNotGranted_RelationDropped()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]));
        rows[Customers] = default;

        Resolve([Rel(Customers, "customer_id", "id")], Naming(("customer_id", Customers)), rows, Own("customer_id")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HitSummaryRow_NoRelations()
    {
        var rows = Rows(Tbl(Customers, ["id"]));
        rows[Orders] = default;

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldBeEmpty();
        Resolve([Rel(Customers, "customer_id", "id")], null, Rows(Tbl(Customers, ["id"])), Own("customer_id")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OwnJoinColumnNoAccess_RelationDropped()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"]));

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("other_column")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TargetJoinColumnConsentDenied_RelationDropped()
    {
        // A consent-denied column is removed from the visible row's column list.
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["name"]));

        var result = Resolve([Rel(Customers, "customer_id", "secret_ref")], null, rows, Own("customer_id"));

        result.ShouldBeEmpty();
        JsonSerializer.Serialize(result).ShouldNotContain("secret_ref");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TargetJoinColumnDiscoveryHidden_RelationDropped()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"], hidden: ["id"]));

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OwnJoinColumnMasked_RelationListed()
    {
        // A masked column is part of the visible set: only its identifier is disclosed.
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"]));

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldHaveSingleItem();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TargetJoinColumnMasked_RelationListed()
    {
        var rows = Rows(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"]));

        Resolve([Rel(Customers, "customer_id", "id")], null, rows, Own("customer_id")).ShouldHaveSingleItem().ToColumn.ShouldBe("id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HiddenStoredRelationOnly_NamingFallbackStillApplies()
    {
        var rows = Rows(Tbl(Orders, ["customer_id", "invoice_id"]), Tbl(Invoices, ["invoice_id"]));
        var naming = Naming(("invoice_id", Invoices));

        var withHidden = Resolve([Rel(Customers, "customer_id", "id")], naming, rows, Own("customer_id", "invoice_id"));
        var withoutStored = Resolve(null, naming, rows, Own("customer_id", "invoice_id"));

        withHidden.ShouldHaveSingleItem().ToTable.ShouldBe(Invoices);
        withHidden.ShouldBe(withoutStored);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void VisibleStoredRelation_SuppressesNamingFallback()
    {
        var rows = Rows(Tbl(Orders, ["customer_id", "invoice_id", "buyer"]), Tbl(Customers, ["id"]), Tbl(Invoices, ["id", "invoice_id"]));

        var result = Resolve(
            [Rel(Customers, "customer_id", "id")],
            [.. Naming(("invoice_id", Invoices)), Meta("buyer", Invoices)],
            rows,
            Own("customer_id", "invoice_id", "buyer"));

        result.Select(r => r.FromColumn).ShouldBe(["customer_id", "buyer"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void StoredRelationVisibleTargetHiddenColumn_FallbackSuppressedNoLeak()
    {
        var rows = Rows(Tbl(Orders, ["customer_id", "invoice_id"]), Tbl(Customers, ["id"], hidden: ["id"]), Tbl(Invoices, ["invoice_id"]));

        Resolve([Rel(Customers, "customer_id", "id")], Naming(("invoice_id", Invoices)), rows, Own("customer_id", "invoice_id")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HiddenRelationsDoNotConsumeCap()
    {
        var rows = Rows(Tbl(Orders, ["h1", "h2", "h3", "v1", "v2", "v3"]), Tbl(Customers, ["id"]));
        var own = Own("h1", "h2", "h3", "v1", "v2", "v3");

        var result = Resolve(
            [Rel(Other, "h1", "id"), Rel(Other, "h2", "id"), Rel(Other, "h3", "id"), Rel(Customers, "v1", "id"), Rel(Customers, "v2", "id"), Rel(Customers, "v3", "id")],
            null, rows, own);

        result.Select(r => r.FromColumn).ShouldBe(["v1", "v2", "v3"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void HiddenRelationCount_NotObservable()
    {
        var rows = Rows(Tbl(Orders, ["v1", "h"]), Tbl(Customers, ["id"]));
        var visible = Rel(Customers, "v1", "id");
        var hidden = Enumerable.Range(0, 7).Select(i => Rel(new TableIdentifier("x", "y", "t" + i), "h", "id")).ToList();

        var none = Resolve([visible], null, rows, Own("v1", "h"));
        var many = Resolve([.. hidden, visible, .. hidden], null, rows, Own("v1", "h"));

        JsonSerializer.Serialize(many).ShouldBe(JsonSerializer.Serialize(none));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MetaForeignKeyToInvisibleTable_Dropped()
    {
        var rows = Rows(Tbl(Orders, ["buyer"]));

        Resolve(null, [Meta("buyer", Customers)], rows, Own("buyer")).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void RestrictedScopeCaller_OnlyScopeVisibleColumns()
    {
        var rows = Rows(Tbl(Orders, ["customer_id", "region_id"]), Tbl(Customers, ["id"]));
        var stored = new[] { Rel(Customers, "customer_id", "id"), Rel(Customers, "region_id", "id") };

        Resolve(stored, null, rows, Own("customer_id")).Select(r => r.FromColumn).ShouldBe(["customer_id"]);
    }

    // ---- Security: the asynchronous resolver fails closed ---------------------------------------------------------------------------

    private static CatalogSearchHit Hit(TableIdentifier id, string kind = "table") => new(
        id, id.TableName, null, id.Domain, "NORMAL", 1, 1, null, [], [], [], null, Kind: kind);

    private static IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> OwnOf(TableIdentifier id, params string[] columns) =>
        new Dictionary<TableIdentifier, IReadOnlySet<string>> { [id] = Own(columns) };

    private static Task<List<CatalogSearchHit>> RunAsync(
        ICatalogVisibilityService? visibility,
        IGraphQlCatalogMap? map,
        IReadOnlyList<CatalogSearchHit> hits,
        IReadOnlyDictionary<TableIdentifier, IReadOnlySet<string>> own,
        ILogger? logger = null,
        bool expand = true,
        CancellationToken ct = default) =>
        CatalogDiscoveryService.ResolveJoinRelationsForHitsAsync(
            visibility, map, logger ?? NullLogger.Instance, hits, own,
            new CatalogSearchQuery("q", ExpandRelations: expand), Caller, ct);

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQlMapNull_FallbackFiltered_NoUnfilteredHeuristics()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id", "invoice_id"], fkMeta: new() { ["customer_id"] = "hidden_tbl" }), Tbl(Invoices, ["invoice_id"]));

        var hits = await RunAsync(visibility, null, [Hit(Orders)], OwnOf(Orders, "customer_id", "invoice_id"));

        var rel = hits.ShouldHaveSingleItem().RelatedJoinPaths.ShouldHaveSingleItem();
        rel.ToTable.ShouldBe(Invoices);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQlMapThrows_NoThrow_FallbackFiltered()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["invoice_id", "customer_id"]), Tbl(Invoices, ["invoice_id"]));

        var hits = await RunAsync(visibility, new FakeMap { Throw = true }, [Hit(Orders)], OwnOf(Orders, "invoice_id"));

        hits.ShouldHaveSingleItem().RelatedJoinPaths.ShouldHaveSingleItem().ToTable.ShouldBe(Invoices);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibilityLookupThrows_NoRelations_NoThrow()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"])) { Throw = true };
        var map = new FakeMap { [Orders] = [Rel(Customers, "customer_id", "id")] };

        var hits = await RunAsync(visibility, map, [Hit(Orders), Hit(Customers)], OwnOf(Orders, "customer_id"));

        hits.Count.ShouldBe(2);
        hits.ShouldAllBe(h => h.RelatedJoinPaths.Count == 0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibilityServiceMissing_NoRelations()
    {
        var map = new FakeMap { [Orders] = [Rel(Customers, "customer_id", "id")] };

        var hits = await RunAsync(null, map, [Hit(Orders)], OwnOf(Orders, "customer_id"));

        hits.ShouldHaveSingleItem().RelatedJoinPaths.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task MoreThan500DistinctTargets_ExcessDropped_SingleBatchCall()
    {
        var targets = Enumerable.Range(0, 600).Select(i => new TableIdentifier("sales", "dbo", $"t{i:D3}")).ToList();
        var columns = Enumerable.Range(0, 600).Select(i => $"c{i:D3}").ToArray();
        var tables = targets.Select(t => Tbl(t, ["id"])).Append(Tbl(Orders, columns)).ToArray();
        var visibility = new FakeVisibility(tables);
        var map = new FakeMap { [Orders] = targets.Select((t, i) => Rel(t, $"c{i:D3}", "id")).ToList() };

        var hits = await RunAsync(visibility, map, [Hit(Orders)], OwnOf(Orders, columns), logger: null);

        visibility.ManyCalls.Count.ShouldBe(2);
        visibility.ManyCalls[1].Count.ShouldBe(CatalogDiscoveryService.MaxJoinRelationTargets);
        visibility.SingleCalls.ShouldBe(0);
        hits.ShouldHaveSingleItem().RelatedJoinPaths.Count.ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NonAuthoritativeBatch_FallsBackToSingleLookups_StillFiltered()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id", "invoice_id"]), Tbl(Customers, ["id"])) { Authoritative = false };
        var map = new FakeMap { [Orders] = [Rel(Customers, "customer_id", "id"), Rel(Invoices, "invoice_id", "id")] };

        var hits = await RunAsync(visibility, map, [Hit(Orders)], OwnOf(Orders, "customer_id", "invoice_id"));

        hits.ShouldHaveSingleItem().RelatedJoinPaths.ShouldHaveSingleItem().ToTable.ShouldBe(Customers);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FailureLog_ContainsNoTableOrColumnNames()
    {
        var entries = new List<(LogLevel Level, string Message)>();
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id"])) { Throw = true };
        var map = new FakeMap { Throw = true };

        await RunAsync(visibility, map, [Hit(Orders)], OwnOf(Orders, "customer_id"), new ListLogger<CatalogDiscoveryService>(entries));

        entries.ShouldNotBeEmpty();
        entries.ShouldAllBe(e => e.Level == LogLevel.Warning);
        foreach (var (_, message) in entries)
        {
            message.ShouldContain(nameof(InvalidOperationException));
            message.ShouldNotContain("orders");
            message.ShouldNotContain("customer_id");
            message.ShouldNotContain("sales");
        }
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id"])) { ThrowCanceled = true };

        await Should.ThrowAsync<OperationCanceledException>(() => RunAsync(visibility, null, [Hit(Orders)], OwnOf(Orders, "customer_id"), ct: cts.Token));
    }

    [Fact]
    public async Task ExpandRelationsFalse_NoRelations()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"]));
        var map = new FakeMap { [Orders] = [Rel(Customers, "customer_id", "id")] };
        var seeded = Hit(Orders) with { RelatedJoinPaths = [Meta("x", Customers)] };

        var hits = await RunAsync(visibility, map, [seeded], OwnOf(Orders, "customer_id"), expand: false);

        hits.ShouldHaveSingleItem().RelatedJoinPaths.ShouldBeEmpty();
        visibility.ManyCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ProcedureHit_NoRelations()
    {
        var visibility = new FakeVisibility(Tbl(Orders, ["customer_id"]), Tbl(Customers, ["id"]));
        var map = new FakeMap { [Orders] = [Rel(Customers, "customer_id", "id")] };

        var hits = await RunAsync(visibility, map, [Hit(Orders, kind: "procedure"), Hit(Orders)], OwnOf(Orders, "customer_id"));

        hits[0].RelatedJoinPaths.ShouldBeEmpty();
        hits[1].RelatedJoinPaths.ShouldHaveSingleItem();
    }

    private sealed class FakeMap : IGraphQlCatalogMap
    {
        private readonly Dictionary<TableIdentifier, IReadOnlyList<GraphQlRelationMapping>> _relations = [];

        public bool Throw { get; init; }

        public List<GraphQlRelationMapping> this[TableIdentifier id]
        {
            set => _relations[id] = value;
        }

        public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("map failed for orders.customer_id");
            }

            return Task.FromResult(_relations.TryGetValue(table, out var rels)
                ? new GraphQlTableMapping(table, "f", "T", "F", "O", [], rels)
                : null);
        }

        public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default) => Task.FromResult<TableIdentifier?>(null);

        public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
    }

    private sealed class FakeVisibility(params TableMetadata[] tables) : ICatalogVisibilityService
    {
        public bool Throw { get; init; }

        public bool ThrowCanceled { get; init; }

        public bool Authoritative { get; init; } = true;

        public List<IReadOnlyCollection<TableIdentifier>> ManyCalls { get; } = [];

        public int SingleCalls { get; private set; }

        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
        {
            SingleCalls++;
            var found = tables.FirstOrDefault(t => t.Identifier == table);
            return ValueTask.FromResult<CatalogVisibleRow?>(found is null ? null : new CatalogVisibleRow(found, CatalogOutputShape.Full));
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async ValueTask<CatalogVisibleBatch> FindVisibleManyAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
        {
            ManyCalls.Add(candidates.ToList());
            await Task.Yield();
            if (ThrowCanceled)
            {
                throw new OperationCanceledException();
            }

            if (Throw)
            {
                throw new InvalidOperationException("visibility failed for orders.customer_id in sales");
            }

            if (!Authoritative)
            {
                return default;
            }

            return CatalogVisibleBatch.Of(tables
                .Where(t => candidates.Contains(t.Identifier))
                .ToDictionary(t => t.Identifier, t => new CatalogVisibleRow(t, CatalogOutputShape.Full)));
        }
    }
}

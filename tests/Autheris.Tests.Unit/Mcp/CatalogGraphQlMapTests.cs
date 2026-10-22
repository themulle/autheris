namespace Autheris.Tests.Unit.Mcp;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Mcp;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// The MCP view of the catalog GraphQL schema: GraphQL names of a table and the tables a query document reads,
/// so the guardrail can run ABAC and four-eyes on every table of a query_graphql call.
/// </summary>
public sealed class CatalogGraphQlMapTests
{
    private static readonly TableIdentifier Customers = new("crm", "dbo", "customers");
    private static readonly TableIdentifier Orders = new("crm", "dbo", "orders");
    private static readonly TableIdentifier Files = new("lake", "raw", "files");

    private readonly CatalogGraphQlMap _map;

    public CatalogGraphQlMapTests()
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(
        [
            Table(Customers, [new TableColumn { ColumnName = "id", DataType = "bigint" }, new TableColumn { ColumnName = "name", DataType = "varchar" }]),
            Table(Orders, [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "customer_id", DataType = "bigint" }, new TableColumn { ColumnName = "amount", DataType = "decimal(10,2)" }]),
            Table(Files, [new TableColumn { ColumnName = "path", DataType = "varchar" }], DataSourceType.LakehouseIceberg)
        ]));

        var relations = Substitute.For<ITableRelationRepository>();
        var relation = new TableRelation
        {
            RelationName = "customer_orders",
            ParentTableIdentifier = Customers,
            ChildTableIdentifier = Orders,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["customer_id"]
        };
        relations.GetRelationsForTableAsync(Customers, Arg.Any<CancellationToken>()).Returns([relation]);
        relations.GetRelationsForTableAsync(Orders, Arg.Any<CancellationToken>()).Returns([]);
        relations.GetRelationsForTableAsync(Files, Arg.Any<CancellationToken>()).Returns([]);

        _map = new CatalogGraphQlMap(metadata, relations);
    }

    private static TableMetadata Table(TableIdentifier id, IReadOnlyList<TableColumn> columns, DataSourceType type = DataSourceType.Sql, string sourceName = "") => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = true, SourceType = "PostgreSql", SourceName = sourceName, DataSourceType = type },
        Columns = columns
    };

    [Fact]
    public async Task GetTable_ReturnsTheGeneratedNames_AndGraphQlTypes()
    {
        var orders = await _map.GetTableAsync(Orders);

        orders.ShouldNotBeNull();
        orders.QueryField.ShouldBe("crm_dbo_orders");
        orders.FilterType.ShouldBe("crm_dbo_orders_filter");
        orders.OrderByType.ShouldBe("crm_dbo_orders_order_by");
        orders.Columns.Select(c => (c.ColumnName, c.GraphQlType)).ShouldBe([("id", "Int"), ("customer_id", "Long"), ("amount", "Decimal")]);
        orders.Relations.ShouldHaveSingleItem().Target.ShouldBe(Customers);
    }

    [Fact]
    public async Task GetTable_CrossSourceRelation_IsNotListedAndNamesNoOmittedTarget()
    {
        // BUG #75 (S-9): the MCP view is derived from the same model, so an omitted relation never shows up.
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(
        [
            Table(Customers, [new TableColumn { ColumnName = "id", DataType = "bigint" }], sourceName: "pg1"),
            Table(Orders, [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "customer_id", DataType = "bigint" }], sourceName: "pg2")
        ]));
        var relations = Substitute.For<ITableRelationRepository>();
        relations.GetRelationsForTableAsync(Customers, Arg.Any<CancellationToken>()).Returns([new TableRelation
        {
            RelationName = "customer_orders",
            ParentTableIdentifier = Customers,
            ChildTableIdentifier = Orders,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["customer_id"]
        }]);
        relations.GetRelationsForTableAsync(Orders, Arg.Any<CancellationToken>()).Returns([]);
        var map = new CatalogGraphQlMap(metadata, relations);

        var customers = await map.GetTableAsync(Customers);
        var orders = await map.GetTableAsync(Orders);

        customers.ShouldNotBeNull().Relations.ShouldBeEmpty();
        orders.ShouldNotBeNull().Relations.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTable_TableOutsideTheGraphQlSchema_IsNull()
    {
        (await _map.GetTableAsync(Files)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveDocument_CollectsRootTablesAndNestedRelations_ThroughFragments()
    {
        var customers = (await _map.GetTableAsync(Customers))!;
        var relationField = customers.Relations.Single().Field;

        var tables = await _map.ResolveDocumentTablesAsync($$"""
            query Q { c: crm_dbo_customers(first: 5) { id ...F __typename } }
            fragment F on crm_dbo_customers { {{relationField}} { amount } }
            """);

        tables.ShouldNotBeNull();
        tables.ShouldBe([Customers, Orders], ignoreOrder: true);
    }

    [Fact]
    public async Task ResolveDocument_IntrospectionOnly_ReadsNoTable()
    {
        var tables = await _map.ResolveDocumentTablesAsync("{ __schema { types { name } } __typename }");

        tables.ShouldNotBeNull();
        tables.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("not graphql {")]
    [InlineData("mutation { crm_dbo_customers { id } }")]
    [InlineData("subscription { crm_dbo_customers { id } }")]
    [InlineData("query A { crm_dbo_customers { id } } query B { crm_dbo_orders { id } }")]
    [InlineData("{ unknownField { id } }")]
    [InlineData("{ crm_dbo_customers { ...Missing } }")]
    public async Task ResolveDocument_AnythingElse_FailsClosed(string document)
    {
        (await _map.ResolveDocumentTablesAsync(document)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task FragmentBomb25Levels_CompletesUnder1s_ReturnsDistinctTables()
    {
        // MCP-11: every level spreads the next one twice; without memoisation this is 2^25 expansions. The chain stays below MaxSelectionDepth (32), which counts fragment nesting.
        var customers = (await _map.GetTableAsync(Customers))!;
        var relationField = customers.Relations.Single().Field;
        var fragments = string.Join("\n", Enumerable.Range(0, 25).Select(i => $"fragment F{i} on crm_dbo_customers {{ ...F{i + 1} ...F{i + 1} }}"));
        var document = $"query Q {{ crm_dbo_customers {{ ...F0 }} }}\n{fragments}\nfragment F25 on crm_dbo_customers {{ id {relationField} {{ amount }} }}";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tables = await _map.ResolveDocumentTablesAsync(document);
        watch.Stop();

        watch.Elapsed.ShouldBeLessThan(System.TimeSpan.FromSeconds(1));
        tables.ShouldNotBeNull();
        tables.ShouldBe([Customers, Orders], ignoreOrder: true);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CyclicFragments_ReturnsNull()
    {
        (await _map.ResolveDocumentTablesAsync(
            "query Q { crm_dbo_customers { ...A } } fragment A on crm_dbo_customers { ...B } fragment B on crm_dbo_customers { ...A }"))
            .ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisitedSelectionsCapExceeded_ReturnsNull_GuardrailDenies()
    {
        // MCP-11: more than 10,000 visited selections cannot be resolved, so the guardrail has no table list and denies.
        var fields = string.Join(" ", Enumerable.Repeat("id", 12_000));

        (await _map.ResolveDocumentTablesAsync($"{{ crm_dbo_customers {{ {fields} }} }}")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DocumentLongerThan64KB_ReturnsNull()
    {
        var padding = new string(' ', 70 * 1024);

        (await _map.ResolveDocumentTablesAsync($"{{ crm_dbo_customers {{ id }} }}{padding}")).ShouldBeNull();
    }
}

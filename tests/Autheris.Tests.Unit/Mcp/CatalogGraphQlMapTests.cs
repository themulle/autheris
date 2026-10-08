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

    private static TableMetadata Table(TableIdentifier id, IReadOnlyList<TableColumn> columns, DataSourceType type = DataSourceType.Sql) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = true, SourceType = "PostgreSql", DataSourceType = type },
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
}

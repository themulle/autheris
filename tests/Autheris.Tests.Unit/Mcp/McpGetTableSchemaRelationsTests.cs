namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #711 A: <c>get_table_schema</c> honours <c>include_relations</c> (default true). Only declared relations of the catalog
/// GraphQL schema are listed (MCP-05: never guessed from column names), and only towards a table the caller can see, with the join
/// columns visible on both sides (the rule of <c>describe_dataset</c>, issue #392).
/// </summary>
public sealed class McpGetTableSchemaRelationsTests
{
    private static readonly TableIdentifier Orders = new("sales", "Public", "orders");
    private static readonly TableIdentifier Customers = new("sales", "public", "customers");
    private static readonly TableIdentifier Regions = new("sales", "public", "regions");
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly McpSessionContext Session = new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

    private readonly ICatalogDiscoveryService _discovery = Substitute.For<ICatalogDiscoveryService>();
    private readonly IGraphQlCatalogMap _map = Substitute.For<IGraphQlCatalogMap>();

    public McpGetTableSchemaRelationsTests()
    {
        Visible(Orders, "id", "customer_id", "region_code", "region_country");
        Visible(Customers, "id", "name");
        Visible(Regions, "code", "country");
        Mapping(Orders,
            new GraphQlRelationMapping("customer", Customers, IsList: false, ["customer_id"], ["id"]),
            new GraphQlRelationMapping("region", Regions, IsList: false, ["region_code", "region_country"], ["code", "country"]));
        Mapping(Customers, new GraphQlRelationMapping("orders", Orders, IsList: true, ["id"], ["customer_id"]));
    }

    private void Visible(TableIdentifier id, params string[] columns) =>
        _discovery.GetDatasetDetailAsync(id, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogDatasetDetail?>(new CatalogDatasetDetail(
                DatasetId: id.ToString(),
                Domain: id.Domain,
                Schema: id.Schema,
                Table: id.TableName,
                Columns: columns.Select(c => new CatalogColumnDetail(c, "int", "Public", "Clear", IsPrimaryKey: false, IsPiiIndicator: false)).ToList(),
                PrimaryKeys: [],
                Sensitivity: "INTERNAL",
                IsActive: true)));

    private void Invisible(TableIdentifier id) =>
        _discovery.GetDatasetDetailAsync(id, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogDatasetDetail?>(null));

    private void Mapping(TableIdentifier id, params GraphQlRelationMapping[] relations) =>
        _map.GetTableAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GraphQlTableMapping?>(new GraphQlTableMapping(id, $"{id.Domain}_{id.Schema}_{id.TableName}", "T", "F", "O", [], relations)));

    private async Task<McpTableSchemaResult> GetTableSchemaAsync(string arguments, IGraphQlCatalogMap? map = null)
    {
        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            _discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>(),
            graphQlMap: map ?? _map);
        var tool = new McpToolDefinition(McpDatasetTools.GetTableSchema, "Get schema", "{}", TargetGraphQLOperation: "get_table_schema");
        var json = await handler.ExecuteToolAsync(tool, arguments, Session);
        return JsonSerializer.Deserialize<McpTableSchemaResult>(json, JsonOpts)!;
    }

    [Fact]
    public async Task IncludeRelationsDefault_ListsDeclaredRelationsWithDatasetIds_BothDirections()
    {
        var result = await GetTableSchemaAsync("""{"datasets":["sales.public.orders","sales.public.customers"]}""");

        var orders = result.Tables.Single(t => t.Table() == "orders");
        var relation = orders.Relations.ShouldHaveSingleItem();
        relation.TargetTable.ShouldBe("sales.public.customers");
        relation.FromColumn.ShouldBe("customer_id");
        relation.ToColumn.ShouldBe("id");

        var customers = result.Tables.Single(t => t.Table() == "customers");
        customers.Relations.ShouldHaveSingleItem().ShouldBe(new McpTableRelation("sales.public.orders", "id", "customer_id"));
    }

    [Fact]
    public async Task IncludeRelationsTrue_SameAsDefault()
    {
        var result = await GetTableSchemaAsync("""{"dataset":"sales.public.orders","include_relations":true}""");

        result.Tables.ShouldHaveSingleItem().Relations.ShouldHaveSingleItem().TargetTable.ShouldBe("sales.public.customers");
    }

    [Fact]
    public async Task IncludeRelationsFalse_ListsNoRelations_AndReadsNoRelationTarget()
    {
        var result = await GetTableSchemaAsync("""{"dataset":"sales.public.orders","include_relations":false}""");

        result.Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();
        await _map.DidNotReceiveWithAnyArgs().GetTableAsync(default, default);
        await _discovery.DidNotReceive().GetDatasetDetailAsync(Customers, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompositeKeyRelation_IsNotSplitIntoSingleColumnRelations()
    {
        // McpTableRelation carries one column pair; a two-column key is not listed rather than misrepresented as two relations.
        var result = await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""");

        result.Tables.ShouldHaveSingleItem().Relations.ShouldNotContain(r => r.TargetTable == "sales.public.regions");
    }

    [Fact]
    public async Task TableOutsideTheGraphQlSchema_OrNoMap_ListsNoRelations()
    {
        _map.GetTableAsync(Orders, Arg.Any<CancellationToken>()).Returns(Task.FromResult<GraphQlTableMapping?>(null));

        (await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""")).Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RelationToATableTheCallerCannotSee_IsOmitted()
    {
        Invisible(Customers);

        var result = await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""");

        result.Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RelationWithAJoinColumnNotListedForTheCaller_IsOmitted_OnEitherSide()
    {
        Visible(Orders, "id", "region_code");          // own join column customer_id not listed
        Visible(Customers, "id");
        (await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""")).Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();

        Visible(Orders, "id", "customer_id");
        Visible(Customers, "name");                     // target join column id not listed
        (await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""")).Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RelationsAreNeverGuessedFromColumnNames()
    {
        // MCP-05: customer_id without a declared relation must not produce sales.public.customers.
        Mapping(Orders);

        (await GetTableSchemaAsync("""{"dataset":"sales.public.orders"}""")).Tables.ShouldHaveSingleItem().Relations.ShouldBeEmpty();
    }
}

file static class TableSchemaTestExtensions
{
    public static string Table(this McpTableBriefSchema schema) => schema.Dataset.Split('.')[^1];
}

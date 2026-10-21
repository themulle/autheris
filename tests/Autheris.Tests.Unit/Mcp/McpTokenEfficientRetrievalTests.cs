namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Verifies token-efficient retrieval across large catalogs (10,000+ tables) for AI App and Report Generators.
/// </summary>
public sealed class McpTokenEfficientRetrievalTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly TableIdentifier OrdersTable = new("sales", "public", "orders");
    private static readonly TableIdentifier CustomersTable = new("sales", "public", "customers");

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IGatewayExecutionService _gateway = Substitute.For<IGatewayExecutionService>();

    private static TableMetadata CreateTable(TableIdentifier id, string description, params (string Name, string Type, bool Pk)[] cols) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, Description = description, Sensitivity = "INTERNAL" },
        PrimaryKeyColumns = cols.Where(c => c.Pk).Select(c => c.Name).ToList(),
        Columns = cols.Select(c => new TableColumn
        {
            ColumnName = c.Name,
            DataType = c.Type,
            Description = $"{c.Name} column"
        }).ToList()
    };

    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-USER"),
                new Claim("tenant_id", "tenant-a"),
                .. roles.Select(r => new Claim(ClaimTypes.Role, r))
            ],
            "MCP"));

    [Fact]
    public async Task ListDatasets_OverviewMode_ReturnsDomainCountsWithoutEnumeratingDatasets()
    {
        // 300 tables across 3 domains
        var tables = new List<TableMetadata>();
        for (int i = 0; i < 150; i++) tables.Add(CreateTable(new("sales", "public", $"order_{i}"), "Sales table"));
        for (int i = 0; i < 100; i++) tables.Add(CreateTable(new("finance", "public", $"invoice_{i}"), "Finance table"));
        for (int i = 0; i < 50; i++) tables.Add(CreateTable(new("hr", "public", $"employee_{i}"), "HR table"));

        var options = new GatewayOptions { Catalog = new DataCatalogOptions { SharedTables = tables.Select(t => $"{t.Identifier.Domain}.{t.Identifier.Schema}.{t.Identifier.TableName}").ToList() } };
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));

        var catalog = new McpDatasetCatalog(_metadata, _consents, _gateway, options: Options.Create(options),
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents, options));

        var result = await catalog.ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: null, offset: 0, limit: 10, tag: null, language: null, overview: true, format: null, ct: default);

        result.Datasets.ShouldBeEmpty();
        result.Total.ShouldBe(300);
        result.Domains.ShouldNotBeNull();
        result.Domains.Count.ShouldBe(3);
        result.Domains[0].Domain.ShouldBe("sales");
        result.Domains[0].TableCount.ShouldBe(150);
        result.Domains[1].Domain.ShouldBe("finance");
        result.Domains[1].TableCount.ShouldBe(100);
        result.Domains[2].Domain.ShouldBe("hr");
        result.Domains[2].TableCount.ShouldBe(50);
        result.Guidance.ShouldNotBeNull();
        result.Guidance.ShouldContain("domains");
        result.Endpoints.ShouldBeNull();
    }

    [Fact]
    public async Task ListDatasets_CompactFormat_AndPagination_OmitsEndpointsToSaveTokens()
    {
        var tables = new List<TableMetadata>
        {
            CreateTable(OrdersTable, "Orders table", ("id", "int", true)),
            CreateTable(CustomersTable, "Customers table", ("id", "int", true))
        };

        var options = new GatewayOptions { Catalog = new DataCatalogOptions { SharedTables = ["sales.public.orders", "sales.public.customers"] } };
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(tables));

        var catalog = new McpDatasetCatalog(_metadata, _consents, _gateway, options: Options.Create(options),
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents, options));

        // When format == "compact": Endpoints should be null
        var compact = await catalog.ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: null, offset: 0, limit: 10, tag: null, language: null, overview: false, format: "compact", ct: default);
        compact.Endpoints.ShouldBeNull();
        compact.Datasets.Count.ShouldBe(2);

        // When offset > 0: Endpoints should be omitted
        var page1 = await catalog.ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: null, offset: 1, limit: 10, tag: null, language: null, overview: false, format: null, ct: default);
        page1.Endpoints.ShouldBeNull();

        // When offset == 0 and format != compact: Endpoints should be present
        var page0 = await catalog.ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: null, offset: 0, limit: 10, tag: null, language: null, overview: false, format: null, ct: default);
        page0.Endpoints.ShouldNotBeNull();
    }

    [Fact]
    public async Task DescribeDataset_BriefMode_OmitsHeavyProfilingAndGoldenQueries()
    {
        var table = CreateTable(OrdersTable, "Orders table", ("id", "int", true), ("customer_id", "int", false), ("amount", "decimal", false));
        var options = new GatewayOptions { Catalog = new DataCatalogOptions { SharedTables = ["sales.public.orders"] } };
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        _metadata.GetTableMetadataAsync(OrdersTable, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(table));

        var catalog = new McpDatasetCatalog(_metadata, _consents, _gateway, options: Options.Create(options),
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(_metadata.ServeSingleReadsFromCatalog(), _consents, options));

        var brief = await catalog.DescribeDatasetAsync(User("GovernanceAdmin"), "sales.public.orders", language: null, brief: true, ct: default);

        brief.Id.ShouldBe("sales.public.orders");
        brief.View.ShouldBe("Brief");
        brief.LongDescription.ShouldBeNull();
        brief.GraphQl.ShouldBeNull();
        brief.OtherAccess.ShouldBeNull();
        brief.Examples.ShouldBeEmpty();
        brief.Columns.Count.ShouldBe(3);
        brief.Columns.ShouldAllBe(c => c.ValueProfile == null && c.Deprecation == null && c.Semantics == null);
        brief.Columns[0].PrimaryKey.ShouldBeTrue();
    }

    [Fact]
    public async Task McpToolExecutionHandler_GetTableSchema_BatchRetrieval_ReturnsConciseSchemaAndRelations()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        var ordersDetail = new CatalogDatasetDetail(
            DatasetId: "sales.public.orders",
            Domain: "sales",
            Schema: "public",
            Table: "orders",
            Columns:
            [
                new CatalogColumnDetail("id", "int", "INTERNAL", "Clear", IsPrimaryKey: true, IsPiiIndicator: false),
                new CatalogColumnDetail("customer_id", "int", "INTERNAL", "Clear", IsPrimaryKey: false, IsPiiIndicator: false, Description: "Customer FK"),
                new CatalogColumnDetail("amount", "decimal", "INTERNAL", "Clear", IsPrimaryKey: false, IsPiiIndicator: false)
            ],
            PrimaryKeys: ["id"],
            Sensitivity: "INTERNAL",
            IsActive: true,
            Description: "Customer orders and amounts");

        var customersDetail = new CatalogDatasetDetail(
            DatasetId: "sales.public.customers",
            Domain: "sales",
            Schema: "public",
            Table: "customers",
            Columns:
            [
                new CatalogColumnDetail("id", "int", "INTERNAL", "Clear", IsPrimaryKey: true, IsPiiIndicator: false),
                new CatalogColumnDetail("name", "varchar", "INTERNAL", "Clear", IsPrimaryKey: false, IsPiiIndicator: false)
            ],
            PrimaryKeys: ["id"],
            Sensitivity: "INTERNAL",
            IsActive: true,
            Description: "Customer master data");

        discovery.GetDatasetDetailAsync(OrdersTable, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogDatasetDetail?>(ordersDetail));
        discovery.GetDatasetDetailAsync(CustomersTable, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<CatalogDatasetDetail?>(customersDetail));

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            _metadata);

        var tool = new McpToolDefinition(McpDatasetTools.GetTableSchema, "Get schema", "{}", TargetGraphQLOperation: "get_table_schema");
        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

        var resultJson = await handler.ExecuteToolAsync(tool, """{"datasets":["sales.public.orders","sales.public.customers"]}""", session);

        var result = JsonSerializer.Deserialize<McpTableSchemaResult>(resultJson, JsonOpts);
        result.ShouldNotBeNull();
        result.TotalTables.ShouldBe(2);

        var orders = result.Tables.First(t => t.Dataset == "sales.public.orders");
        orders.Description.ShouldBe("Customer orders and amounts");
        orders.PrimaryKeys.ShouldBe(["id"]);
        orders.Columns.Count.ShouldBe(3);
        orders.Columns.First(c => c.Name == "id").PrimaryKey.ShouldBeTrue();
        orders.Relations.ShouldHaveSingleItem().TargetTable.ShouldBe("sales.customers");
        orders.Relations[0].FromColumn.ShouldBe("customer_id");
        orders.Relations[0].ToColumn.ShouldBe("id");
    }

    [Fact]
    public async Task McpToolExecutionHandler_FindRelevantTables_ReturnsRankedCandidatesAndJoinPaths()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        var hit = new CatalogSearchHit(
            TableIdentifier: OrdersTable,
            DisplayName: "Orders",
            Description: "Customer order lines and revenue",
            Domain: "sales",
            Sensitivity: "INTERNAL",
            CombinedScore: 0.95,
            Bm25Score: 0.95,
            VectorScore: 0.95,
            MatchedTerms: ["margin", "revenue"],
            RelevantColumns: ["amount"],
            RelatedJoinPaths:
            [
                new TableRelationship(OrdersTable, "customer_id", CustomersTable, "id", TableRelationshipType.ForeignKey)
            ],
            SuggestedGraphQlField: "sales_public_orders");

        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>([hit]));

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            _metadata);

        var tool = new McpToolDefinition(McpDatasetTools.FindRelevantTables, "Find relevant tables", "{}", TargetGraphQLOperation: "find_relevant_tables");
        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

        var resultJson = await handler.ExecuteToolAsync(tool, """{"use_case":"Customer margin and revenue report","domain":"sales","limit":5}""", session);

        var result = JsonSerializer.Deserialize<McpUseCaseDiscoveryResult>(resultJson, JsonOpts);
        result.ShouldNotBeNull();
        result.UseCase.ShouldBe("Customer margin and revenue report");
        result.TotalCandidates.ShouldBe(1);
        result.Candidates[0].Dataset.ShouldBe("sales.orders");
        result.Candidates[0].MatchedColumns.ShouldBe(["amount"]);
        result.Candidates[0].Relations.ShouldHaveSingleItem().TargetTable.ShouldBe("sales.customers");
        result.Candidates[0].Relations[0].FromColumn.ShouldBe("customer_id");
        result.Candidates[0].Relations[0].ToColumn.ShouldBe("id");
        result.RecommendedNextStep.ShouldNotBeNull();
        result.RecommendedNextStep.ShouldContain("get_table_schema");
    }
}

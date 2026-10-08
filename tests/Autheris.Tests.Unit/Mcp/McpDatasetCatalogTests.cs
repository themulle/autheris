namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// MCP dataset tools: list_datasets, describe_dataset and sample_rows show only what the caller may see
/// and read rows only through the governed table path.
/// </summary>
public sealed class McpDatasetCatalogTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Payroll = new("hr", "public", "payroll");

    private readonly ITableMetadataRepository _metadata = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consents = Substitute.For<IConsentRepository>();
    private readonly IGatewayExecutionService _gateway = Substitute.For<IGatewayExecutionService>();
    private readonly IGoldenQueryService _golden = Substitute.For<IGoldenQueryService>();

    public McpDatasetCatalogTests()
    {
        _metadata.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Table(Orders, "Customer orders"), Table(Payroll, "Salaries")]));
        _golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        // The caller has an Allow consent on orders that grants id and amount, but not customer_email.
        _consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
                new Consent
                {
                    TableIdentifier = Orders,
                    TenantId = new TenantId(Tenant),
                    Effect = ConsentEffect.Allow,
                    GranteeSid = new Sid("S-1-USER"),
                    ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                    ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                    ColumnRules =
                    [
                        new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                        new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear }
                    ]
                }
            ]));
    }

    private static TableMetadata Table(TableIdentifier id, string description) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, Description = description, Sensitivity = "INTERNAL" },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int", Description = "Key" },
            new TableColumn { ColumnName = "amount", DataType = "decimal", Description = "Order amount" },
            new TableColumn { ColumnName = "customer_email", DataType = "varchar", IsSensitive = true, Description = "Contact" }
        ]
    };

    private static ClaimsPrincipal User(params string[] roles) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-USER"),
                new Claim("tenant_id", Tenant),
                .. roles.Select(r => new Claim(ClaimTypes.Role, r))
            ],
            "MCP"));

    private readonly IGraphQlCatalogMap _graphQl = Substitute.For<IGraphQlCatalogMap>();

    private McpDatasetCatalog Catalog(GatewayOptions? options = null) =>
        new(_metadata, _consents, _gateway, _golden, Options.Create(options ?? new GatewayOptions()), _graphQl);

    private void MapOrdersToGraphQl() =>
        _graphQl.GetTableAsync(Orders, Arg.Any<CancellationToken>()).Returns(new GraphQlTableMapping(
            Orders, "sales_public_orders", "sales_public_orders", "sales_public_orders_filter", "sales_public_orders_order_by",
            [
                new GraphQlColumnMapping("id", "id", "Int"),
                new GraphQlColumnMapping("amount", "amount", "Decimal"),
                new GraphQlColumnMapping("customer_email", "customer_email", "String")
            ],
            [new GraphQlRelationMapping("payroll_by_order", Payroll, IsList: false)]));

    [Fact]
    public async Task ListDatasets_PointsToGraphQlFirst_AndListsTheEnabledProtocols()
    {
        MapOrdersToGraphQl();
        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true }, DuckDbOlap = new DuckDbOlapOptions { Enabled = false } };

        var result = await Catalog(options).ListDatasetsAsync(User(), null, null);

        result.Datasets.ShouldHaveSingleItem().GraphQlField.ShouldBe("sales_public_orders");
        result.Guidance!.ShouldContain("query_graphql");
        var endpoints = result.Endpoints!;
        endpoints[0].Protocol.ShouldBe("GraphQL");
        endpoints[0].Url.ShouldBe("/graphql");
        endpoints[0].Preferred.ShouldBeTrue();
        endpoints.Count(e => e.Preferred).ShouldBe(1);
        endpoints.Select(e => e.Protocol).ShouldContain("OData v4");
        endpoints.Select(e => e.Protocol).ShouldContain("WebSQL");
        endpoints.Select(e => e.Protocol).ShouldNotContain("DuckDB OLAP");
    }

    [Fact]
    public async Task DescribeDataset_ExplainsTheGraphQlQuery_ForVisibleColumnsAndDatasetsOnly()
    {
        MapOrdersToGraphQl();

        var result = await Catalog().DescribeDatasetAsync(User(), "sales.public.orders");

        var graphQl = result.GraphQl.ShouldNotBeNull();
        graphQl.Endpoint.ShouldBe("/graphql");
        graphQl.QueryField.ShouldBe("sales_public_orders");
        graphQl.FilterType.ShouldBe("sales_public_orders_filter");
        graphQl.ExampleQuery.ShouldContain("sales_public_orders(first: 10)");
        graphQl.ExampleQuery.ShouldContain("id amount");
        graphQl.ExampleQuery.ShouldNotContain("customer_email");
        graphQl.Arguments.ShouldContain("where");
        // The related payroll table is hidden from this caller, so its relation is not offered.
        graphQl.Relations.ShouldBeEmpty();
        result.Columns[1].GraphQlField.ShouldBe("amount");
        result.Columns[1].GraphQlType.ShouldBe("Decimal");
        result.OtherAccess!.ShouldContain(a => a.Protocol == "OData v4" && a.Url == "/odata/v4/sales/public/orders");
        result.OtherAccess!.ShouldNotContain(a => a.Preferred);
    }

    [Fact]
    public async Task DescribeDataset_Admin_SeesRelationsToOtherDatasets()
    {
        MapOrdersToGraphQl();

        var result = await Catalog().DescribeDatasetAsync(User("GovernanceAdmin"), "sales.public.orders");

        result.GraphQl!.Relations.ShouldHaveSingleItem().ShouldBe(new McpGraphQlRelation("payroll_by_order", "hr.public.payroll", false));
    }

    [Fact]
    public async Task DescribeDataset_OutsideTheGraphQlSchema_OffersTheOtherPaths()
    {
        var result = await Catalog().DescribeDatasetAsync(User(), "sales.public.orders");

        result.GraphQl.ShouldBeNull();
        result.OtherAccess!.ShouldContain(a => a.Protocol == "OData v4");
    }

    [Fact]
    public async Task ListDatasets_ShowsOnlyTablesWithAConsent()
    {
        var result = await Catalog().ListDatasetsAsync(User(), search: null, domain: null);

        result.Datasets.Select(d => d.Id).ShouldBe(["sales.public.orders"]);
        result.Datasets[0].Description.ShouldBe("Customer orders");
        result.Datasets[0].ColumnCount.ShouldBe(2);
        result.Total.ShouldBe(1);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public async Task ListDatasets_Admin_SeesTheWholeCatalog_AndSearchFilters()
    {
        var all = await Catalog().ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: null);
        all.Datasets.Select(d => d.Id).ShouldBe(["hr.public.payroll", "sales.public.orders"]);

        var bySearch = await Catalog().ListDatasetsAsync(User("GovernanceAdmin"), search: "salar", domain: null);
        bySearch.Datasets.Select(d => d.Id).ShouldBe(["hr.public.payroll"]);

        var byDomain = await Catalog().ListDatasetsAsync(User("GovernanceAdmin"), search: null, domain: "SALES");
        byDomain.Datasets.Select(d => d.Id).ShouldBe(["sales.public.orders"]);
    }

    [Fact]
    public async Task ListDatasets_AnonymousCaller_SeesNothing()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", Tenant)], "MCP"));

        var result = await Catalog().ListDatasetsAsync(anonymous, null, null);

        result.Datasets.ShouldBeEmpty();
    }

    [Fact]
    public async Task DescribeDataset_ReturnsOnlyGrantedColumns_AndExamples()
    {
        _golden.GetGoldenQueriesAsync("sales", "orders", Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>(
                [new GoldenQuery("g1", "sales", "orders", "Big orders", "Orders above 1000", "query { orders { id } }", "{\"min\":1000}")]));

        var result = await Catalog().DescribeDatasetAsync(User(), "Sales.Public.Orders");

        result.Id.ShouldBe("sales.public.orders");
        result.Description.ShouldBe("Customer orders");
        result.Columns.Select(c => c.Name).ShouldBe(["id", "amount"]);
        result.Columns[0].PrimaryKey.ShouldBeTrue();
        result.Columns[1].Type.ShouldBe("decimal");
        result.Examples.ShouldHaveSingleItem().Title.ShouldBe("Big orders");
        result.Examples[0].Variables.ShouldBe("{\"min\":1000}");
    }

    [Fact]
    public async Task DescribeDataset_HiddenTable_LooksLikeAMissingTable()
    {
        var hidden = await Should.ThrowAsync<TableNotFoundException>(() => Catalog().DescribeDatasetAsync(User(), "hr.public.payroll"));
        var missing = await Should.ThrowAsync<TableNotFoundException>(() => Catalog().DescribeDatasetAsync(User(), "hr.public.nothing"));

        hidden.Message.ShouldBe(missing.Message.Replace("nothing", "payroll"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("sales.orders")]
    [InlineData("a.b.c.d")]
    public async Task DescribeDataset_RejectsIdsThatAreNotDomainSchemaTable(string dataset)
    {
        await Should.ThrowAsync<ArgumentException>(() => Catalog().DescribeDatasetAsync(User(), dataset));
    }

    [Fact]
    public async Task SampleRows_ReadsThroughTheGovernedTablePath_WithAClampedCount()
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
        [
            new Dictionary<string, object?> { ["id"] = 1, ["amount"] = 10m }
        ];
        _gateway.ExecuteTableQueryAsync(Arg.Any<ClaimsPrincipal?>(), Orders, Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult((rows, TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>()))));

        var principal = User();
        var result = await Catalog().SampleRowsAsync(principal, "sales.public.orders", count: 500);

        result.Id.ShouldBe("sales.public.orders");
        result.RowCount.ShouldBe(1);
        result.Rows.ShouldBe(rows);
        await _gateway.Received(1).ExecuteTableQueryAsync(principal, Orders, 20, 0,
            Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SampleRows_DeniedDecision_IsForbidden()
    {
        _gateway.ExecuteTableQueryAsync(Arg.Any<ClaimsPrincipal?>(), Orders, Arg.Any<int?>(), Arg.Any<int?>(),
                Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(((IReadOnlyList<IReadOnlyDictionary<string, object?>>)[], TableAccessDecision.Denied(Orders, "no consent"))));

        await Should.ThrowAsync<GatewayForbiddenException>(() => Catalog().SampleRowsAsync(User(), "sales.public.orders", count: null));
    }

    [Fact]
    public async Task SampleRows_HiddenTable_NeverReachesTheDatabase()
    {
        await Should.ThrowAsync<TableNotFoundException>(() => Catalog().SampleRowsAsync(User(), "hr.public.payroll", count: 5));

        await _gateway.DidNotReceiveWithAnyArgs().ExecuteTableQueryAsync(default, default, default, default, default, default, default, default);
    }
}

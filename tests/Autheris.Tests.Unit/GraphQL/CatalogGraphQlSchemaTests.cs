using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class CatalogGraphQlSchemaTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();

    private readonly TableIdentifier _custTableId = new("sales", "dbo", "customers");
    private readonly TableIdentifier _orderTableId = new("sales", "dbo", "orders");

    public CatalogGraphQlSchemaTests()
    {
        var custTable = new TableMetadata
        {
            Identifier = _custTableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "nvarchar(100)" },
                new TableColumn { ColumnName = "is_active", DataType = "bit" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var orderTable = new TableMetadata
        {
            Identifier = _orderTableId,
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "order_id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal(18,2)" }
            ],
            PrimaryKeyColumns = ["order_id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([custTable, orderTable]);

        var relation = new TableRelation
        {
            RelationName = "orders",
            ParentTableIdentifier = _custTableId,
            ChildTableIdentifier = _orderTableId,
            Cardinality = RelationCardinality.OneToMany,
            JoinKeysParent = ["id"],
            JoinKeysChild = ["customer_id"]
        };

        _relationRepo.GetRelationsForTableAsync(_custTableId, Arg.Any<CancellationToken>())
            .Returns([relation]);
        _relationRepo.GetRelationsForTableAsync(_orderTableId, Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private async Task<IRequestExecutor> CreateExecutorAsync(HttpContext? httpContext = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);

        var ctx = httpContext ?? new DefaultHttpContext();
        if (ctx.User == null || !ctx.User.Identity?.IsAuthenticated == true)
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "T1")], "TestAuth");
            ctx.User = new ClaimsPrincipal(identity);
        }

        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    [Fact]
    public async Task Schema_ContainsGeneratedRootFieldsAndLegacyTableField()
    {
        var executor = await CreateExecutorAsync();
        var schema = executor.Schema;

        // Generated root query field
        var queryType = schema.QueryType;
        Assert.NotNull(queryType.Fields["sales_dbo_customers"]);
        Assert.NotNull(queryType.Fields["sales_dbo_orders"]);

        // Legacy root field remains
        Assert.NotNull(queryType.Fields["table"]);

        // Shared types
        Assert.True(schema.Types.ContainsName("AutherisSortDirection"));
        Assert.True(schema.Types.ContainsName("AutherisStringFilter"));
        Assert.True(schema.Types.ContainsName("AutherisIntFilter"));
    }

    [Fact]
    public async Task ExecuteQuery_TranslatesSelectionSetToTreeQueryNodeAndResolvesJson()
    {
        TreeQueryNode? capturedNode = null;
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<TreeQueryNode>(node => capturedNode = node),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var json = """
                [
                    {
                        "id": 1,
                        "name": "Alice",
                        "is_active": true,
                        "orders": [
                            { "order_id": 101, "customer_id": 1, "amount": 99.50 }
                        ]
                    }
                ]
                """;
                return JsonDocument.Parse(json);
            });

        var executor = await CreateExecutorAsync();

        var query = """
        query {
            sales_dbo_customers(
                first: 10,
                offset: 5,
                where: { name: { eq: "Alice" } },
                orderBy: [{ id: DESC }]
            ) {
                id
                name
                is_active
                orders(first: 20) {
                    order_id
                    amount
                }
            }
        }
        """;

        var result = await executor.ExecuteAsync(query);
        var jsonResult = result.ToJson();

        Assert.NotNull(capturedNode);
        Assert.Equal(_custTableId, capturedNode.Table);
        Assert.Equal(10, capturedNode.Limit);
        Assert.Equal(5, capturedNode.Offset);
        Assert.Contains("id", capturedNode.Columns);
        Assert.Contains("name", capturedNode.Columns);
        Assert.Contains("is_active", capturedNode.Columns);

        // Where filter
        var comp = Assert.IsType<TreeComparison>(capturedNode.Where);
        Assert.Equal("name", comp.Column);
        Assert.Equal(TreeFilterOperator.Eq, comp.Operator);
        Assert.Equal("Alice", comp.Value);

        // OrderBy
        var order = Assert.Single(capturedNode.OrderBy);
        Assert.Equal("id", order.Column);
        Assert.True(order.Descending);

        // Child relation
        var rel = Assert.Single(capturedNode.Relations);
        Assert.Equal("orders", rel.ResponseKey);
        Assert.Equal(["id"], rel.ParentColumns);
        Assert.Equal(["customer_id"], rel.ChildColumns);
        Assert.True(rel.IsList);
        Assert.Equal(20, rel.Child.Limit);
        Assert.Contains("order_id", rel.Child.Columns);
        Assert.Contains("amount", rel.Child.Columns);

        // Result content verified
        Assert.Contains("Alice", jsonResult);
        Assert.Contains("99.5", jsonResult);
        Assert.DoesNotContain("errors", jsonResult);
    }

    [Theory]
    [InlineData(typeof(GatewayInvalidQueryException), "INVALID_QUERY")]
    [InlineData(typeof(GatewayForbiddenException), "ACCESS_DENIED")]
    [InlineData(typeof(GatewayThrottledException), "TOO_MANY_REQUESTS")]
    [InlineData(typeof(TimeoutException), "TIMEOUT")]
    public async Task ExecuteQuery_MapsDomainExceptionsToExpectedGraphQLErrorCodes(Type exceptionType, string expectedCode)
    {
        Exception ex = exceptionType switch
        {
            _ when exceptionType == typeof(GatewayInvalidQueryException) => new GatewayInvalidQueryException("Invalid input."),
            _ when exceptionType == typeof(GatewayForbiddenException) => new GatewayForbiddenException("Forbidden."),
            _ when exceptionType == typeof(GatewayThrottledException) => new GatewayThrottledException(5),
            _ when exceptionType == typeof(TimeoutException) => new TimeoutException("Database timeout."),
            _ => new InvalidOperationException()
        };

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns<Task<JsonDocument>>(_ => Task.FromException<JsonDocument>(ex));

        var executor = await CreateExecutorAsync();
        var result = await executor.ExecuteAsync("{ sales_dbo_customers { id } }");

        var json = result.ToJson();
        Assert.Contains("errors", json);
        Assert.Contains(expectedCode, json);
    }

    [Fact]
    public async Task ExecuteQuery_UnderGermanCulture_PreservesDecimalWhereFilter()
    {
        var prevCulture = System.Globalization.CultureInfo.CurrentCulture;
        var prevUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
        var deCulture = new System.Globalization.CultureInfo("de-DE");
        System.Globalization.CultureInfo.CurrentCulture = deCulture;
        System.Globalization.CultureInfo.CurrentUICulture = deCulture;

        try
        {
            TreeQueryNode? capturedNode = null;
            _treeService.ExecuteAsync(
                    Arg.Any<ClaimsPrincipal>(),
                    Arg.Do<TreeQueryNode>(node => capturedNode = node),
                    Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(JsonDocument.Parse("[]"));

            var executor = await CreateExecutorAsync();

            var query = """
            query {
                sales_dbo_orders(where: { amount: { gte: 1.5 } }) {
                    order_id
                    amount
                }
            }
            """;

            var result = await executor.ExecuteAsync(query);
            var json = result.ToJson();
            Assert.DoesNotContain("errors", json);
            Assert.NotNull(capturedNode);
            Assert.NotNull(capturedNode.Where);

            var comp = Assert.IsType<TreeComparison>(capturedNode.Where);
            Assert.Equal("amount", comp.Column);
            Assert.Equal(TreeFilterOperator.Gte, comp.Operator);
            Assert.Equal(1.5m, comp.Value);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = prevCulture;
            System.Globalization.CultureInfo.CurrentUICulture = prevUiCulture;
        }
    }

    [Fact]
    public async Task Schema_WithCollidingCatalogTables_StartsSuccessfullyAndExecutesCanonicalQuery()
    {
        var collidingTable = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders_filter"),
            Table = new Table
            {
                IsActive = true,
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        // Add collidingTable alongside existing custTable and orderTable
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TableMetadata
                {
                    Identifier = _custTableId,
                    Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
                    Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
                    PrimaryKeyColumns = ["id"]
                },
                new TableMetadata
                {
                    Identifier = _orderTableId,
                    Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
                    Columns = [new TableColumn { ColumnName = "order_id", DataType = "int" }],
                    PrimaryKeyColumns = ["order_id"]
                },
                collidingTable
            ]);

        // Creating executor must succeed without SchemaException (G-2 / R-GQL-2)
        var executor = await CreateExecutorAsync();
        Assert.NotNull(executor.Schema);

        // Canonical orderTable query field must exist and be queryable
        Assert.NotNull(executor.Schema.QueryType.Fields["sales_dbo_orders"]);

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[]"));

        var query = "query { sales_dbo_orders { order_id } }";
        var result = await executor.ExecuteAsync(query);
        Assert.DoesNotContain("errors", result.ToJson());
    }
}


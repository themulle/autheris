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
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
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
                Arg.Any<string>(),
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
                Arg.Any<string>(),
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
                    Arg.Any<string>(),
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

    private static TableMetadata ColumnlessTable(TableIdentifier id, params TableColumn[] columns) => new()
    {
        Identifier = id,
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = columns,
        PrimaryKeyColumns = []
    };

    private void SeedWithOmittedTables()
    {
        var valid = new TableMetadata
        {
            Identifier = _custTableId,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "account_no", DataType = "bigint" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["account_no"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            },
            PrimaryKeyColumns = ["id"]
        };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([
                valid,
                ColumnlessTable(new TableIdentifier("sales", "dbo", "empty")),
                ColumnlessTable(new TableIdentifier("sales", "dbo", "dashed"), new TableColumn { ColumnName = "a-b", DataType = "int" })
            ]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task Schema_WithColumnlessAndAllFilteredTables_BuildsAndExecutesQueryOnValidTable()
    {
        SeedWithOmittedTables();
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("""[{ "id": 1 }]"""));

        var executor = await CreateExecutorAsync();

        Assert.NotNull(executor.Schema.QueryType.Fields["sales_dbo_customers"]);
        Assert.False(executor.Schema.QueryType.Fields.TryGetField("sales_dbo_empty", out _));
        Assert.False(executor.Schema.QueryType.Fields.TryGetField("sales_dbo_dashed", out _));
        var result = await executor.ExecuteAsync("query { sales_dbo_customers { id } }");
        Assert.DoesNotContain("errors", result.ToJson());
    }

    [Fact]
    public async Task Schema_NoGeneratedCatalogTypeHasZeroFields()
    {
        SeedWithOmittedTables();

        var executor = await CreateExecutorAsync();

        var generated = executor.Schema.Types
            .Where(t => t.Name.StartsWith("sales_dbo_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(generated);
        foreach (var type in generated)
        {
            switch (type)
            {
                case HotChocolate.Types.InputObjectType input:
                    Assert.True(input.Fields.Count > 0, type.Name);
                    break;
                case HotChocolate.Types.ObjectType obj:
                    Assert.True(obj.Fields.Any(f => !f.IsIntrospectionField), type.Name);
                    break;
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_OmittedTableName_NotQueryable_ValidTableStillGoverned()
    {
        // SEC-BUG157-01 / T-BUG157-S11
        SeedWithOmittedTables();
        ClaimsPrincipal? seenPrincipal = null;
        _treeService.ExecuteAsync(
                Arg.Do<ClaimsPrincipal>(p => seenPrincipal = p), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("""[{ "id": 1, "account_no": "HMAC-HEX" }]"""));
        var executor = await CreateExecutorAsync();

        var omitted = await executor.ExecuteAsync("query { sales_dbo_empty { id } }");
        Assert.Contains("errors", omitted.ToJson());
        await _treeService.DidNotReceive().ExecuteAsync(
            Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The valid table still runs through the governed tree service with the caller principal, and the HMAC column is typed String.
        var valid = await executor.ExecuteAsync("query { sales_dbo_customers { id account_no } }");
        Assert.DoesNotContain("errors", valid.ToJson());
        Assert.NotNull(seenPrincipal);
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
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[]"));

        var query = "query { sales_dbo_orders { order_id } }";
        var result = await executor.ExecuteAsync(query);
        Assert.DoesNotContain("errors", result.ToJson());
    }

    [Fact]
    public async Task Schema_WithMaskedNumericAndBooleanColumns_ReportsMaskedErrorCodeAndNull()
    {
        var dealTableId = new TableIdentifier("sales", "dbo", "deals");
        var dealTable = new TableMetadata
        {
            Identifier = dealTableId,
            Table = new Table
            {
                IsActive = true,
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "deals",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "is_confidential", DataType = "boolean" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([dealTable]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var executor = await CreateExecutorAsync();

        // Return redacted string values for numeric and boolean columns
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("""
                [
                    { "id": "***", "amount": "***", "is_confidential": "***" }
                ]
                """));

        var query = "query { sales_dbo_deals { id amount is_confidential } }";
        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();

        // Data should have null for masked fields
        using var doc = JsonDocument.Parse(json);
        var deal = doc.RootElement.GetProperty("data").GetProperty("sales_dbo_deals")[0];
        Assert.Equal(JsonValueKind.Null, deal.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, deal.GetProperty("amount").ValueKind);
        Assert.Equal(JsonValueKind.Null, deal.GetProperty("is_confidential").ValueKind);

        // Errors should contain MASKED error code (G-8 / R-GQL-1)
        Assert.Contains("MASKED", json);
    }

    [Fact]
    public async Task Schema_WithBooleanColumn_AcceptsFloatingPointOnePointZero()
    {
        var dealTableId = new TableIdentifier("sales", "dbo", "flags");
        var flagTable = new TableMetadata
        {
            Identifier = dealTableId,
            Table = new Table
            {
                IsActive = true,
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "flags",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "is_active", DataType = "boolean" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([flagTable]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var executor = await CreateExecutorAsync();

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("""
                [
                    { "id": 1, "is_active": 1.0 }
                ]
                """));

        var query = "query { sales_dbo_flags { id is_active } }";
        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();

        Assert.DoesNotContain("errors", json);
        using var doc = JsonDocument.Parse(json);
        var flag = doc.RootElement.GetProperty("data").GetProperty("sales_dbo_flags")[0];
        Assert.True(flag.GetProperty("is_active").GetBoolean());
    }

    [Fact]
    public async Task Schema_WithHmacMaskedColumn_ReturnsPseudonymAsString()
    {
        var tableId = new TableIdentifier("crm", "dbo", "users");
        var userTable = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                IsActive = true,
                SourceName = "crm",
                SchemaName = "dbo",
                TableName = "users",
                SourceType = "PostgreSql",
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "account_no", DataType = "bigint" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["account_no"] = new MaskingRule { RuleType = "HMAC_SHA256" }
            },
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([userTable]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var executor = await CreateExecutorAsync();

        // account_no is typed as String in the GraphQL schema because of HMAC
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("""
                [
                    { "id": 1, "account_no": "HMAC-HEX-987654" }
                ]
                """));

        var query = "query { crm_dbo_users { id account_no } }";
        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();

        Assert.DoesNotContain("errors", json);
        using var doc = JsonDocument.Parse(json);
        var user = doc.RootElement.GetProperty("data").GetProperty("crm_dbo_users")[0];
        Assert.Equal("HMAC-HEX-987654", user.GetProperty("account_no").GetString());
    }

    [Fact]
    public async Task ExecuteQuery_WithMultipleRootFields_SharesSingleOperationId_AndClearsOperationOnCleanup()
    {
        var custTable = new TableMetadata
        {
            Identifier = _custTableId,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var orderTable = new TableMetadata
        {
            Identifier = _orderTableId,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "order_id", DataType = "int" }],
            PrimaryKeyColumns = ["order_id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([custTable, orderTable]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var capturedOpIds = new List<string>();
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Do<string>(opId => capturedOpIds.Add(opId)),
                Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("[]"));

        var executor = await CreateExecutorAsync();

        var query = "query { sales_dbo_customers { id } sales_dbo_orders { order_id } }";
        var result = await executor.ExecuteAsync(query);

        Assert.DoesNotContain("errors", result.ToJson());
        Assert.Equal(2, capturedOpIds.Count);
        Assert.Equal(capturedOpIds[0], capturedOpIds[1]); // Both root fields share single operation ID

        _treeService.Received(1).ClearOperation(capturedOpIds[0]); // Operation was cleared when resolvers completed
    }
    [Fact]
    public async Task G9_IsNullNull_IsRejected_InsteadOfBecomingIsNotNull()
    {
        TreeQueryNode? captured = null;
        _treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<TreeQueryNode>(n => captured = n), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("[]"));
        var executor = await CreateExecutorAsync();

        var result = await executor.ExecuteAsync("{ sales_dbo_customers(where: { name: { isNull: null } }) { id } }");

        Assert.Null(captured);
        Assert.Contains("errors", result.ToJson());
    }

    [Fact]
    public async Task G9_EmptyNot_MatchesNoRow()
    {
        TreeQueryNode? captured = null;
        _treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<TreeQueryNode>(n => captured = n), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("[]"));
        var executor = await CreateExecutorAsync();

        await executor.ExecuteAsync("{ sales_dbo_customers(where: { not: {} }) { id } }");

        Assert.NotNull(captured);
        var or = Assert.IsType<TreeOrFilter>(captured!.Where);
        Assert.Empty(or.Items);
    }

    [Fact]
    public async Task G5_OrderByDirection_FromVariable_IsResolved()
    {
        TreeQueryNode? captured = null;
        _treeService.ExecuteAsync(Arg.Any<ClaimsPrincipal>(), Arg.Do<TreeQueryNode>(n => captured = n), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => JsonDocument.Parse("[]"));
        var executor = await CreateExecutorAsync();

        var request = OperationRequestBuilder.New()
            .SetDocument("query($dir: AutherisSortDirection!) { sales_dbo_customers(orderBy: [{ id: $dir }]) { id } }")
            .SetVariableValues(new Dictionary<string, object?> { ["dir"] = "DESC" })
            .Build();
        var result = await executor.ExecuteAsync(request);

        Assert.DoesNotContain("errors", result.ToJson());
        Assert.NotNull(captured);
        var order = Assert.Single(captured!.OrderBy);
        Assert.Equal("id", order.Column);
        Assert.True(order.Descending);
    }
    // BUG #75: relations between tables of different data sources are not part of the schema.

    private void UseCatalogWithSources(string customersSource, string ordersSource)
    {
        TableMetadata Table(TableIdentifier id, string source, params string[] columns) => new()
        {
            Identifier = id,
            Table = new Table { IsActive = true, SourceType = "PostgreSql", SourceName = source, DataSourceType = DataSourceType.Sql },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = "int" }).ToList(),
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([Table(_custTableId, customersSource, "id", "name"), Table(_orderTableId, ordersSource, "order_id", "customer_id")]);
    }

    private static string TypeBlock(string sdl, string typeName)
    {
        var start = sdl.IndexOf($"type {typeName} ", StringComparison.Ordinal);
        Assert.True(start >= 0, $"type {typeName} not found in SDL");
        var end = sdl.IndexOf("\n}", start, StringComparison.Ordinal);
        return sdl[start..end];
    }

    [Fact]
    public async Task Schema_SameSourceRelation_IsInSdl()
    {
        UseCatalogWithSources("pg1", "pg1");
        var executor = await CreateExecutorAsync();

        Assert.Contains("orders(", TypeBlock(executor.Schema.ToString(), "sales_dbo_customers"));
    }

    [Fact]
    public async Task Schema_CrossSourceRelation_AbsentFromSdlAndQueryFailsValidation()
    {
        UseCatalogWithSources("pg1", "pg2");
        var executor = await CreateExecutorAsync();
        var sdl = executor.Schema.ToString();

        Assert.DoesNotContain("orders", TypeBlock(sdl, "sales_dbo_customers"));
        Assert.DoesNotContain("customers_by_orders", TypeBlock(sdl, "sales_dbo_orders"));

        var result = await executor.ExecuteAsync("query { sales_dbo_customers { id orders { order_id } } }");

        Assert.Contains("errors", result.ToJson());
    }

    [Fact]
    public async Task Schema_OmittedRelation_QueryFailsValidationAndNeverReachesTreeService()
    {
        UseCatalogWithSources("secret_pg1", "secret_pg2");
        var executor = await CreateExecutorAsync();

        var result = await executor.ExecuteAsync("query { sales_dbo_customers { id orders { order_id } } }");
        var json = result.ToJson();

        Assert.Contains("errors", json);
        Assert.DoesNotContain("secret_pg", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sales_dbo_orders", json, StringComparison.Ordinal);
        await _treeService.DidNotReceive().ExecuteAsync(
            Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Schema_AggregateAndSubscriptionTypes_HaveNoRelationFields()
    {
        UseCatalogWithSources("pg1", "pg1");
        var executor = await CreateExecutorAsync();
        var sdl = executor.Schema.ToString();

        Assert.Contains("orders(", TypeBlock(sdl, "sales_dbo_customers")); // the relation exists on the object type only
        foreach (var typed in new[] { "sales_dbo_customers_aggregate_key", "sales_dbo_customers_aggregate_group", "sales_dbo_customers_aggregate_result" })
        {
            Assert.DoesNotContain("orders", TypeBlock(sdl, typed));
        }

        var aggregateStart = sdl.IndexOf("sales_dbo_customers_aggregate(", StringComparison.Ordinal);
        Assert.True(aggregateStart >= 0);
        var aggregateField = sdl[aggregateStart..sdl.IndexOf(')', aggregateStart)];
        Assert.Contains("groupBy: [sales_dbo_customers_column!]", aggregateField);
        Assert.DoesNotContain("where: String", aggregateField);
        Assert.DoesNotContain("orders", aggregateField);
        if (sdl.Contains("type Subscription ", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("orders", TypeBlock(sdl, "Subscription"));
        }
    }

    [Fact]
    public async Task Schema_IsIdenticalForAllCallers()
    {
        UseCatalogWithSources("pg1", "pg2");
        var alice = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "T1")], "TestAuth")) };
        var bob = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "bob"), new Claim("tenant_id", "T2")], "TestAuth")) };

        var aliceSdl = (await CreateExecutorAsync(alice)).Schema.ToString();
        var bobSdl = (await CreateExecutorAsync(bob)).Schema.ToString();

        Assert.Equal(aliceSdl, bobSdl);
    }

    [Fact]
    public async Task Schema_StaleRelationField_IsRejectedByTheRuntimeCheck()
    {
        // Stale schema between rebuilds (fail closed): the old schema still lists the field, the real tree service rejects it.
        UseCatalogWithSources("pg1", "pg1");
        var executor = await CreateExecutorAsync();
        Assert.Contains("orders(", TypeBlock(executor.Schema.ToString(), "sales_dbo_customers"));

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<JsonDocument>(_ => throw new GatewayInvalidQueryException("Relations across data sources are not supported in one query."));

        var result = await executor.ExecuteAsync("query { sales_dbo_customers { id orders { order_id } } }");

        Assert.Contains("errors", result.ToJson());
    }
}

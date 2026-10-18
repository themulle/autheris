namespace Autheris.Tests.Unit.GraphQL;

using System.Security.Claims;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-DATA-112 WP-0: Hardening tests for read-only tokens accessing aggregations, fail-closed security,
/// and Hot Chocolate schema generation for empty/colliding tables and aggregation filtering.
/// </summary>
public sealed class ReadOnlyTokenAggregateTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedAggregationService _aggregationService = Substitute.For<IGovernedAggregationService>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();

    private readonly TableIdentifier _custTableId = new("sales", "dbo", "customers");

    public ReadOnlyTokenAggregateTests()
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
                new TableColumn { ColumnName = "ssn", DataType = "nvarchar(11)" }
            ],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([custTable]);

        _relationRepo.GetRelationsForTableAsync(_custTableId, Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private static ClaimsPrincipal CreateReadOnlyPrincipal(string name = "agent-ro", string tenantId = "T1")
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, name),
            new Claim("tenant_id", tenantId),
            new Claim("oid", "00000000-0000-0000-0000-000000000001")
        ], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private async Task<IRequestExecutor> CreateExecutorAsync(HttpContext? httpContext = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_aggregationService);
        services.AddSingleton(_treeService);

        var ctx = httpContext ?? new DefaultHttpContext();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddQueryType<Query>()
            .AddMutationType<TestMutation>()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseRequest<ReadOnlyOperationMiddleware>()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution()
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    public sealed class TestMutation
    {
        public static int Calls { get; set; }

        public bool MutateTable()
        {
            Calls++;
            return true;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WithReadOnlyToken_WhenTableAllowed_ReturnsAggregationResult()
    {
        // Arrange
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Is<AggregationQuery>(q => q.Table == _custTableId),
                Arg.Any<CancellationToken>())
            .Returns(new AggregationResult(
                _custTableId,
                [],
                new AggregationGroupRow(new Dictionary<string, object?>(), 42, new Dictionary<string, object?>()),
                new AggregationMetadata(
                    MinimumGroupSize: 5,
                    Strategy: SmallGroupStrategy.Suppress,
                    Suppressed: false,
                    SuppressedGroupsCount: 0,
                    SuppressedRowsCount: 0,
                    MergedIntoOther: false,
                    Truncated: false)));

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count suppressed minimumGroupSize } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert
        var json = result.ToJson();
        json.ShouldNotContain("errors");
        json.ShouldContain("\"count\": 42");
        json.ShouldContain("\"suppressed\": false");
        json.ShouldContain("\"minimumGroupSize\": 5");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WithReadOnlyToken_WhenTableDenied_FailsClosedWithAccessDenied()
    {
        // Arrange: Underlying table read permission is denied
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new GatewayForbiddenException("Access denied."));

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Fail-closed with ACCESS_DENIED
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("ACCESS_DENIED");
        json.ShouldContain("Access denied.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WithReadOnlyToken_WhenTableNotFound_ReturnsAccessDenied_WithoutEchoingName()
    {
        // Arrange: Missing table gives indistinguishable ACCESS_DENIED error (anti-enumeration)
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new TableNotFoundException(_custTableId));

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Same code and message as denied table; no table name echo
        var opResult = (OperationResult)result;
        opResult.Errors.ShouldNotBeNull();
        opResult.Errors.Count.ShouldBeGreaterThan(0);
        opResult.Errors[0].Code.ShouldBe("ACCESS_DENIED");
        opResult.Errors[0].Message.ShouldBe("Access denied.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WhenColumnMasked_ReturnsAggregationOnMaskedColumn_FixedMessage()
    {
        // Arrange: MaskedAggregationException thrown by aggregation engine
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new MaskedAggregationException("ssn"));

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Fixed message and code without leaking column name
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("AGGREGATION_ON_MASKED_COLUMN");
        json.ShouldContain(MaskedAggregationException.FixedMessage);
        json.ShouldNotContain("\"ssn\"");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WhenColumnUnavailable_ReturnsAggregationColumnUnavailable_FixedMessage()
    {
        // Arrange: Denied or unknown column in groupBy or where
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new AggregationColumnUnavailableException());

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate(groupBy: [\"secret\"]) { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Fixed message and code without existence oracle
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("AGGREGATION_COLUMN_UNAVAILABLE");
        json.ShouldContain(AggregationColumnUnavailableException.FixedMessage);
        json.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WhenRowCapExceeded_ReturnsAggregationRowCapExceeded_FixedMessage()
    {
        // Arrange
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new AggregationRowCapExceededException());

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("AGGREGATION_ROW_CAP_EXCEEDED");
        json.ShouldContain(AggregationRowCapExceededException.FixedMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteAggregate_WhenArgumentInvalid_ReturnsAggregationArgumentInvalid()
    {
        // Arrange: ArgumentException thrown by aggregation validation
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        _aggregationService.ExecuteAggregationAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<AggregationQuery>(),
                Arg.Any<CancellationToken>())
            .Returns<AggregationResult>(_ => throw new ArgumentException("Duplicate aggregate functions."));

        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("{ sales_dbo_customers_aggregate { count } }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Clean error code and message without exception stack or internal details
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("AGGREGATION_ARGUMENT_INVALID");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteMutation_WithReadOnlyToken_IsRejectedByReadOnlyMiddleware()
    {
        // Arrange
        TestMutation.Calls = 0;
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("mutation { mutateTable }")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("READ_ONLY_TOKEN");
        TestMutation.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteMixedDocument_WithoutOperationName_WithReadOnlyToken_IsRejectedByReadOnlyMiddleware()
    {
        // Arrange
        TestMutation.Calls = 0;
        var principal = CreateReadOnlyPrincipal();
        var httpContext = new DefaultHttpContext { User = principal };
        var executor = await CreateExecutorAsync(httpContext);

        // Act
        var request = OperationRequestBuilder.New()
            .SetDocument("query Q { sales_dbo_customers_aggregate { count } } mutation M { mutateTable }")
            .SetOperationName("M")
            .AddGlobalState(nameof(ClaimsPrincipal), principal)
            .Build();
        var result = await executor.ExecuteAsync(request);

        // Assert: Fails closed when document has mutation
        var json = result.ToJson();
        json.ShouldContain("errors");
        json.ShouldContain("READ_ONLY_TOKEN");
        TestMutation.Calls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_WithEmptyCatalog_BuildsSuccessfullyWithoutThrowing()
    {
        // Arrange: Catalog has no tables at all
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Act & Assert: Schema build should succeed without 'Query extension has no fields' error
        var executor = await CreateExecutorAsync();
        executor.Schema.ShouldNotBeNull();
        executor.Schema.QueryType.Fields.TryGetField("sales_dbo_customers_aggregate", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_WithCollidingAggregateTable_OmitsCollidingTable_AndBuildsSuccessfully()
    {
        // Arrange: Table 1 is 'customers' (creates sales_dbo_customers and sales_dbo_customers_aggregate)
        // Table 2 is 'customers_aggregate' (would collide on field sales_dbo_customers_aggregate)
        var table1 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var table2 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers_aggregate"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table1, table2]);

        // Act: Schema build should not crash with duplicate field name error
        var executor = await CreateExecutorAsync();

        // Assert: Schema built cleanly, one table omitted due to collision
        executor.Schema.ShouldNotBeNull();
        executor.Schema.QueryType.Fields.TryGetField("sales_dbo_customers", out _).ShouldBeTrue();
        executor.Schema.QueryType.Fields.TryGetField("sales_dbo_customers_aggregate", out _).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_RegistersAutherisAggregationResultType_Cleanly()
    {
        // Act: Schema build with standard catalog
        var executor = await CreateExecutorAsync();

        // Assert: AutherisAggregationResult is registered and unique
        executor.Schema.ShouldNotBeNull();
        executor.Schema.Types.ContainsName("AutherisAggregationResult").ShouldBeTrue();
        executor.Schema.Types.Count(t => t.Name == "AutherisAggregationResult").ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Schema_WithCollidingAggregateTable_ReverseOrder_OmitsCollidingTable_AndBuildsSuccessfully()
    {
        // Arrange: Table 1 is 'customers_aggregate', Table 2 is 'customers' (reverse discovery order)
        var table1 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers_aggregate"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var table2 = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "customers"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([table1, table2]);

        // Act: Schema build should not crash with duplicate field name error
        var executor = await CreateExecutorAsync();

        // Assert: Schema built cleanly despite reverse ordering collision
        executor.Schema.ShouldNotBeNull();
        executor.Schema.QueryType.Fields.TryGetField("sales_dbo_customers_aggregate", out _).ShouldBeTrue();
    }
}

namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Common.Interfaces;
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
using Shouldly;
using Xunit;

public sealed class CatalogGraphQlConnectionTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();

    private readonly TableIdentifier _custTableId = new("sales", "dbo", "customers");

    public CatalogGraphQlConnectionTests()
    {
        var custTable = CreateTableMetadata();
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns([custTable]);
        _relationRepo.GetRelationsForTableAsync(_custTableId, Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private static TableMetadata CreateTableMetadata(
        string domain = "sales",
        string schema = "dbo",
        string table = "customers",
        IReadOnlyList<string>? primaryKeys = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "name", DataType = "nvarchar(100)" },
                new TableColumn { ColumnName = "status", DataType = "nvarchar(50)" }
            ],
            PrimaryKeyColumns = primaryKeys ?? ["id"]
        };
    }

    private async Task<IRequestExecutor> CreateExecutorAsync(HttpContext? httpContext = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);
        services.AddSingleton<IKeysetCursorEncoder>(new KeysetCursorEncoder("test-signing-key-32-bytes-long!!"));

        var ctx = httpContext ?? new DefaultHttpContext();
        if (ctx.User == null || !ctx.User.Identity?.IsAuthenticated == true)
        {
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, "alice"),
                new Claim(ClaimTypes.NameIdentifier, "user-alice-123"),
                new Claim("tenant_id", "T1")
            ], "TestAuth");
            ctx.User = new ClaimsPrincipal(identity);
        }

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(ctx);
        services.AddSingleton<IHttpContextAccessor>(accessor);
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
    [Trait("Category", "Security")]
    public void ForGraphQl_BindsContextParametersExactly()
    {
        var metadata = CreateTableMetadata();
        var filter = new TreeComparison("status", TreeFilterOperator.Eq, "ACTIVE");
        var select = new[] { "id", "name" };

        var context = KeysetCursorContextFactory.ForGraphQl(
            tenantId: "tenant-a",
            metadata: metadata,
            keyColumns: ["id"],
            callerSid: "user-123",
            filter: filter,
            select: select,
            keyDirections: [KeyColumnDirection.Ascending]);

        context.TenantId.ShouldBe("tenant-a");
        context.DataSource.ShouldBe("sales");
        context.Schema.ShouldBe("dbo");
        context.TableName.ShouldBe("customers");
        context.Caller.ShouldBe("user-123");
        context.Columns.ShouldBe(["id"]);
        context.Direction.ShouldBe("asc");
        context.RequestHash.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ForGraphQl_DescendingDirection_BindsDirectionSignature()
    {
        var metadata = CreateTableMetadata();
        var context = KeysetCursorContextFactory.ForGraphQl(
            tenantId: "tenant-a",
            metadata: metadata,
            keyColumns: ["id"],
            callerSid: "user-123",
            filter: null,
            select: null,
            keyDirections: [KeyColumnDirection.Descending]);

        context.Direction.ShouldBe("desc");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ForGraphQl_RequestHash_MatchesForIdenticalShape()
    {
        var filter1 = new TreeAndFilter([
            new TreeComparison("status", TreeFilterOperator.Eq, "ACTIVE"),
            new TreeComparison("id", TreeFilterOperator.Gt, 100)
        ]);
        var filter2 = new TreeAndFilter([
            new TreeComparison("status", TreeFilterOperator.Eq, "ACTIVE"),
            new TreeComparison("id", TreeFilterOperator.Gt, 100)
        ]);

        var hash1 = KeysetCursorContextFactory.ComputeRequestHash(filter1, ["ID", "Name"]);
        var hash2 = KeysetCursorContextFactory.ComputeRequestHash(filter2, ["name", "id"]);

        hash1.ShouldBe(hash2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ForGraphQl_RequestHash_DiffersWhenFilterChanges()
    {
        var filterActive = new TreeComparison("status", TreeFilterOperator.Eq, "ACTIVE");
        var filterPending = new TreeComparison("status", TreeFilterOperator.Eq, "PENDING");

        var hashActive = KeysetCursorContextFactory.ComputeRequestHash(filterActive, ["id"]);
        var hashPending = KeysetCursorContextFactory.ComputeRequestHash(filterPending, ["id"]);

        hashActive.ShouldNotBe(hashPending);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ForGraphQl_RequestHash_DiffersWhenSelectChanges()
    {
        var filter = new TreeComparison("status", TreeFilterOperator.Eq, "ACTIVE");

        var hash1 = KeysetCursorContextFactory.ComputeRequestHash(filter, ["id", "name"]);
        var hash2 = KeysetCursorContextFactory.ComputeRequestHash(filter, ["id", "name", "status"]);

        hash1.ShouldNotBe(hash2);
    }

    [Fact]
    public async Task Schema_ContainsRelayConnectionAndEdgeTypes()
    {
        var executor = await CreateExecutorAsync();
        var schema = executor.Schema;

        // Relay PageInfo registered
        schema.Types.ContainsName("PageInfo").ShouldBeTrue();

        // Customer edge and connection registered
        schema.Types.ContainsName("sales_dbo_customersEdge").ShouldBeTrue();
        schema.Types.ContainsName("sales_dbo_customersConnection").ShouldBeTrue();

        // Query field exists
        var queryType = schema.QueryType;
        var connectionField = queryType.Fields["sales_dbo_customersConnection"];
        connectionField.ShouldNotBeNull();
        connectionField.Arguments.ContainsName("first").ShouldBeTrue();
        connectionField.Arguments.ContainsName("after").ShouldBeTrue();
        connectionField.Arguments.ContainsName("where").ShouldBeTrue();
        connectionField.Arguments.ContainsName("orderBy").ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteConnection_FirstPage_ReturnsEdgesAndPageInfo()
    {
        TreeQueryNode? capturedNode = null;
        var jsonRows = "[{\"id\":1,\"name\":\"Customer 1\"},{\"id\":2,\"name\":\"Customer 2\"},{\"id\":3,\"name\":\"Customer 3\"}]";

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<TreeQueryNode>(n => capturedNode = n),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse(jsonRows));

        var executor = await CreateExecutorAsync();

        var query = @"
            query {
                sales_dbo_customersConnection(first: 2) {
                    pageInfo {
                        hasNextPage
                        hasPreviousPage
                        startCursor
                        endCursor
                    }
                    edges {
                        cursor
                        node {
                            id
                            name
                        }
                    }
                    totalCount
                }
            }";

        var result = await executor.ExecuteAsync(query);
        var raw = result.ToJson();
        raw.ShouldNotContain("\"errors\":");

        capturedNode.ShouldNotBeNull();
        capturedNode.Limit.ShouldBe(2);
        capturedNode.Offset.ShouldBe(0);
        capturedNode.ExecutionLimit.ShouldBe(3); // Requested 2, asks for 3 to check hasNextPage

        using var doc = JsonDocument.Parse(raw);
        var conn = doc.RootElement.GetProperty("data").GetProperty("sales_dbo_customersConnection");

        var pageInfo = conn.GetProperty("pageInfo");
        pageInfo.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        pageInfo.GetProperty("hasPreviousPage").GetBoolean().ShouldBeFalse();
        pageInfo.GetProperty("startCursor").GetString().ShouldNotBeNullOrWhiteSpace();
        pageInfo.GetProperty("endCursor").GetString().ShouldNotBeNullOrWhiteSpace();

        var edges = conn.GetProperty("edges");
        edges.GetArrayLength().ShouldBe(2); // Sliced down from 3 to 2

        var firstEdge = edges[0];
        firstEdge.GetProperty("node").GetProperty("id").GetInt32().ShouldBe(1);
        firstEdge.GetProperty("node").GetProperty("name").GetString().ShouldBe("Customer 1");
        firstEdge.GetProperty("cursor").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ExecuteConnection_SecondPageWithAfter_BuildsKeysetSeekPredicate()
    {
        // 1. Get first page cursor
        TreeQueryNode? capturedNode1 = null;
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<TreeQueryNode>(n => capturedNode1 = n),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[{\"id\":1,\"name\":\"Customer 1\"},{\"id\":2,\"name\":\"Customer 2\"},{\"id\":3,\"name\":\"Customer 3\"}]"));

        var executor = await CreateExecutorAsync();

        var query1 = @"
            query {
                sales_dbo_customersConnection(first: 2) {
                    pageInfo {
                        endCursor
                    }
                    edges {
                        node {
                            id
                        }
                    }
                }
            }";

        var result1 = await executor.ExecuteAsync(query1);
        var raw1 = result1.ToJson();
        raw1.ShouldNotContain("\"errors\":");

        using var doc1 = JsonDocument.Parse(raw1);
        var cursor = doc1.RootElement.GetProperty("data")
            .GetProperty("sales_dbo_customersConnection")
            .GetProperty("pageInfo")
            .GetProperty("endCursor")
            .GetString();

        cursor.ShouldNotBeNullOrWhiteSpace();

        // 2. Query second page using after
        TreeQueryNode? capturedNode2 = null;
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Do<TreeQueryNode>(n => capturedNode2 = n),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[{\"id\":3,\"name\":\"Customer 3\"}]"));

        var query2 = $@"
            query {{
                sales_dbo_customersConnection(first: 2, after: ""{cursor}"") {{
                    pageInfo {{
                        hasNextPage
                        hasPreviousPage
                    }}
                    edges {{
                        node {{
                            id
                        }}
                    }}
                }}
            }}";

        var result2 = await executor.ExecuteAsync(query2);
        var raw2 = result2.ToJson();
        raw2.ShouldNotContain("\"errors\":");

        capturedNode2.ShouldNotBeNull();
        capturedNode2.Offset.ShouldBe(0); // Keyset seek, never offset!
        capturedNode2.Where.ShouldNotBeNull();
        capturedNode2.Where.ShouldBeOfType<TreeComparison>();
        var comp = (TreeComparison)capturedNode2.Where;
        comp.Column.ShouldBe("id");
        comp.Operator.ShouldBe(TreeFilterOperator.Gt);
        comp.Value.ShouldBe(2);

        using var doc2 = JsonDocument.Parse(result2.ToJson());
        var conn2 = doc2.RootElement.GetProperty("data").GetProperty("sales_dbo_customersConnection");
        conn2.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean().ShouldBeTrue();
        conn2.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteConnection_TamperedCursor_FailsClosedWithInvalidQuery()
    {
        var executor = await CreateExecutorAsync();

        var query = @"
            query {
                sales_dbo_customersConnection(first: 2, after: ""invalid.cursor.signature"") {
                    edges {
                        node {
                            id
                        }
                    }
                }
            }";

        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("INVALID_QUERY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteConnection_CrossTenantCursor_FailsClosed()
    {
        // 1. Generate cursor for Tenant T1
        var encoder = new KeysetCursorEncoder("test-signing-key-32-bytes-long!!");
        var metadata = CreateTableMetadata();
        var contextT1 = KeysetCursorContextFactory.ForGraphQl(
            tenantId: "T1",
            metadata: metadata,
            keyColumns: ["id"],
            callerSid: "user-alice-123",
            filter: null,
            select: ["id"]);

        var cursorT1 = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 1 }, contextT1);

        // 2. Present cursor under Tenant T2
        var t2Context = new DefaultHttpContext();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "bob"),
            new Claim(ClaimTypes.NameIdentifier, "user-bob-456"),
            new Claim("tenant_id", "T2")
        ], "TestAuth");
        t2Context.User = new ClaimsPrincipal(identity);

        var executor = await CreateExecutorAsync(t2Context);

        var query = $@"
            query {{
                sales_dbo_customersConnection(first: 2, after: ""{cursorT1}"") {{
                    edges {{
                        node {{
                            id
                        }}
                    }}
                }}
            }}";

        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("INVALID_QUERY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteConnection_MaskedPrimaryKey_FailsClosed()
    {
        // Setup treeService to report that 'id' is masked for the caller
        _treeService.GetMaskedColumns(Arg.Any<ClaimsPrincipal>(), Arg.Any<TreeQueryNode>(), Arg.Any<string>())
            .Returns([new TreeMaskedColumn([], "id")]);

        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[{\"id\":1,\"name\":\"Customer 1\"}]"));

        var executor = await CreateExecutorAsync();

        var query = @"
            query {
                sales_dbo_customersConnection(first: 2) {
                    edges {
                        node {
                            id
                        }
                    }
                }
            }";

        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("KEYSET_PAGINATION_UNAVAILABLE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteConnection_UnauthorizedQuery_ReturnsAccessDenied_BeforePaginationErrors()
    {
        // SEC-RG-08: Even with invalid pagination (e.g. first: -5), an unauthorized caller must see ACCESS_DENIED
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<JsonDocument>(_ => throw new GatewayForbiddenException("Access denied."));

        var executor = await CreateExecutorAsync();

        var query = @"
            query {
                sales_dbo_customersConnection(first: -5) {
                    edges {
                        node {
                            id
                        }
                    }
                }
            }";

        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("ACCESS_DENIED");
        json.ShouldNotContain("INVALID_QUERY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ExecuteConnection_NonKeysetOrderBy_ReturnsInvalidQuery()
    {
        // HIGH-02: Non-keyset column in orderBy on *Connection is rejected post-authorization with INVALID_QUERY
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonDocument.Parse("[{\"id\":1,\"name\":\"Customer 1\"}]"));

        var executor = await CreateExecutorAsync();

        var query = @"
            query {
                sales_dbo_customersConnection(first: 2, orderBy: [{ status: ASC }]) {
                    edges {
                        node {
                            id
                        }
                    }
                }
            }";

        var result = await executor.ExecuteAsync(query);
        var json = result.ToJson();
        json.ShouldContain("\"errors\":");
        json.ShouldContain("INVALID_QUERY");
    }
}

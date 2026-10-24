using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// #318 (GAP-052), #319 (GAP-053), #330 (GAP-057): the real catalog schema, cost rule, error filter and shield against the real
/// HotChocolate engine. G0 pin: before the fix the validation-phase <see cref="QueryCostAnalyzerRule"/> answered every variable page size
/// and every out-of-range literal with <c>QUERY_TOO_COMPLEX</c> (a validation error the shield maps to <c>GRAPHQL_VALIDATION_FAILED</c>),
/// so the tree builder never saw the value; the tests below fail while that is so.
/// </summary>
[Collection("CatalogRelation")]
public sealed class GraphQlPagingEngineTests
{
    private const string PageRangeBody = "{\"errors\":[{\"message\":\"The page size or offset is out of range.\",\"extensions\":{\"code\":\"INVALID_QUERY\"}}]}";

    private static readonly TableIdentifier Customers = new("sales", "dbo", "customers");
    private static readonly TableIdentifier Secrets = new("sales", "dbo", "secrets");

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ITableRelationRepository _relationRepo = Substitute.For<ITableRelationRepository>();
    private readonly IGovernedTreeQueryService _treeService = Substitute.For<IGovernedTreeQueryService>();
    private readonly List<TreeQueryNode> _nodes = [];

    public GraphQlPagingEngineTests()
    {
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([Table(Customers), Table(Secrets)]);
        _relationRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);
        _treeService.ExecuteAsync(
                Arg.Any<ClaimsPrincipal>(),
                Arg.Any<TreeQueryNode>(),
                Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var node = call.ArgAt<TreeQueryNode>(1);
                _nodes.Add(node);
                if (node.Table == Secrets)
                {
                    throw new GatewayForbiddenException("Access denied.");
                }

                return JsonDocument.Parse("[]");
            });
    }

    private static TableMetadata Table(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "nvarchar(100)" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    private async Task<IRequestExecutor> CreateAsync(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);

        var services = new ServiceCollection();
        services.AddSingleton(_metadataRepo);
        services.AddSingleton(_relationRepo);
        services.AddSingleton(_treeService);
        services.AddSingleton(env);
        services.AddSingleton(_ => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance));
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant_id", "T1")], "TestAuth"))
        };
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = ctx });
        services.AddSingleton(Options.Create(new GatewayOptions()));

        return await services
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Query>()
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseRequest<CatalogOperationCleanupMiddleware>()
            .UseDefaultPipeline()
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxAllowedCost: 5000, maxResponseRows: 5000, maxRootFields: 25))
            .AddTypeModule(sp => new CatalogGraphQlTypeModule(
                sp.GetRequiredService<ITableMetadataRepository>(),
                sp.GetRequiredService<ITableRelationRepository>()))
            .BuildRequestExecutorAsync();
    }

    private async Task<(string Json, IReadOnlyList<IError> Errors)> RunAsync(string environmentName, string query, string? variablesJson = null)
    {
        var executor = await CreateAsync(environmentName);
        var request = variablesJson is null
            ? OperationRequest.FromSourceText(query)
            : OperationRequest.FromSourceText(query, variableValues: JsonDocument.Parse(variablesJson));
        await using var result = await executor.ExecuteAsync(request);
        return (Compact(result.ToJson()), ((OperationResult)result).Errors ?? []);
    }

    private static string Compact(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private const string PagedQuery = "query($f: Int, $o: Int) { sales_dbo_customers(first: $f, offset: $o, orderBy: [{ id: ASC }]) { id } }";

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task FirstOffsetVariables_FiveTen(string env)
    {
        var (json, errors) = await RunAsync(env, PagedQuery, "{\"f\":5,\"o\":10}");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(5);
        _nodes.Single().Offset.ShouldBe(10);
    }

    [Fact]
    public async Task NullOffset_IsZero_AndNullFirst_IsTheDefaultPage()
    {
        var (json, errors) = await RunAsync("Development", PagedQuery, "{\"f\":null,\"o\":null}");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(100);
        _nodes.Single().Offset.ShouldBe(0);
    }

    [Fact]
    public async Task ZeroFirst_NullOffset_ZeroRows()
    {
        var (json, errors) = await RunAsync("Development", PagedQuery, "{\"f\":0,\"o\":null}");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(0);
        _nodes.Single().Offset.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_06_NullFirst_DefaultPageBounded_DeclaredDefaultIgnored()
    {
        var (json, errors) = await RunAsync(
            "Development",
            "query($f: Int = 1) { sales_dbo_customers(first: $f, orderBy: [{ id: ASC }]) { id } }",
            "{\"f\":null}");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(100);
    }

    [Fact]
    public async Task AbsentVariable_UsesDeclaredDefault()
    {
        var (json, errors) = await RunAsync(
            "Development",
            "query($f: Int = 7) { sales_dbo_customers(first: $f, orderBy: [{ id: ASC }]) { id } }");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(7);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_05_UnboundVariableFirst_StaysWithinTheGatewayBudgetAtRuntime()
    {
        // The validation pass relaxes a variable page size without a default; the value itself is priced where it is known. A runtime value over
        // the budget is therefore still a complexity error (never a data query).
        var (json, errors) = await RunAsync("Development", PagedQuery, "{\"f\":5000,\"o\":0}");

        errors.ShouldBeEmpty(json); // validation pass: allowed; the runtime pass (CostAndQuotaMiddleware) prices it (not in this host)
        QueryCostAnalyzerRule.CalculateCost(
                HotChocolate.Language.Utf8GraphQLParser.Parse(PagedQuery),
                (await CreateAsync("Development")).Schema,
                maxResponseRows: 5000,
                variableValues: new Dictionary<string, object?> { ["f"] = 5000, ["o"] = 0 })
            .ShouldBeGreaterThan(5000);
    }

    [Theory]
    [InlineData("Development", "100000")]
    [InlineData("Production", "100000")]
    [InlineData("Development", "-1")]
    [InlineData("Production", "-1")]
    public async Task FirstOutOfRange_Literal_ReachesTheTreeBuilder_FixedBody(string env, string first)
    {
        var (json, errors) = await RunAsync(env, "{ sales_dbo_customers(first: " + first + ") { id } }");

        errors.Count.ShouldBe(1, json);
        json.ShouldBe(PageRangeBody);
    }

    [Theory]
    [InlineData("Development", "100000")]
    [InlineData("Production", "100000")]
    [InlineData("Production", "-1")]
    public async Task FirstOutOfRange_Variable_FixedBody(string env, string first)
    {
        var (json, _) = await RunAsync(env, PagedQuery, "{\"f\":" + first + ",\"o\":0}");

        json.ShouldBe(PageRangeBody);
    }

    [Theory]
    [InlineData("Production", "-1")]
    [InlineData("Production", "10001")]
    public async Task OffsetOutOfRange_FixedBody(string env, string offset)
    {
        var (json, _) = await RunAsync(env, PagedQuery, "{\"f\":5,\"o\":" + offset + "}");

        json.ShouldBe(PageRangeBody);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task FirstZero_ZeroRows_NoError(string env)
    {
        var (json, errors) = await RunAsync(env, "{ sales_dbo_customers(first: 0) { id } }");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Limit.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_08_PageRangeOnDeniedTable_SameBodyAsForAnyOtherPageSize()
    {
        var deniedSmall = await RunAsync("Production", "{ sales_dbo_secrets(first: 10) { id } }");
        var deniedHuge = await RunAsync("Production", "{ sales_dbo_secrets(first: 100000) { id } }");
        var deniedNegative = await RunAsync("Production", "{ sales_dbo_secrets(first: -1) { id } }");
        var allowedHuge = await RunAsync("Production", "{ sales_dbo_customers(first: 100000) { id } }");

        deniedHuge.Json.ShouldBe(deniedSmall.Json);
        deniedNegative.Json.ShouldBe(deniedSmall.Json);
        deniedSmall.Json.ShouldNotContain("out of range");
        allowedHuge.Json.ShouldBe(PageRangeBody);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_08_PageRangeMixedWithADeniedTable_IsShielded()
    {
        var mixed = await RunAsync("Production", "{ a: sales_dbo_customers(first: 100000) { id } b: sales_dbo_secrets(first: 10) { id } }");
        var denied = await RunAsync("Production", "{ sales_dbo_secrets(first: 10) { id } }");

        mixed.Json.ShouldBe(denied.Json);
        mixed.Json.ShouldNotContain("out of range");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_RG_07_FirstBeyondInt32_IsAFixedClientError_Not5xx()
    {
        foreach (var env in new[] { "Development", "Production" })
        {
            var (json, errors) = await RunAsync(env, PagedQuery, "{\"f\":9223372036854775807,\"o\":0}");

            errors.ShouldNotBeEmpty(json);
            errors.ShouldAllBe(e => e.Code == "GRAPHQL_VARIABLE_INVALID" || e.Code == "INVALID_QUERY" || e.Code == "GRAPHQL_VALIDATION_FAILED", json);
            json.ShouldNotContain("INTERNAL_SERVER_ERROR");
        }
    }

    [Fact]
    public async Task CostBudget_StillAppliesToAnInRangeHugePage()
    {
        // first: 5000 is in range, but 5000 rows x multiplier is over the budget: still QUERY_TOO_COMPLEX in Development.
        var (json, errors) = await RunAsync("Development", "{ sales_dbo_customers(first: 5000) { id } }");

        errors.Count.ShouldBe(1, json);
        errors[0].Code.ShouldBe("QUERY_TOO_COMPLEX");
        _nodes.ShouldBeEmpty();
    }

    // ---- #319 / SEC-RG-11: logical members -------------------------------------------------------------------------

    [Theory]
    [InlineData("or: [{}, { name: { eq: \"A\" } }]")]
    [InlineData("or: [{ name: { eq: \"A\" } }, {}]")]
    public async Task OrWithEmptyMember_IsNeutral(string where)
    {
        var (json, errors) = await RunAsync("Development", "{ sales_dbo_customers(first: 5, where: { " + where + " }) { id } }");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Where.ShouldBeNull();
    }

    [Fact]
    public async Task AndWithEmptyMember_IsNeutral_AndKeepsTheOtherMember()
    {
        var (json, errors) = await RunAsync("Development", "{ sales_dbo_customers(first: 5, where: { and: [{}, { name: { eq: \"A\" } }] }) { id } }");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Where.ShouldBeOfType<TreeAndFilter>().Items.Single().ShouldBeOfType<TreeComparison>();
    }

    [Fact]
    public async Task EmptyOrList_Unchanged()
    {
        var (json, errors) = await RunAsync("Development", "{ sales_dbo_customers(first: 5, where: { or: [] }) { id } }");

        errors.ShouldBeEmpty(json);
        _nodes.Single().Where.ShouldBeOfType<TreeOrFilter>().Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("or: [null, { name: { eq: \"A\" } }]")]
    [InlineData("and: [null, { name: { eq: \"A\" } }]")]
    [InlineData("or: [{ name: { eq: \"A\" } }, null]")]
    public async Task NullMember_Rejected(string where)
    {
        var (json, errors) = await RunAsync("Development", "{ sales_dbo_customers(first: 5, where: { " + where + " }) { id } }");

        errors.ShouldNotBeEmpty(json);
        _nodes.ShouldBeEmpty();
    }
}

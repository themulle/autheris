namespace Autheris.Tests.Unit.GraphQL;

using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.GraphQL.Interceptors;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-PG-12 / SEC-PG-12-DEFENSE: the unified validation shield must not swallow the cost and depth rule results. They do not
/// depend on which fields exist, so they keep a stable code and a fixed message in every environment (real engine).
/// </summary>
[Trait("Category", "Security")]
public sealed class GraphQlQueryDefenseShieldTests
{
    public sealed class Node
    {
        public string Id() => "x";

        public Node Child() => new();
    }

    public sealed class DefenseQuery
    {
        public Node Root() => new();

        public string A() => "a";

        public string B() => "b";

        public string C() => "c";
    }

    private static async Task<IRequestExecutor> CreateExecutorAsync(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);

        return await new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(sp => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance))
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<DefenseQuery>()
            .AddMaxExecutionDepthRule(3)
            .AddValidationRule<QueryCostAnalyzerRule>((_, _) => new QueryCostAnalyzerRule(maxRootFields: 2, maxExecutionDepth: 3))
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
    }

    private static async Task<string> RunAsync(IRequestExecutor executor, string query)
    {
        await using var result = await executor.ExecuteAsync(query);
        return result.ToJson();
    }

    [Fact]
    public async Task SecPg12Defense_Development_CostRuleError_HasStableCodeFixedMessageAndNoExtensions()
    {
        var executor = await CreateExecutorAsync("Development");

        var json = await RunAsync(executor, "{ a b c }");

        json.ShouldContain("QUERY_TOO_COMPLEX");
        json.ShouldContain(ErrorSanitizingFilter.ComplexMessage);
        json.ShouldNotContain("GRAPHQL_VALIDATION_FAILED");
        AssertNoLimitDetail(json);
    }

    [Fact]
    public async Task SecPg12Defense_Development_DepthRuleError_HasStableCodeAndFixedMessageNamingDepth()
    {
        var executor = await CreateExecutorAsync("Development");

        var json = await RunAsync(executor, "{ root { child { child { child { id } } } } }");

        json.ShouldContain("QUERY_TOO_DEEP");
        json.ShouldContain(ErrorSanitizingFilter.DepthMessage);
        json.ShouldContain("depth");
        json.ShouldNotContain("GRAPHQL_VALIDATION_FAILED");
        AssertNoLimitDetail(json);
    }

    [Fact]
    public async Task SecPg12Defense_Development_StructuralLimitWinsOverUnknownField()
    {
        var executor = await CreateExecutorAsync("Development");

        var json = await RunAsync(executor, "{ a b c salaries }");

        json.ShouldContain("QUERY_TOO_COMPLEX");
        json.ShouldNotContain("salaries");
        AssertNoLimitDetail(json);
    }

    [Fact]
    public async Task SecPg12Defense_Production_OverLimitDocuments_AreByteIdenticalForExistingAndMissingFields()
    {
        var executor = await CreateExecutorAsync("Production");

        // a..c exist, "salaries" and "nope" do not; every over-limit document must answer the same body.
        var existing = await RunAsync(executor, "{ a b c root { id } }");
        var missing = await RunAsync(executor, "{ a b c nope }");
        var other = await RunAsync(executor, "{ a b c salaries }");
        var deepExisting = await RunAsync(executor, "{ root { child { child { child { id } } } } }");
        var deepMissing = await RunAsync(executor, "{ root { child { child { child { nope } } } } }");

        existing.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        missing.ShouldBe(existing);
        other.ShouldBe(existing);
        deepMissing.ShouldBe(deepExisting);
        deepExisting.ShouldBe(existing);
        existing.ShouldNotContain("QUERY_TOO_COMPLEX");
        existing.ShouldNotContain("QUERY_TOO_DEEP");
        AssertNoLimitDetail(existing);
    }

    private static void AssertNoLimitDetail(string json)
    {
        json.ShouldNotContain("calculatedCost");
        json.ShouldNotContain("maxAllowedCost");
        json.ShouldNotContain("maxRootFields");
        json.ShouldNotContain("hint");
        json.ShouldNotContain("budget");
        json.ShouldNotContain("ExecutionDepth");
    }
}

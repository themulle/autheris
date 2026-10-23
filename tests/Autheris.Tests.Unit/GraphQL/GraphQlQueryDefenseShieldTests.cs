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

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SecPg12Defense_CostRuleError_KeepsItsStableCode(string environment)
    {
        var executor = await CreateExecutorAsync(environment);

        var json = await RunAsync(executor, "{ a b c }");

        json.ShouldContain("QUERY_TOO_COMPLEX");
        json.ShouldNotContain("GRAPHQL_VALIDATION_FAILED");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task SecPg12Defense_DepthRuleError_HasStableCodeAndFixedMessageNamingDepth(string environment)
    {
        var executor = await CreateExecutorAsync(environment);

        var json = await RunAsync(executor, "{ root { child { child { child { id } } } } }");

        json.ShouldContain("QUERY_TOO_DEEP");
        json.ShouldContain(ErrorSanitizingFilter.DepthMessage);
        json.ShouldContain("depth");
        json.ShouldNotContain("GRAPHQL_VALIDATION_FAILED");
        json.ShouldNotContain("\"locations\"");
    }

    [Fact]
    public async Task SecPg12Defense_UnknownFieldNextToCostError_StillShieldsToValidationFailure()
    {
        var executor = await CreateExecutorAsync("Production");

        var json = await RunAsync(executor, "{ a b c salaries }");

        json.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        json.ShouldNotContain("salaries");
    }
}

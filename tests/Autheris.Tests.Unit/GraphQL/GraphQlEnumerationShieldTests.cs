namespace Autheris.Tests.Unit.GraphQL;

using System.Threading.Tasks;
using Autheris.Api.Middleware;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-6 / R-ERR-1: unknown fields (validation error), denied tables (ACCESS_DENIED) and unknown or denied columns
/// (INVALID_QUERY) must produce the same response outside Development, so the catalog cannot be enumerated.
/// </summary>
public sealed class GraphQlEnumerationShieldTests
{
    public sealed class ShieldQuery
    {
        public string Orders() => throw new GraphQLException(ErrorBuilder.New().SetMessage("Access denied.").SetCode("ACCESS_DENIED").Build());

        public string Employees() => throw new GraphQLException(ErrorBuilder.New().SetMessage("Unknown or denied column 'ssn' on table 'hr.employees'.").SetCode("INVALID_QUERY").Build());

        public string Public() => "ok";

        public string Throttled() => throw new GraphQLException(ErrorBuilder.New().SetMessage("Too many requests.").SetCode("TOO_MANY_REQUESTS").Build());
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
            .AddQueryType<ShieldQuery>()
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
    public async Task Production_UnknownField_DeniedTable_AndDeniedColumn_AreIndistinguishable()
    {
        var executor = await CreateExecutorAsync("Production");

        var unknown = await RunAsync(executor, "{ salaries }");
        var deniedTable = await RunAsync(executor, "{ orders }");
        var deniedColumn = await RunAsync(executor, "{ employees }");
        var deniedNextToAllowed = await RunAsync(executor, "{ public orders }");

        deniedTable.ShouldBe(unknown);
        deniedColumn.ShouldBe(unknown);
        deniedNextToAllowed.ShouldBe(unknown);
        unknown.ShouldContain(GraphQlEnumerationShieldMiddleware.Message);
        unknown.ShouldNotContain("salaries");
        unknown.ShouldNotContain("\"data\"");
    }

    [Fact]
    public async Task Production_ShieldedResult_IsMarkedAsValidationFailure_ForStatus400()
    {
        var executor = await CreateExecutorAsync("Production");

        await using var result = await executor.ExecuteAsync("{ orders }");

        result.ContextData.ShouldContainKey(ExecutionContextData.ValidationErrors);
    }

    [Fact]
    public async Task Production_UnrelatedErrorsAndSuccessfulQueries_AreUntouched()
    {
        var executor = await CreateExecutorAsync("Production");

        (await RunAsync(executor, "{ public }")).ShouldContain("\"ok\"");
        (await RunAsync(executor, "{ public throttled }")).ShouldContain("TOO_MANY_REQUESTS");
    }

    [Fact]
    public async Task Development_KeepsDetailedMessages()
    {
        var executor = await CreateExecutorAsync("Development");

        (await RunAsync(executor, "{ salaries }")).ShouldContain("salaries");
        (await RunAsync(executor, "{ employees }")).ShouldContain("ssn");
    }

    [Fact]
    public void ErrorFilter_Production_InvalidQueryMessage_IsGeneric()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var filter = new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);

        var error = filter.OnError(ErrorBuilder.New().SetMessage("Unknown or denied column 'ssn'.").SetCode("INVALID_QUERY").Build());

        error.Code.ShouldBe("INVALID_QUERY");
        error.Message.ShouldBe(GraphQlEnumerationShieldMiddleware.Message);
    }
}

namespace Autheris.Tests.Unit.GraphQL;

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
/// #294 (SEC-PG-12): pins the real HotChocolate behaviour. Document validation errors do not reach the IErrorFilter, so
/// <see cref="GraphQlEnumerationShieldMiddleware"/> sanitizes them in Development; the classifier relies on code and extension keys.
/// </summary>
[Trait("Category", "Security")]
public sealed class ErrorSanitizingFilterEngineErrorTests
{
    public sealed class Q
    {
        public int Count(int id) => id;
    }

    private static async Task<IRequestExecutor> CreateAsync(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return await new ServiceCollection()
            .AddSingleton(env)
            .AddSingleton(_ => new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance))
            .AddGraphQLServer()
            .AddApplicationService<IHostEnvironment>()
            .AddApplicationService<ErrorSanitizingFilter>()
            .AddErrorFilter(sp => sp.GetRequiredService<ErrorSanitizingFilter>())
            .AddQueryType<Q>()
            .UseRequest<GraphQlEnumerationShieldMiddleware>()
            .UseDefaultPipeline()
            .BuildRequestExecutorAsync();
    }

    private static async Task<IReadOnlyList<IError>> ErrorsAsync(string env, OperationRequest request)
    {
        var executor = await CreateAsync(env);
        await using var result = await executor.ExecuteAsync(request);
        return ((OperationResult)result).Errors ?? [];
    }

    [Theory]
    [InlineData("{ secretField }")]
    [InlineData("{ count(id: 1, hiddenArg: 2) }")]
    [InlineData("{ count(id: \"text\") }")]
    public async Task Development_ValidationErrors_GetStableCodeAndFixedMessage(string query)
    {
        var errors = await ErrorsAsync("Development", OperationRequest.FromSourceText(query));

        errors.ShouldNotBeEmpty();
        foreach (var e in errors)
        {
            e.Code.ShouldBe("GRAPHQL_VALIDATION_FAILED");
            e.Message.ShouldBe("The GraphQL request is invalid.");
            e.Extensions!.Keys.ShouldBe(["code"]);
        }
    }

    [Fact]
    public async Task Development_VariableCoercionError_GetsAStableGraphQlCode()
    {
        var request = OperationRequest.FromSourceText("query($id: Int!) { count(id: $id) }", variableValues: System.Text.Json.JsonDocument.Parse("{\"id\":\"not-a-number\"}"));

        var errors = await ErrorsAsync("Development", request);

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(e => e.Code == "GRAPHQL_VARIABLE_INVALID" || e.Code == "GRAPHQL_VALIDATION_FAILED");
        errors.ShouldAllBe(e => e.Message == "A variable value is invalid." || e.Message == "The GraphQL request is invalid.");
    }

    [Fact]
    public async Task Development_SyntaxError_GetsParseOrValidationCode()
    {
        var errors = await ErrorsAsync("Development", OperationRequest.FromSourceText("{ count(id: }"));

        errors.ShouldNotBeEmpty();
        errors.ShouldAllBe(e => e.Code == "GRAPHQL_PARSE_FAILED" || e.Code == "GRAPHQL_VALIDATION_FAILED");
        errors.ShouldAllBe(e => !e.Message.Contains("Expected"));
    }

    [Theory]
    [InlineData("{ secretField }", "{ otherUnknown }")]
    [InlineData("{ secretField }", "{ count(id: 1) extra }")]
    [InlineData("{ count(id: 1, hiddenArg: 2) }", "{ count(id: 1, otherArg: 2) }")]
    [InlineData("{ secretField }", "{ count(id: \"text\") }")]
    public async Task SEC_PG_12_InvalidDocument_SameBodyInEveryEnvironment_AndForAnyName(string first, string second)
    {
        var bodies = new List<string>();
        foreach (var env in new[] { "Development", "Production" })
        {
            var executor = await CreateAsync(env);
            foreach (var query in new[] { first, second })
            {
                await using var result = await executor.ExecuteAsync(query);
                bodies.Add(result.ToJson());
            }
        }

        bodies.Distinct().Count().ShouldBe(1, string.Join(" | ", bodies.Distinct()));
        bodies[0].ShouldContain("GRAPHQL_VALIDATION_FAILED");
        bodies[0].ShouldNotContain("locations");
        bodies[0].ShouldNotContain("secretField");
    }
}

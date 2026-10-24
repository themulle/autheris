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

        public string Ranged() => throw new GraphQLException(ErrorBuilder.New().SetMessage("The page size or offset is out of range.").SetCode("INVALID_QUERY").Build());

        public string Forged() => throw new GraphQLException(ErrorBuilder.New().SetMessage("The page size or offset is out of range. Table 'hr.salaries'.").SetCode("INVALID_QUERY").Build());

        public string OtherCode() => throw new GraphQLException(ErrorBuilder.New().SetMessage("The page size or offset is out of range.").SetCode("ACCESS_DENIED").Build());

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
        unknown.ShouldContain(ErrorSanitizingFilter.ValidationMessage);
        unknown.ShouldContain("GRAPHQL_VALIDATION_FAILED");
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
    public async Task Development_ValidationErrorsAreSanitized()
    {
        var executor = await CreateExecutorAsync("Development");

        // SEC-PG-12 (#294): validation texts never name fields, not even in Development; they carry a stable code.
        var unknown = await RunAsync(executor, "{ salaries }");
        unknown.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        unknown.ShouldNotContain("salaries");
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

    private const string ProductionValidationBody = "{\"errors\":[{\"message\":\"The GraphQL request is invalid.\",\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}]}";
    private const string PageRangeBody = "{\"errors\":[{\"message\":\"The page size or offset is out of range.\",\"extensions\":{\"code\":\"INVALID_QUERY\"}}]}";

    private static async Task<string> CompactAsync(IRequestExecutor executor, string query)
    {
        await using var result = await executor.ExecuteAsync(query);
        return System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(result.ToJson()).RootElement);
    }

    [Theory]
    [InlineData("{ salaries }")]
    [InlineData("{ public(hidden: 1) }")]
    [InlineData("{ public ... on Nope { x } }")]
    [InlineData("{ orders }")]
    [InlineData("{ employees }")]
    [Trait("Category", "Security")]
    public async Task SEC_RG_09_ProductionShieldBodies_ByteIdentical(string query)
    {
        // The five #294 bodies (unknown field, argument and type, denied table, denied column) as literals captured from main before #330.
        var executor = await CreateExecutorAsync("Production");

        (await CompactAsync(executor, query)).ShouldBe(ProductionValidationBody);
    }

    [Theory]
    [InlineData("{ salaries }", ProductionValidationBody)]
    [InlineData("{ public(hidden: 1) }", ProductionValidationBody)]
    [InlineData("{ public ... on Nope { x } }", ProductionValidationBody)]
    [InlineData("{ orders }", "{\"errors\":[{\"message\":\"Access denied.\",\"path\":[\"orders\"],\"extensions\":{\"code\":\"ACCESS_DENIED\",\"dev_fix_hints\":[\"Request consent via GraphQL mutation \\u0027requestConsent(domain: ..., tableName: ...)\\u0027\",\"Or enable \\u0027Insecure:warn_auto_approve_access_requests: true\\u0027 in appsettings.Development.json\",\"Or enable \\u0027Insecure:danger_bypass_consent_checks: true\\u0027 for unrestricted dev access\"]}}],\"data\":null}")]
    [InlineData("{ employees }", "{\"errors\":[{\"message\":\"Unknown or denied column \\u0027ssn\\u0027 on table \\u0027hr.employees\\u0027.\",\"path\":[\"employees\"],\"extensions\":{\"code\":\"INVALID_QUERY\"}}],\"data\":null}")]
    [Trait("Category", "Security")]
    public async Task SEC_RG_09_DevelopmentShieldBodies_ByteIdentical(string query, string expected)
    {
        var executor = await CreateExecutorAsync("Development");

        (await CompactAsync(executor, query)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [Trait("Category", "Security")]
    public async Task SEC_RG_08_PageRange_NotShielded_FixedBody(string environment)
    {
        var executor = await CreateExecutorAsync(environment);

        (await CompactAsync(executor, "{ ranged }")).ShouldBe(PageRangeBody);
    }

    [Theory]
    [InlineData("{ ranged public orders }")]
    [InlineData("{ ranged employees }")]
    [InlineData("{ ranged salaries }")]
    [Trait("Category", "Security")]
    public async Task SEC_RG_08_PageRangeMixedWithAnyOtherError_Shielded(string query)
    {
        var executor = await CreateExecutorAsync("Production");

        (await CompactAsync(executor, query)).ShouldBe(ProductionValidationBody);
    }

    [Theory]
    [InlineData("{ forged }")]
    [InlineData("{ otherCode }")]
    [Trait("Category", "Security")]
    public async Task SEC_RG_08_OnlyTheExactConstantAndCodeIsExempt(string query)
    {
        var executor = await CreateExecutorAsync("Production");

        (await CompactAsync(executor, query)).ShouldBe(ProductionValidationBody);
    }
}

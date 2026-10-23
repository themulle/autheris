namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Api.Middleware;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#294 GAP-003 (SEC-PG-12): code-less engine validation and coercion errors get a stable code and a fixed message.</summary>
public sealed class ErrorSanitizingFilterCodeTests
{
    private static ErrorSanitizingFilter CreateFilter(string envName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void ValidationError_GetsValidationCode(string env)
    {
        var error = ErrorBuilder.New().SetMessage("The field `secretTable` does not exist on the type `Query`.").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("GRAPHQL_VALIDATION_FAILED");
        result.Message.ShouldNotContain("secretTable");
        result.Message.ShouldNotContain("Query");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void EngineCodedValidationError_IsReplaced(string env)
    {
        var error = ErrorBuilder.New().SetMessage("Unknown argument `hiddenArg` on field `Query.t`.").SetCode("HC0021").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("GRAPHQL_VALIDATION_FAILED");
        result.Message.ShouldNotContain("hiddenArg");
        result.Message.ShouldNotContain("HC0021");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void VariableCoercion_GetsVariableCode(string env)
    {
        var error = ErrorBuilder.New().SetMessage("Variable `$amount` got an invalid value for type `Decimal`.").SetCode("HC0016").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("GRAPHQL_VARIABLE_INVALID");
        result.Message.ShouldNotContain("amount");
        result.Message.ShouldNotContain("Decimal");
    }

    [Fact]
    public void ParseError_GetsParseCode()
    {
        var error = ErrorBuilder.New().SetMessage("Expected a `Name`-token, but found a `RightBrace`-token.").SetCode("HC0011").Build();

        CreateFilter("Production").OnError(error).Code.ShouldBe("GRAPHQL_PARSE_FAILED");
    }

    [Fact]
    public void WhitelistedCodes_AreAcceptedByTheParityReflectionSeam()
    {
        var field = typeof(ErrorSanitizingFilter).GetField("WhitelistedSafeCodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        field.ShouldNotBeNull();
        var codes = ((IEnumerable<string>)field!.GetValue(null)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        codes.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        codes.ShouldContain("GRAPHQL_VARIABLE_INVALID");
        codes.ShouldContain("GRAPHQL_PARSE_FAILED");
        codes.ShouldContain("REQUIRED_FILTER_COLUMN_MISSING");
        codes.ShouldNotContain("REQUIRED_FILTER_MISSING");
    }
}

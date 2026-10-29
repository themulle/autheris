namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Api.Middleware;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#294 GAP-003 (SEC-PG-12): code-less engine validation and coercion errors get a stable code and a fixed message.</summary>
[Trait("Category", "Security")]
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
        var error = ErrorBuilder.New().SetMessage("Variable `$amount` got an invalid value for type `Decimal`.").SetCode("HC0016").SetExtension("variable", "amount").Build();

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

    [Theory]
    [InlineData("Development", "AGGREGATION_INVALID_FUNCTION_FOR_TYPE")]
    [InlineData("Production", "AGGREGATION_INVALID_FUNCTION_FOR_TYPE")]
    [InlineData("Production", "AGGREGATION_ROW_CAP_EXCEEDED")]
    [InlineData("Production", "REQUIRED_FILTER_COLUMN_MISSING")]
    [InlineData("Production", "AGGREGATION_PRIVACY_RESTRICTED")]
    [InlineData("Production", "AGGREGATION_NOT_SUPPORTED")]
    [InlineData("Production", "AGG_NEGATED_FILTER_REJECTED")]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-05")]
    public void TypedFixedMessageCodes_PassThroughInEveryEnvironment(string env, string code)
    {
        var error = ErrorBuilder.New().SetMessage("Thrower text.").SetCode(code).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe(code);
        result.Message.ShouldNotBe("Thrower text.");
        result.Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-T-05")]
    public void Sanitizer_NewCodes_NotExpectedOutcome_UnknownCode_StillSanitized()
    {
        var expected = typeof(ErrorSanitizingFilter).GetField("ExpectedOutcomeCodes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        expected.ShouldNotBeNull();
        var codes = ((IEnumerable<string>)expected!.GetValue(null)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        codes.ShouldNotContain("AGGREGATION_PRIVACY_RESTRICTED");
        codes.ShouldNotContain("AGGREGATION_NOT_SUPPORTED");

        var error = ErrorBuilder.New().SetMessage("secret column amount").SetCode("AGGREGATION_SOMETHING_ELSE").Build();
        var result = CreateFilter("Production").OnError(error);
        result.Code.ShouldNotBe("AGGREGATION_SOMETHING_ELSE");
        result.Message.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "I389-SC-01")]
    public void NewAggregationCodes_FixedMessagesContainNoThrowerText()
    {
        foreach (var code in new[] { "AGGREGATION_PRIVACY_RESTRICTED", "AGGREGATION_NOT_SUPPORTED" })
        {
            var error = ErrorBuilder.New().SetMessage("Thrower text amount_column.").SetCode(code).Build();
            var result = CreateFilter("Production").OnError(error);
            result.Message.ShouldNotContain("Thrower");
            result.Message.ShouldNotContain("amount");
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void Gql17_MaskedCode_PassesWithFixedMessage(string env)
    {
        var error = ErrorBuilder.New().SetMessage("Column 'ssn' is masked.").SetCode("MASKED").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("MASKED");
        result.Message.ShouldBe(ErrorSanitizingFilter.MaskedMessage);
    }

    [Fact]
    public void Gql17_MaskedMessage_ContainsNoValue()
    {
        var error = ErrorBuilder.New().SetMessage("Column 'ssn' value '123-45-6789' hash abc is masked.").SetCode("MASKED").Build();

        var result = CreateFilter("Production").OnError(error);

        result.Message.ShouldNotContain("ssn");
        result.Message.ShouldNotContain("123-45-6789");
        result.Message.ShouldNotContain("abc");
    }

    [Fact]
    public void Gql17_MaskedCode_NotLoggedAtErrorLevel()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ErrorSanitizingFilter>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        var filter = new ErrorSanitizingFilter(env, logger);

        filter.OnError(ErrorBuilder.New().SetMessage("Column 'ssn' is masked.").SetCode("MASKED").Build());

        logger.ReceivedCalls().Count(c => c.GetArguments().Length > 0
            && c.GetArguments()[0] is Microsoft.Extensions.Logging.LogLevel l
            && l >= Microsoft.Extensions.Logging.LogLevel.Error).ShouldBe(0);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Gql17_UnlistedCode_StillMaskedAsInternal(string env)
    {
        var error = ErrorBuilder.New().SetMessage("secret detail").SetCode("SOME_UNLISTED_CODE").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
        result.Message.ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("MASKED_COLUMN")]
    [InlineData("masked")]
    [InlineData("Masked")]
    public void Gql17_SimilarCodes_StillMasked(string code)
    {
        var error = ErrorBuilder.New().SetMessage("Column 'ssn' is masked.").SetCode(code).Build();

        var result = CreateFilter("Production").OnError(error);

        result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
        result.Message.ShouldNotContain("ssn");
    }
}

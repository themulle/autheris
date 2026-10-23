namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Api.Middleware;
using Autheris.Domain.Exceptions;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#314 (SEC-PG-12): no exception type, raw message or engine name reaches a client in any environment.</summary>
[Trait("Category", "Security")]
public sealed class ErrorSanitizingFilterLeakTests
{
    private static ErrorSanitizingFilter CreateFilter(string envName, ILogger<ErrorSanitizingFilter>? logger = null)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return new ErrorSanitizingFilter(env, logger ?? NullLogger<ErrorSanitizingFilter>.Instance);
    }

    private static IError WithException(Exception ex, string? code = null)
    {
        var b = ErrorBuilder.New().SetMessage(ex.Message).SetException(ex);
        if (code != null) b.SetCode(code);
        return b.Build();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_UnknownException_NeverWritesTypeOrMessage(string env)
    {
        var result = CreateFilter(env).OnError(WithException(new InvalidOperationException("secret table hr.salaries failed")));

        result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
        result.Message.ShouldNotContain("InvalidOperationException");
        result.Message.ShouldNotContain("salaries");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_BareFormatOrOverflowFromApplicationCode_FailsClosedToInternal(string env)
    {
        foreach (var ex in new Exception[] { new FormatException("Input string '99999999999999999999' was not in a correct format."), new OverflowException("Value was too large for an Int64.") })
        {
            var result = CreateFilter(env).OnError(WithException(ex));

            result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
            result.Message.ShouldNotContain("Int64");
            result.Message.ShouldNotContain("9999");
            result.Message.ShouldNotContain(ex.GetType().Name);
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_EngineCoercionException_MapsToVariableInvalid(string env)
    {
        var result = CreateFilter(env).OnError(WithException(new InvalidFormatException("Cannot coerce 99999999999999999999 to Int64")));

        result.Code.ShouldBe("GRAPHQL_VARIABLE_INVALID");
        result.Message.ShouldNotContain("InvalidFormatException");
        result.Message.ShouldNotContain("9999");
    }

    [Fact]
    public void SEC_PG_12_JsonExceptionInsideResolver_IsInternal_NotInvalidJson()
    {
        var error = ErrorBuilder.New().SetMessage("bad").SetException(new System.Text.Json.JsonException("bad json at secret")).SetPath(Path.Root.Append("field")).Build();

        var result = CreateFilter("Production").OnError(error);

        result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
    }

    [Fact]
    public void SEC_PG_12_JsonExceptionAtRequestParse_MapsToInvalidJson()
    {
        var result = CreateFilter("Production").OnError(WithException(new System.Text.Json.JsonException("secret detail")));

        result.Code.ShouldBe("INVALID_JSON");
        result.Message.ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_EveryTypedCode_SendsTheFixedTableText_NeverTheThrowerText(string env)
    {
        var field = typeof(ErrorSanitizingFilter).GetField("FixedClientMessages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.ShouldNotBeNull();
        var table = (IReadOnlyDictionary<string, string>)field!.GetValue(null)!;
        table.Count.ShouldBeGreaterThan(15);

        foreach (var (code, text) in table)
        {
            var thrower = new GatewayInvalidQueryException("RestrictNegatedFilters differential privacy column 'ssn' hr.employees");
            var error = ErrorBuilder.New().SetMessage(thrower.Message).SetCode(code).SetException(thrower).Build();

            var result = CreateFilter(env).OnError(error);

            result.Code.ShouldBe(code);
            result.Message.ShouldBe(text);
            result.Message.ShouldNotContain("RestrictNegatedFilters");
            result.Message.ShouldNotContain("differential privacy");
            result.Message.ShouldNotContain("ssn");
        }
    }

    [Fact]
    public void SEC_PG_12_NegatedFilterRejection_MessageIsGenericAtTheSource()
    {
        Autheris.Application.Analytics.AggregationFilterRejectedException.FixedMessage.ShouldNotContain("RestrictNegatedFilters");
        new Autheris.Application.Analytics.AggregationFilterRejectedException().Message.ShouldBe(Autheris.Application.Analytics.AggregationFilterRejectedException.FixedMessage);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_ProviderLikeGlobMessage_MapsToInvalidQuery(string env)
    {
        var result = CreateFilter(env).OnError(WithException(new TestDbException("SQLite Error 1: 'LIKE or GLOB pattern too complex'.")));

        result.Code.ShouldBe("INVALID_QUERY");
        result.Message.ShouldNotContain("SQLite");
        result.Message.ShouldNotContain("GLOB");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SEC_PG_12_TableNotFound_IsMaskedAsForbiddenInEveryEnvironment(string env)
    {
        var ex = new TableNotFoundException(new Autheris.Domain.Common.TableIdentifier("finance", "dbo", "hidden"));

        var result = CreateFilter(env).OnError(WithException(ex, ex.ErrorCode));

        result.Code.ShouldBe("FORBIDDEN");
        result.Message.ShouldNotContain("hidden");
        result.Message.ShouldNotContain("TableNotFound");
    }

    [Fact]
    public void SEC_PG_12_Development_KeepsOnlyFixedFixHints()
    {
        var result = CreateFilter("Development").OnError(ErrorBuilder.New().SetMessage("Authentication is required.").SetCode("UNAUTHORIZED").Build());

        result.Extensions!.ContainsKey("dev_fix_hints").ShouldBeTrue();
    }

    [Fact]
    public void SEC_PG_12_ServerSideLogging_KeepsTheFullException()
    {
        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var ex = new InvalidOperationException("boom");

        CreateFilter("Production", logger).OnError(WithException(ex));

        logger.ReceivedCalls().Any(c => c.GetMethodInfo().Name == "Log" && c.GetArguments()[0] is LogLevel.Error && c.GetArguments()[3] == ex).ShouldBeTrue();
    }

    private sealed class InvalidFormatException(string message) : Exception(message);

    private sealed class TestDbException(string message) : System.Data.Common.DbException(message);
}

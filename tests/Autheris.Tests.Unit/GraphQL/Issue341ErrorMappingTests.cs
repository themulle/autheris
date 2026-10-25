namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Api.Middleware;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #341: stable codes the resolvers already raised, and the governed SQL table refusal, no longer fall through to
/// INTERNAL_SERVER_ERROR; caller text and exception messages never reach the client.
/// </summary>
[Trait("Category", "Security")]
public sealed class Issue341ErrorMappingTests
{
    private const string Leak = "table finance.dbo.finance_items password=hunter2";

    private static ErrorSanitizingFilter CreateFilter(string envName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
    }

    [Theory]
    [InlineData("Production", "SYNC_FAILED")]
    [InlineData("Production", "GDPR_SUBJECT_SCOPE_UNRESOLVED")]
    public void StableResolverCode_KeepsCodeWithFixedMessage(string env, string code)
    {
        var error = ErrorBuilder.New().SetMessage(Leak).SetCode(code).SetException(new InvalidOperationException(Leak)).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe(code);
        result.Message.ShouldNotContain("hunter2");
        result.Message.ShouldNotContain("finance_items");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void WebSqlTableDenied_AnswersAccessDenied_WithFixedMessage(string env)
    {
        var error = ErrorBuilder.New().SetMessage(Leak).SetException(new WebSqlTableDeniedException(Leak)).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("ACCESS_DENIED");
        result.Message.ShouldBe("Access denied.");
        result.Message.ShouldNotContain("finance");
    }

    private const string Canary = "column secret_canary_col in table hr.dbo.salaries";

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void GatewayInvalidQueryException_MapsToInvalidQuery_FixedMessage_NoInputEcho(string env)
    {
        var error = ErrorBuilder.New().SetMessage(Canary).SetException(new Autheris.Domain.Exceptions.GatewayInvalidQueryException(Canary))
            .SetExtension("detail", Canary).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("INVALID_QUERY");
        result.Message.ShouldNotContain("secret_canary_col");
        result.Message.ShouldNotContain("salaries");
        result.Extensions!.ContainsKey("detail").ShouldBeFalse();
        result.Extensions.Values.ShouldNotContain(v => v != null && v.ToString()!.Contains("secret_canary_col"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void GatewayPageRangeException_MapsToInvalidQuery_PageRangeFixedMessage(string env)
    {
        var error = ErrorBuilder.New().SetMessage("x").SetException(new Autheris.Domain.Exceptions.GatewayPageRangeException()).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("INVALID_QUERY");
        result.Message.ShouldBe(Autheris.Domain.Exceptions.GatewayPageRangeException.FixedMessage);
        // A1-S4: the enumeration shield still recognizes the page-range error.
        GraphQlEnumerationShieldMiddleware.IsPageRangeError(result).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void GatewayNotImplementedException_MapsToNotImplemented_FixedMessage(string env)
    {
        var error = ErrorBuilder.New().SetMessage(Canary)
            .SetException(new Autheris.Domain.Exceptions.GatewayNotImplementedException("No executor for DataSourceType 'LakehouseDelta'. " + Canary)).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("NOT_IMPLEMENTED");
        result.Message.ShouldBe("The requested feature or data source capability is not implemented.");
        result.Message.ShouldNotContain("LakehouseDelta");
    }

    [Fact]
    public void NotImplementedCode_IsWhitelisted_WithFixedMessage()
    {
        var error = ErrorBuilder.New().SetMessage(Canary).SetCode("NOT_IMPLEMENTED").SetException(new InvalidOperationException(Canary)).Build();

        var result = CreateFilter("Production").OnError(error);

        result.Code.ShouldBe("NOT_IMPLEMENTED");
        result.Message.ShouldBe("The requested feature or data source capability is not implemented.");
    }

    [Fact]
    public void TreePathGraphQlException_InvalidQuery_MessageUnchanged()
    {
        var error = ErrorBuilder.New().SetMessage("The page size or offset is out of range.").SetCode("INVALID_QUERY").Build();

        var result = CreateFilter("Production").OnError(error);

        result.Code.ShouldBe("INVALID_QUERY");
        result.Message.ShouldBe("The page size or offset is out of range.");
    }

    [Fact]
    public void WebSqlPolicyViolation_IsStillMasked()
    {
        var error = ErrorBuilder.New().SetMessage(Leak).SetException(new WebSqlPolicyException(Leak)).Build();

        CreateFilter("Production").OnError(error).Code.ShouldBe("INTERNAL_SERVER_ERROR");
    }

    [Theory]
    [InlineData("", "dbo", "t")]
    [InlineData("d", " ", "t")]
    [InlineData("d", "dbo", "")]
    [InlineData(null, "dbo", "t")]
    [InlineData("a.b", "dbo", "t")]
    [InlineData("d", "dbo", "t\nu")]
    [InlineData("d", "dbo", "t\0")]
    public void TableIdentifierTryCreate_RejectsBlankAndMalformedComponents(string? domain, string? schema, string? name)
    {
        TableIdentifier.TryCreate(domain, schema, name, out var id).ShouldBeFalse();
        id.ShouldBe(default);
    }

    [Fact]
    public void TableIdentifierTryCreate_AcceptsValidAndTrims()
    {
        TableIdentifier.TryCreate(" finance ", "dbo", "items", out var id).ShouldBeTrue();
        id.ToString().ShouldBe("finance.dbo.items");
    }

    [Theory]
    [InlineData("d", "s", "t")]
    [InlineData("", "s", "t")]
    [InlineData(" ", "s", "t")]
    [InlineData("d", "s", "a.b")]
    [InlineData("d", "s", "a\nb")]
    [InlineData("d", "s", "a\rb")]
    [InlineData("d", "s", "a\0b")]
    [InlineData("d", null, "t")]
    [InlineData("d\tx", "s", "t")]
    public void TableIdentifier_TryCreate_MatchesConstructorRules(string? domain, string? schema, string? name)
    {
        var constructed = true;
        try
        {
            _ = new TableIdentifier(domain!, schema!, name!);
        }
        catch (ArgumentException)
        {
            constructed = false;
        }

        TableIdentifier.TryCreate(domain, schema, name, out _).ShouldBe(constructed);
    }

    [Theory]
    [InlineData("", "orders")]
    [InlineData("a_", "x")]
    [InlineData("_x", "x")]
    public void CostAndQuota_InvalidTableName_StillChargedAgainstFallbackBucket(string fieldName, string _)
    {
        var method = typeof(Autheris.GraphQL.Interceptors.CostAndQuotaMiddleware)
            .GetMethod("ExtractTableFromDocument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.ShouldNotBeNull();
        var doc = HotChocolate.Language.Utf8GraphQLParser.Parse($"{{ {(fieldName.Length == 0 ? "ok" : fieldName)} }}");

        var id = (TableIdentifier)method.Invoke(null, [doc])!;

        // A name that cannot construct an identifier lands in a fixed bucket instead of throwing or escaping the quota.
        id.ShouldNotBe(default);
        if (fieldName.Length != 0)
        {
            id.ToString().ShouldBe("graphql.query.graphql");
        }
    }
}

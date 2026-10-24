namespace Autheris.Tests.Unit.DataApi;

using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Data.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#295 (GAP-004), #313, #301 negatives; SEC-PG-14: RFC 7807 bodies with a stable code and fixed messages.</summary>
[Trait("Category", "Security")]
public sealed class DatasetDataEndpointErrorTests
{
    private static DefaultHttpContext Ctx(string query = "")
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.QueryString = new QueryString(query);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u"), new Claim("tenant", "default")], "Bearer"));
        return context;
    }

    private static async Task<(int Status, JsonElement Body, string Raw)> RunAsync(Exception? thrown, string query = "", IGovernedDataQueryService? svc = null)
    {
        var ctx = Ctx(query);
        var service = svc ?? Substitute.For<IGovernedDataQueryService>();
        if (thrown is not null)
        {
            service.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
                .Returns<Task<DatasetQueryEnvelope>>(_ => throw thrown);
        }

        await DatasetDataEndpoints.HandleQueryThreePartAsync("sales", "dbo", "customers", ctx, service, NullLoggerFactory.Instance, CancellationToken.None);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    [Fact]
    public async Task ArgumentException_Is400InvalidQuery_WithFixedMessage_SEC_PG_14()
    {
        var (status, body, raw) = await RunAsync(new ArgumentException("bad value for parameter 'intent' and rawSql 'DROP x'", "paramNameXyz"));

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
        raw.ShouldNotContain("intent");
        raw.ShouldNotContain("rawSql");
        raw.ShouldNotContain("DROP x");
        raw.ShouldNotContain("paramNameXyz");
    }

    [Fact]
    public async Task TableNotFound_Is404ResourceNotFound_WithoutExceptionText_SEC_PG_14()
    {
        var (status, body, raw) = await RunAsync(new TableNotFoundException(new TableIdentifier("sales", "dbo", "secret_tbl")));

        status.ShouldBe(404);
        body.GetProperty("code").GetString().ShouldBe("RESOURCE_NOT_FOUND");
        raw.ShouldNotContain("secret_tbl");
    }

    [Fact]
    public async Task UnauthorizedAccess_Is403WithCode_AndNoExceptionText_SEC_PG_14()
    {
        var (status, body, raw) = await RunAsync(new UnauthorizedAccessException("user bob lacks role admin"));

        status.ShouldBe(403);
        body.GetProperty("code").GetString().ShouldBe("ACCESS_DENIED");
        raw.ShouldNotContain("bob");
    }

    [Fact]
    public async Task UnexpectedException_Is500WithCode_AndNoExceptionText_SEC_PG_14()
    {
        var (status, body, raw) = await RunAsync(new InvalidOperationException("Npgsql host=db-prod-01"));

        status.ShouldBe(500);
        body.GetProperty("code").GetString().ShouldBe("INTERNAL_ERROR");
        raw.ShouldNotContain("db-prod-01");
    }

    [Theory]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-1")]
    [InlineData("?offset=-5")]
    [InlineData("?limit=99999999999")]
    [InlineData("?offset=1&offset=2")]
    public async Task BadPaging_Is400InvalidQuery_AndNeverReachesTheService_SEC_PG_14(string query)
    {
        var svc = Substitute.For<IGovernedDataQueryService>();

        var (status, body, raw) = await RunAsync(null, query, svc);

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
        raw.ShouldNotContain("limit", Case.Insensitive);
        raw.ShouldNotContain("offset", Case.Insensitive);
        raw.ShouldNotContain("abc");
        await svc.DidNotReceiveWithAnyArgs().ExecuteQueryAsync(default!, default!, default);
    }

    [Fact]
    public async Task LimitZero_IsForwardedAsZero()
    {
        var svc = Substitute.For<IGovernedDataQueryService>();
        svc.ExecuteQueryAsync(Arg.Any<DatasetQueryRequest>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetQueryEnvelope("sales.dbo.customers", 0, 0, 0, false, [], []));

        var (status, _, _) = await RunAsync(null, "?limit=0&offset=3", svc);

        status.ShouldBe(200);
        await svc.Received(1).ExecuteQueryAsync(
            Arg.Is<DatasetQueryRequest>(r => r.Limit == 0 && r.Offset == 3), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    // #329 (SEC-RG-14): REST never answers SQL_SYNTAX_ERROR; a parser failure of the filter is a bad parameter.
    [Fact]
    public async Task DanglingOperator_Returns400InvalidQuery()
    {
        var (status, body, _) = await RunAsync(new Antlr4.Runtime.Misc.ParseCanceledException("line 1:7 mismatched input '<EOF>'"), "?filter=region%20eq");

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
    }

    [Fact]
    public async Task UnbalancedParenthesis_Returns400InvalidQuery()
    {
        var (status, body, _) = await RunAsync(new Antlr4.Runtime.Misc.ParseCanceledException("line 1:12 missing ')'"), "?filter=(id%20eq%201");

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
    }

    [Fact]
    public async Task MalformedFilter_BodyHasNoParserDetail()
    {
        var (status, body, raw) = await RunAsync(new Antlr4.Runtime.Misc.ParseCanceledException("line 1:7 mismatched input 'SECRET_TOKEN' expecting"), "?filter=region%20eq");

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
        raw.ShouldNotContain("SECRET_TOKEN");
        raw.ShouldNotContain("mismatched");
        raw.ShouldNotContain("SYNTAX", Case.Insensitive);
    }

    [Fact]
    public async Task PolicyViolation_NotRemappedToInvalidQuery()
    {
        var (status, body, _) = await RunAsync(new Autheris.Application.Sql.WebSqlPolicyException("masked column in filter"), "?filter=email%20eq%20%27x%27");

        status.ShouldBe(403);
        body.GetProperty("code").GetString().ShouldBe("SQL_POLICY_VIOLATION");
    }

    [Fact]
    public async Task ProviderError_StaysMappedAs502()
    {
        var (status, body, _) = await RunAsync(new TrinoSqlEngine.Ast.Emit.GovernedSqlException(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.ProviderError, TrinoSqlEngine.TargetSqlDialect.Sqlite));

        status.ShouldBe(502);
        body.GetProperty("code").GetString().ShouldBe("SQL_PROVIDER_ERROR");
    }

    [Fact]
    public async Task OffsetAboveCap_Returns400InvalidQuery()
    {
        var (status, body, _) = await RunAsync(new ArgumentException("Offset too large: offset must not exceed 10000.", "request"), "?offset=10001");

        status.ShouldBe(400);
        body.GetProperty("code").GetString().ShouldBe("INVALID_QUERY");
    }
}

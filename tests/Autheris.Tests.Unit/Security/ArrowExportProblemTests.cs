namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Serialization;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#305 (GAP-014), SEC-PG-15: every Arrow export problem carries a stable catalogue code and a fixed message.</summary>
[Trait("Category", "Security")]
public sealed class ArrowExportProblemTests
{
    private static ArrowExportService Service() => new(
        Options.Create(new GatewayOptions { Arrow = new ArrowExportOptions { Enabled = true, MaxExportRows = 1000, BatchSize = 1000 } }),
        NullLogger<ArrowExportService>.Instance);

    private static DefaultHttpContext Ctx(string query, bool authenticated = true)
    {
        var ctx = new DefaultHttpContext();
        if (authenticated)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user:alice"), new Claim("tenant_id", "t1")], "T"));
        }

        ctx.Request.QueryString = new QueryString(query);
        return ctx;
    }

    private static async Task<ProblemHttpResult> RunAsync(DefaultHttpContext ctx, IGovernedSqlExecutionService? sql)
    {
        var result = await ArrowExportEndpoints.HandleArrowExportAsync(
            ctx, Service(), sql, new Autheris.Application.Data.Services.GovernedTableReadPlanner(), CancellationToken.None);
        return result.ShouldBeOfType<ProblemHttpResult>();
    }

    private static IGovernedSqlExecutionService Throwing(Exception ex)
    {
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<Task<GovernedSqlResult>>(_ => throw ex);
        return sql;
    }

    private static string? Code(ProblemHttpResult p) => p.ProblemDetails.Extensions.TryGetValue("code", out var c) ? c?.ToString() : null;

    [Fact]
    public async Task RequiredFilterMissing_HasCode_SEC_PG_15()
    {
        var p = await RunAsync(Ctx("?table=finance.invoices"), Throwing(new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException()));

        p.StatusCode.ShouldBe(400);
        Code(p).ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
    }

    [Fact]
    public async Task Unauthenticated_HasUnauthorizedCode_SEC_PG_15()
    {
        var p = await RunAsync(Ctx("?table=a.b", authenticated: false), null);

        p.StatusCode.ShouldBe(401);
        Code(p).ShouldBe("UNAUTHORIZED");
    }

    [Fact]
    public async Task MissingTableAndSql_HasBadRequestCode_SEC_PG_15()
    {
        var p = await RunAsync(Ctx(""), null);

        p.StatusCode.ShouldBe(400);
        Code(p).ShouldBe("BAD_REQUEST");
    }

    [Fact]
    public async Task InvalidTableIdentifier_DoesNotEchoTheSubmittedToken_SEC_PG_15()
    {
        var p = await RunAsync(Ctx("?table=%27%3Bsecret_token%3B%27"), null);

        p.StatusCode.ShouldBe(400);
        Code(p).ShouldBe("BAD_REQUEST");
        (p.ProblemDetails.Detail ?? string.Empty).ShouldNotContain("secret_token");
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), 500, "INTERNAL_ERROR")]
    [InlineData(typeof(System.Security.SecurityException), 403, "SQL_POLICY_VIOLATION")]
    [InlineData(typeof(ArgumentException), 400, "BAD_REQUEST")]
    public async Task CatchAllBranches_HaveCodes_SEC_PG_15(Type type, int status, string code)
    {
        var p = await RunAsync(Ctx("?table=finance.invoices"), Throwing((Exception)Activator.CreateInstance(type, "leak-me")!));

        p.StatusCode.ShouldBe(status);
        Code(p).ShouldBe(code);
        (p.ProblemDetails.Detail ?? string.Empty).ShouldNotContain("leak-me");
    }

    [Fact]
    public async Task NoSqlService_HasServiceUnavailableCode_SEC_PG_15()
    {
        var p = await RunAsync(Ctx("?table=finance.invoices"), null);

        p.StatusCode.ShouldBe(503);
        Code(p).ShouldBe("SERVICE_UNAVAILABLE");
    }
}

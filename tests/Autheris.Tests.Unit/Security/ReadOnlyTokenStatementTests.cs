namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>
/// WP-SIMPL-38 PR 2 (#484): Read-only token query execution on /v1/statement and
/// statement cancellation via DELETE on own statement paths.
/// </summary>
public sealed class ReadOnlyTokenStatementTests
{
    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(bool Passed, int Status)> InvokeAsync(string method, string path, Action<IHeaderDictionary>? headers = null)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ => { passed = true; return Task.CompletedTask; }, "/graphql", "/mcp");
        var context = new DefaultHttpContext { User = ReadOnlyPrincipal() };
        context.Request.Method = method;
        context.Request.Path = path;
        headers?.Invoke(context.Request.Headers);
        context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context);
        return (passed, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/v1/statement")]
    [InlineData("/v1/statement/")]
    [InlineData("/V1/STATEMENT")]
    [InlineData("/V1/Statement/")]
    [Trait("Category", "Security")]
    public async Task ReadOnly_PostV1Statement_IsPermitted(string path)
    {
        var (passed, status) = await InvokeAsync("POST", path);

        passed.ShouldBeTrue();
        status.ShouldBe(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/v1/statement/queued/stmt-1/tok-1")]
    [InlineData("/v1/statement/executing/stmt-1/tok-1")]
    [InlineData("/v1/statement/123")]
    [InlineData("/v1/statement/sub")]
    [InlineData("/v1/statement/queued")]
    [InlineData("/v1/statement/executing")]
    [InlineData("/v1/statement/other/nested/path")]
    [Trait("Category", "Security")]
    public async Task ReadOnly_PostV1StatementSubPath_Is403(string path)
    {
        var (passed, status) = await InvokeAsync("POST", path);

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("/api/v1/sql/statements/stmt-123")]
    [InlineData("/api/sql/statements/stmt-123")]
    [InlineData("/v1/statement/queued/stmt-123/token-456")]
    [InlineData("/v1/statement/executing/stmt-123/token-456")]
    [InlineData("/API/V1/SQL/STATEMENTS/stmt-123")]
    [InlineData("/API/SQL/STATEMENTS/stmt-123")]
    [InlineData("/V1/STATEMENT/QUEUED/stmt-123/token-456")]
    [InlineData("/V1/STATEMENT/EXECUTING/stmt-123/token-456")]
    [Trait("Category", "Security")]
    public async Task ReadOnly_DeleteOwnStatementPaths_ArePermitted(string path)
    {
        var (passed, status) = await InvokeAsync("DELETE", path);

        passed.ShouldBeTrue();
        status.ShouldBe(StatusCodes.Status200OK);
    }

    [Theory]
    // Non-statement paths
    [InlineData("/api/v1/sql")]
    [InlineData("/api/sql")]
    [InlineData("/v1/statement")]
    [InlineData("/v1/statement/")]
    [InlineData("/api/v1/queries")]
    [InlineData("/api/v1/procedures/test_proc")]
    [InlineData("/other/random/path")]
    // Missing or empty segments
    [InlineData("/api/v1/sql/statements")]
    [InlineData("/api/v1/sql/statements/")]
    [InlineData("/api/sql/statements")]
    [InlineData("/api/sql/statements/")]
    [InlineData("/v1/statement/queued")]
    [InlineData("/v1/statement/queued/")]
    [InlineData("/v1/statement/queued/stmt-1")]
    [InlineData("/v1/statement/queued//token-1")]
    [InlineData("/v1/statement/queued/stmt-1/")]
    [InlineData("/v1/statement/executing")]
    [InlineData("/v1/statement/executing/")]
    [InlineData("/v1/statement/executing/stmt-1")]
    [InlineData("/v1/statement/executing//token-1")]
    [InlineData("/v1/statement/executing/stmt-1/")]
    // Extra segments
    [InlineData("/api/v1/sql/statements/stmt-1/extra")]
    [InlineData("/api/sql/statements/stmt-1/extra")]
    [InlineData("/v1/statement/queued/stmt-1/token-1/extra")]
    [InlineData("/v1/statement/executing/stmt-1/token-1/extra")]
    // Semicolon in segment
    [InlineData("/api/v1/sql/statements/stmt;1")]
    [InlineData("/api/sql/statements/stmt;1")]
    [InlineData("/v1/statement/queued/stmt;1/token-1")]
    [InlineData("/v1/statement/queued/stmt-1/token;1")]
    [InlineData("/v1/statement/executing/stmt;1/token-1")]
    [InlineData("/v1/statement/executing/stmt-1/token;1")]
    // Encoded slash in segment (%2F and %2f)
    [InlineData("/api/v1/sql/statements/stmt%2F1")]
    [InlineData("/api/sql/statements/stmt%2f1")]
    [InlineData("/v1/statement/queued/stmt%2F1/token-1")]
    [InlineData("/v1/statement/queued/stmt-1/token%2F1")]
    [InlineData("/v1/statement/executing/stmt%2F1/token-1")]
    [InlineData("/v1/statement/executing/stmt-1/token%2f1")]
    // Dot and double-dot traversal segments
    [InlineData("/api/v1/sql/statements/.")]
    [InlineData("/api/v1/sql/statements/..")]
    [InlineData("/api/sql/statements/.")]
    [InlineData("/api/sql/statements/..")]
    [InlineData("/v1/statement/queued/./token-1")]
    [InlineData("/v1/statement/queued/../token-1")]
    [InlineData("/v1/statement/queued/stmt-1/.")]
    [InlineData("/v1/statement/queued/stmt-1/..")]
    [InlineData("/v1/statement/executing/./token-1")]
    [InlineData("/v1/statement/executing/../token-1")]
    [InlineData("/v1/statement/executing/stmt-1/.")]
    [InlineData("/v1/statement/executing/stmt-1/..")]
    [Trait("Category", "Security")]
    public async Task ReadOnly_DeleteOtherPaths_Is403(string path)
    {
        var (passed, status) = await InvokeAsync("DELETE", path);

        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("X-Original-Method", "/api/v1/sql/statements/stmt-123", true)]
    [InlineData("X-Original-Method", "/api/sql/statements/stmt-123", true)]
    [InlineData("X-Original-Method", "/v1/statement/queued/stmt-123/token-1", true)]
    [InlineData("X-Original-Method", "/v1/statement/executing/stmt-123/token-1", true)]
    [InlineData("X-Forwarded-Method", "/api/v1/sql/statements/stmt-123", true)]
    [InlineData("X-Forwarded-Method", "/api/sql/statements/stmt-123", true)]
    [InlineData("X-Forwarded-Method", "/v1/statement/queued/stmt-123/token-1", true)]
    [InlineData("X-Forwarded-Method", "/v1/statement/executing/stmt-123/token-1", true)]
    [InlineData("X-Original-Method", "/api/v1/sql", false)]
    [InlineData("X-Original-Method", "/api/sql", false)]
    [InlineData("X-Original-Method", "/v1/statement", false)]
    [InlineData("X-Original-Method", "/v1/statement/", false)]
    [InlineData("X-Original-Method", "/api/v1/procedures/test", false)]
    [InlineData("X-Original-Method", "/api/v1/sql/statements/stmt%2F123", false)]
    [InlineData("X-Original-Method", "/v1/statement/queued/stmt-1/token-1/extra", false)]
    [InlineData("X-Original-Method", "/v1/statement/queued/../token-1", false)]
    [InlineData("X-Forwarded-Method", "/api/v1/sql", false)]
    [InlineData("X-Forwarded-Method", "/api/sql", false)]
    [InlineData("X-Forwarded-Method", "/v1/statement", false)]
    [InlineData("X-Forwarded-Method", "/v1/statement/", false)]
    [InlineData("X-Forwarded-Method", "/api/v1/procedures/test", false)]
    [InlineData("X-Forwarded-Method", "/api/v1/sql/statements/stmt%2F123", false)]
    [InlineData("X-Forwarded-Method", "/v1/statement/queued/stmt-1/token-1/extra", false)]
    [InlineData("X-Forwarded-Method", "/v1/statement/queued/../token-1", false)]
    [Trait("Category", "Security")]
    public async Task ReadOnly_OriginalMethodDelete_OnlyOnStatementPaths(string header, string path, bool expectedPassed)
    {
        // When probed with GET and header = DELETE
        var (passedGet, statusGet) = await InvokeAsync("GET", path, h => h[header] = "DELETE");
        passedGet.ShouldBe(expectedPassed);
        if (!expectedPassed)
        {
            statusGet.ShouldBe(StatusCodes.Status403Forbidden);
        }

        // When sent as DELETE with header = DELETE
        var (passedDel, statusDel) = await InvokeAsync("DELETE", path, h => h[header] = "DELETE");
        passedDel.ShouldBe(expectedPassed);
        if (!expectedPassed)
        {
            statusDel.ShouldBe(StatusCodes.Status403Forbidden);
        }
    }
}

using System.Security.Claims;
using System.Net;
using Autheris.Api.Middleware;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// PLAN-PROC-152-INC2 WP-C (D-6, SEC-PROC152I2-09): a read-only token may POST to exactly one procedure segment below
/// /api/v1/procedures, matched on the raw path without decoding.
/// </summary>
public class ReadOnlyTokenProcedureRouteTests
{
    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private static async Task<(bool Passed, int Status)> Invoke(string method, string path, Action<IHeaderDictionary>? headers = null)
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
    [InlineData("/api/v1/procedures/get_orders", true)]
    [InlineData("/API/V1/Procedures/x", true)]
    [InlineData("/api/v1/procedures/openapi.jsonx", true)]
    [InlineData("/api/v1/procedures", false)]
    [InlineData("/api/v1/procedures/", false)]
    [InlineData("/api/v1/procedures//x", false)]
    [InlineData("/api/v1/procedures/x/", false)]
    [InlineData("/api/v1/procedures/x/y", false)]
    [InlineData("/api/v1/procedures/openapi.json", false)]
    [InlineData("/api/v1/procedures/OpenAPI.JSON", false)]
    [InlineData("/API/V1/PROCEDURES/OpenAPI.JSON", false)]
    [InlineData("/api/v1/procedures/openapi.json/", false)]
    [InlineData("/api/v1/procedures/a%2Fb", false)]
    [InlineData("/api/v1/procedures/a%2fb", false)]
    [InlineData("/api/v1/procedures/x;y", false)]
    [InlineData("/api/v1/procedures/x/..", false)]
    [InlineData("/api/v1/procedures/..", false)]
    [InlineData("/api/v1/procedures/.", false)]
    [InlineData("/api/v1/proceduresX/a", false)]
    [InlineData("/api/v1/procedures-x", false)]
    [InlineData("/api/v1/procedures-x/a", false)]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_11_ReadOnlyToken_PostProcedurePaths(string path, bool expectedPassed)
    {
        var (passed, status) = await Invoke("POST", path);

        passed.ShouldBe(expectedPassed);
        if (!expectedPassed) status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("X-Original-Method")]
    [InlineData("X-Forwarded-Method")]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_12_ReadOnlyToken_PostProcedure_ForwardedOrOriginalMethodPost_Forbidden(string header)
    {
        var (passed, status) = await Invoke("GET", "/api/v1/procedures/x", h => h[header] = "POST");
        passed.ShouldBeFalse();
        status.ShouldBe(StatusCodes.Status403Forbidden);

        var (passedPost, statusPost) = await Invoke("POST", "/api/v1/procedures/x", h => h[header] = "DELETE");
        passedPost.ShouldBeFalse();
        statusPost.ShouldBe(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Real routed request over Kestrel: the middleware sits in front of the same route shape as MapProcedureEndpoints
    /// ("/", "/openapi.json", "/{name}"). A permitted path must reach the registered {name} route, a forbidden one no handler.
    /// </summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_PROC152I2_11_RealRoutedRequest_PermittedReachesNameRoute_ForbiddenReachesNoHandler()
    {
        var hits = new List<string>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.Use((ctx, next) => { ctx.User = ReadOnlyPrincipal(); return next(); });
        app.UseMiddleware<ReadOnlyTokenMiddleware>(new PathString("/graphql"), new PathString("/mcp"));
        app.UseRouting();
        var group = app.MapGroup("/api/v1/procedures");
        group.MapGet("/", () => { lock (hits) hits.Add("list"); return "list"; });
        group.MapPost("/openapi.json", () => { lock (hits) hits.Add("openapi"); return "x"; });
        group.MapPost("/{name}", (string name) => { lock (hits) hits.Add("name:" + name); return "ok"; });
        await app.StartAsync();
        var address = app.Urls.First();
        using var client = new HttpClient { BaseAddress = new Uri(address) };

        var permitted = await client.PostAsync("/api/v1/procedures/get_orders", new StringContent("{}"));
        permitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync("/API/V1/Procedures/Up", new StringContent("{}"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        hits.ShouldBe(["name:get_orders", "name:Up"]);

        hits.Clear();
        foreach (var path in new[]
                 {
                     "/api/v1/procedures/openapi.json", "/api/v1/procedures/OpenAPI.JSON", "/api/v1/procedures/a%2Fb",
                     "/api/v1/procedures/x/y", "/api/v1/procedures/x/", "/api/v1/procedures", "/api/v1/procedures/"
                 })
        {
            var response = await client.PostAsync(path, new StringContent("{}"));
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        hits.ShouldBeEmpty();
        await app.StopAsync();
    }
}

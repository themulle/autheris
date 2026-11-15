namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-PROC-152-INC2 WP-C (D-6, SEC-PROC152I2-09): real Kestrel routed request check for read-only token
/// procedure routes (moved out of Autheris.Tests.Unit so unit tests never host Kestrel).
/// </summary>
[Trait("Category", "Security")]
public sealed class ReadOnlyTokenProcedureRouteIntegrationTests
{
    private static ClaimsPrincipal ReadOnlyPrincipal()
    {
        var identity = new ClaimsIdentity([new Claim("oid", "33333333-3333-3333-3333-333333333333")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
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

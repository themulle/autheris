namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class SqlEndpointResponseFormatTests
{
    private static HttpContext CreateContext(string path = "/api/v1/queries/customers", string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-SQL-USER"), new Claim("tenant_id", "tenant-1")], "Test"));
        return context;
    }

    [Fact]
    public async Task HandleGetEndpoint_Default_ReturnsEnvelopeWithColumnsRowsCountAndTruncated()
    {
        var context = CreateContext();
        var execution = Substitute.For<ISqlEndpointExecutionService>();
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" },
            new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Bob" }
        };
        var result = new GovernedSqlResult("SELECT 1", "SELECT 1", ["id", "name"], rows, 2, 5, Truncated: true);
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

        var options = Options.Create(new GatewayOptions());
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeTrue();

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var root = json.RootElement;

        root.ValueKind.ShouldBe(JsonValueKind.Object);
        root.GetProperty("columns").GetArrayLength().ShouldBe(2);
        root.GetProperty("rows").GetArrayLength().ShouldBe(2);
        root.GetProperty("rowCount").GetInt32().ShouldBe(2);
        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task HandleGetEndpoint_WithFormatRows_ReturnsRawRowArray()
    {
        var context = CreateContext(queryString: "?format=rows");
        var execution = Substitute.For<ISqlEndpointExecutionService>();
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" }
        };
        var result = new GovernedSqlResult("SELECT 1", "SELECT 1", ["id", "name"], rows, 1, 5, Truncated: false);
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

        var options = Options.Create(new GatewayOptions());
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeFalse();

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var root = json.RootElement;

        root.ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetArrayLength().ShouldBe(1);
    }
}

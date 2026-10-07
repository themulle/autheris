namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Domain.Common;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ODataEndpointsTests
{
    private static DefaultHttpContext CreateHttpContext(string queryString = "", string path = "/odata/v4/sales/dbo/invoices")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 8080);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-12345"), new Claim("tenant_id", "sales")],
            "TestAuth"));
        return context;
    }

    private static IODataHandler CreateMockHandler(ODataQueryResult result)
    {
        var handler = Substitute.For<IODataHandler>();
        handler.ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<string>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return handler;
    }

    [Fact]
    public async Task HandleEntitySetRequestAsync_AlwaysSetsODataVersion4Header_OnSuccess()
    {
        var context = CreateHttpContext();
        var table = new TableIdentifier("sales", "dbo", "invoices");
        var rows = new List<IReadOnlyDictionary<string, object?>> { new Dictionary<string, object?> { ["id"] = 1 } };
        var payload = ODataResponseFormatter.FormatEntitySetResponse("http://localhost:8080/odata/v4", table, rows);
        var handler = CreateMockHandler(new ODataQueryResult(true, StatusCodes.Status200OK, payload));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        context.Response.Headers["OData-Version"].ToString().ShouldBe("4.0");
        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("?$top=-1")]
    [InlineData("?$top=-100")]
    [InlineData("?$top=abc")]
    [InlineData("?$top=12.5")]
    public async Task HandleEntitySetRequestAsync_InvalidTop_Returns400WithODataVersionHeader(string queryString)
    {
        var context = CreateHttpContext(queryString);
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new object()));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        context.Response.Headers["OData-Version"].ToString().ShouldBe("4.0");
        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        await handler.DidNotReceive().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("?$skip=-1")]
    [InlineData("?$skip=-50")]
    [InlineData("?$skip=xyz")]
    [InlineData("?$skip=3.14")]
    public async Task HandleEntitySetRequestAsync_InvalidSkip_Returns400WithODataVersionHeader(string queryString)
    {
        var context = CreateHttpContext(queryString);
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new object()));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        context.Response.Headers["OData-Version"].ToString().ShouldBe("4.0");
        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        await handler.DidNotReceive().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleEntitySetRequestAsync_TopAndSkip_ParsedCorrectly()
    {
        var context = CreateHttpContext("?$top=25&$skip=50");
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new Dictionary<string, object?> { ["value"] = Array.Empty<object>() }));

        await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        await handler.Received().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            serviceRootUrl: "http://localhost:8080/odata/v4",
            table: Arg.Is<TableIdentifier>(t => t.Domain == "sales" && t.Schema == "dbo" && t.TableName == "invoices"),
            top: 25,
            skip: 50,
            select: null,
            includeCount: false,
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task HandleEntitySetRequestAsync_Select_ForwardedToHandler()
    {
        var context = CreateHttpContext("?$select=id,customer,amount");
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new Dictionary<string, object?> { ["value"] = Array.Empty<object>() }));

        await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        await handler.Received().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<string>(),
            Arg.Any<TableIdentifier>(),
            top: null,
            skip: null,
            select: "id,customer,amount",
            includeCount: false,
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Theory]
    [InlineData("?$count=true", true)]
    [InlineData("?$count=TRUE", true)]
    [InlineData("?$count=false", false)]
    [InlineData("?$count=notabool", false)]
    [InlineData("", false)]
    public async Task HandleEntitySetRequestAsync_Count_ParsedCorrectly(string queryString, bool expectedCount)
    {
        var context = CreateHttpContext(queryString);
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new Dictionary<string, object?> { ["value"] = Array.Empty<object>() }));

        await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        await handler.Received().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<string>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            includeCount: expectedCount,
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>()
        );
    }

    [Fact]
    public async Task HandleEntitySetRequestAsync_Headers_ForwardedToHandler()
    {
        var context = CreateHttpContext();
        context.Request.Headers["X-Custom-Tenant"] = "tenant_123";
        var handler = CreateMockHandler(new ODataQueryResult(true, 200, new Dictionary<string, object?> { ["value"] = Array.Empty<object>() }));

        await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        await handler.Received().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<string>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            headers: Arg.Is<IReadOnlyDictionary<string, string[]>?>(h => h != null && h.ContainsKey("X-Custom-Tenant") && h["X-Custom-Tenant"].Contains("tenant_123")),
            Arg.Any<CancellationToken>()
        );
    }

    [Theory]
    [InlineData(StatusCodes.Status401Unauthorized, "UNAUTHORIZED")]
    [InlineData(StatusCodes.Status403Forbidden, "ACCESS_DENIED")]
    [InlineData(StatusCodes.Status404NotFound, "NOT_FOUND")]
    [InlineData(StatusCodes.Status500InternalServerError, "INTERNAL_ERROR")]
    [InlineData(StatusCodes.Status504GatewayTimeout, "ExecutionTimeout")]
    public async Task HandleEntitySetRequestAsync_PropagatesHandlerErrorsWithODataVersionHeader(int statusCode, string errorCode)
    {
        var context = CreateHttpContext();
        var payload = ODataResponseFormatter.FormatErrorResponse(errorCode, "Simulated error condition");
        var handler = CreateMockHandler(new ODataQueryResult(false, statusCode, payload, errorCode, "Simulated error condition"));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        context.Response.Headers["OData-Version"].ToString().ShouldBe("4.0");
        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(statusCode);
    }

    [Fact]
    public void ExtractEntitySetRows_StripsODataAnnotations()
    {
        var payload = new Dictionary<string, object?>
        {
            ["@odata.context"] = "http://localhost/odata/v4/$metadata#sales_dbo_invoices",
            ["@odata.count"] = 2,
            ["value"] = new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>
                {
                    ["@odata.id"] = "http://localhost/odata/v4/sales/dbo/invoices(1)",
                    ["@odata.etag"] = "W/\"1234\"",
                    ["id"] = 1,
                    ["name"] = "Alice"
                },
                new Dictionary<string, object?>
                {
                    ["@odata.id"] = "http://localhost/odata/v4/sales/dbo/invoices(2)",
                    ["id"] = 2,
                    ["name"] = "Bob"
                }
            }
        };

        var rows = ODataEndpoints.ExtractEntitySetRows(payload);

        rows.Count.ShouldBe(2);
        rows[0].ContainsKey("@odata.id").ShouldBeFalse();
        rows[0].ContainsKey("@odata.etag").ShouldBeFalse();
        rows[0]["id"].ShouldBe(1);
        rows[0]["name"].ShouldBe("Alice");

        rows[1].ContainsKey("@odata.id").ShouldBeFalse();
        rows[1]["id"].ShouldBe(2);
        rows[1]["name"].ShouldBe("Bob");
    }

    [Fact]
    public void ExtractEntitySetRows_WhenNullOrEmpty_ReturnsEmptyList()
    {
        ODataEndpoints.ExtractEntitySetRows(null).ShouldBeEmpty();
        ODataEndpoints.ExtractEntitySetRows(new Dictionary<string, object?>()).ShouldBeEmpty();
    }

    [Fact]
    public void ExtractEntitySetRows_WithJsonElementPayload_ExtractsCorrectly()
    {
        var json = """
        {
            "@odata.context": "http://localhost/odata/v4/$metadata#invoices",
            "value": [
                { "@odata.id": "inv(1)", "id": 10, "amount": 100.5 },
                { "id": 20, "amount": 250.0 }
            ]
        }
        """;
        var doc = JsonDocument.Parse(json);

        var rows = ODataEndpoints.ExtractEntitySetRows(doc.RootElement);

        rows.Count.ShouldBe(2);
        rows[0]["id"]!.ToString().ShouldBe("10");
        rows[0].ContainsKey("@odata.id").ShouldBeFalse();
        rows[1]["id"]!.ToString().ShouldBe("20");
    }

    [Fact]
    public void IsOpenApiAdmin_ChecksClusterAndGovernanceAdminRoles()
    {
        var clusterAdmin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "ClusterAdmin")], "auth"));
        var govAdmin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "GovernanceAdmin")], "auth"));
        var regularUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "DataAnalyst")], "auth"));
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        ODataEndpoints.IsOpenApiAdmin(clusterAdmin).ShouldBeTrue();
        ODataEndpoints.IsOpenApiAdmin(govAdmin).ShouldBeTrue();
        ODataEndpoints.IsOpenApiAdmin(regularUser).ShouldBeFalse();
        ODataEndpoints.IsOpenApiAdmin(anonymous).ShouldBeFalse();
        ODataEndpoints.IsOpenApiAdmin(null).ShouldBeFalse();
    }
}

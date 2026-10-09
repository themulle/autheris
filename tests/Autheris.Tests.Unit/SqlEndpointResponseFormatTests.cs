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
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task HandleGetEndpoint_InProduction_SanitizesSecurityException()
    {
        var prodEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(prodEnv);
        var sp = services.BuildServiceProvider();

        var context = CreateContext();
        context.RequestServices = sp;

        var execution = Substitute.For<ISqlEndpointExecutionService>();
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new System.Security.SecurityException("Secret table hr.salaries is denied")));

        var options = Options.Create(new GatewayOptions());
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var error = json.RootElement.GetProperty("error").GetString();
        error.ShouldBe(WebSqlEndpoints.GenericForbiddenMessage);
        error.ShouldNotBeNull();
        error.ShouldNotContain("Secret table hr.salaries");
    }

    [Fact]
    public async Task HandleGetEndpoint_InProduction_SanitizesArgumentException()
    {
        var prodEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        prodEnv.EnvironmentName.Returns("Production");

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(prodEnv);
        var sp = services.BuildServiceProvider();

        var context = CreateContext();
        context.RequestServices = sp;

        var execution = Substitute.For<ISqlEndpointExecutionService>();
        execution.ExecuteEndpointAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object?>?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GovernedSqlResult>(new ArgumentException("Invalid internal parameter @secretParam")));

        var options = Options.Create(new GatewayOptions());
        await SqlEndpointRoutes.HandleGetEndpoint("customers", context, execution, options, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        var error = json.RootElement.GetProperty("error").GetString();
        error.ShouldBe(WebSqlEndpoints.GenericBadRequestMessage);
        error.ShouldNotBeNull();
        error.ShouldNotContain("Invalid internal parameter @secretParam");
    }
}


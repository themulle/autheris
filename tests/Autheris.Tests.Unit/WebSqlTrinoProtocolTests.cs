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
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlTrinoProtocolTests
{
    private static readonly TenantId TestTenant = new("tenant-trino");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.PrimarySid, "user-trino"),
        new Claim("tenant_id", "tenant-trino"),
        new Claim("tid", "tenant-trino"),
        new Claim(ClaimTypes.Name, "TrinoUser")
    }, "TestAuth"));

    [Theory]
    [InlineData("5s", 5000)]
    [InlineData("500ms", 500)]
    [InlineData("1m", 60000)]
    [InlineData("2000", 2000)]
    [InlineData("10", 10000)]
    public void ParseDuration_ValidInputs_ReturnsExpectedDuration(string input, double expectedMs)
    {
        var duration = WebSqlEndpoints.ParseDuration(input);
        duration.TotalMilliseconds.ShouldBe(expectedMs);
    }

    [Fact]
    public async Task HandleWebSqlRequest_TrinoPostStatement_SynchronousExecution_ReturnsTrinoJson()
    {
        // Arrange
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sqlExecutionService);

        var statementManager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);

        var queryResult = new GovernedSqlResult(
            OriginalSql: "SELECT 1 AS num",
            RewrittenSql: "SELECT 1 AS num",
            Columns: new[] { "num" },
            Rows: new[] { new Dictionary<string, object?> { ["num"] = 1 } },
            RowCount: 1,
            ElapsedMilliseconds: 10);

        sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(queryResult));

        var services = new ServiceCollection();
        services.AddSingleton<IWebSqlStatementManager>(statementManager);
        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = TestUser
        };
        context.Request.Path = "/v1/statement";
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1 AS num"));
        context.Request.Headers["X-Trino-Wait-Timeout"] = "2s";
        context.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        context.Response.Body = new MemoryStream();

        var gatewayOptions = Options.Create(new GatewayOptions());

        // Act
        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            sqlExecutionService,
            gatewayOptions,
            NullLoggerFactory.Instance);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldStartWith("application/json");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("id").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED");
        root.TryGetProperty("nextUri", out _).ShouldBeFalse();

        var columns = root.GetProperty("columns");
        columns.GetArrayLength().ShouldBe(1);
        columns[0].GetProperty("name").GetString().ShouldBe("num");

        var data = root.GetProperty("data");
        data.GetArrayLength().ShouldBe(1);
        data[0][0].GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task HandleWebSqlRequest_TrinoPostStatement_UsesTrinoRowLimits()
    {
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sqlExecutionService);
        using var statementManager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);
        sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("q", "q", ["num"], [], 0, 1));

        var services = new ServiceCollection();
        services.AddSingleton<IWebSqlStatementManager>(statementManager);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = TestUser };
        context.Request.Path = "/v1/statement";
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1 AS num"));
        context.Request.Headers["X-Trino-Wait-Timeout"] = "2s";
        context.Response.Body = new MemoryStream();
        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { DefaultMaxRows = 1000, MaxAllowedRows = 10000 },
            RowLimits = new TransportRowLimitsOptions { Trino = new ChannelRowLimitOptions { MaxAllowedRows = 1000000 } }
        });

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlExecutionService, gatewayOptions, NullLoggerFactory.Instance);

        await sqlExecutionService.Received(1).ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.RowLimit == new SqlRowLimit(1000, 1000000)),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleWebSqlRequest_TrinoPostStatement_SlowQuery_ReturnsRunningAndNextUri()
    {
        // Arrange
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sqlExecutionService);

        var statementManager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);

        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var services = new ServiceCollection();
        services.AddSingleton<IWebSqlStatementManager>(statementManager);
        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = TestUser
        };
        context.Request.Path = "/v1/statement";
        context.Request.ContentType = "text/plain";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT * FROM large_table"));
        context.Request.Headers["X-Trino-Wait-Timeout"] = "10ms";
        context.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        context.Response.Body = new MemoryStream();

        var gatewayOptions = Options.Create(new GatewayOptions());

        // Act
        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            sqlExecutionService,
            gatewayOptions,
            NullLoggerFactory.Instance);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        string statementId = root.GetProperty("id").GetString()!;
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("RUNNING");
        string nextUri = root.GetProperty("nextUri").GetString()!;
        nextUri.ShouldBe($"/v1/statement/queued/{statementId}");

        // Now simulate completion of query
        tcs.SetResult(new GovernedSqlResult(
            OriginalSql: "SELECT * FROM large_table",
            RewrittenSql: "SELECT * FROM large_table",
            Columns: new[] { "id" },
            Rows: new[] { new Dictionary<string, object?> { ["id"] = 99 } },
            RowCount: 1,
            ElapsedMilliseconds: 80));

        // Poll queued endpoint
        var pollContext = new DefaultHttpContext
        {
            RequestServices = sp,
            User = TestUser
        };
        pollContext.Request.Path = nextUri;
        pollContext.Request.Headers["X-Trino-Wait-Timeout"] = "2s";
        pollContext.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        pollContext.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleTrinoQueuedStatementRequest(
            statementId,
            pollContext,
            statementManager,
            NullLoggerFactory.Instance);

        pollContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        pollContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var pollDoc = await JsonDocument.ParseAsync(pollContext.Response.Body);
        var pollRoot = pollDoc.RootElement;

        pollRoot.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FINISHED");
        pollRoot.GetProperty("data")[0][0].GetInt32().ShouldBe(99);
    }

    [Fact]
    public async Task HandleTrinoCancelStatementRequest_CancelsQuery_Returns204()
    {
        // Arrange
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sqlExecutionService);

        var statementManager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);
        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await statementManager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"),
            TestUser,
            TestTenant,
            TimeSpan.FromMilliseconds(10));

        var cancelContext = new DefaultHttpContext
        {
            User = TestUser
        };
        cancelContext.Request.Headers["X-Tenant-Id"] = TestTenant.Value;

        // Act
        await WebSqlEndpoints.HandleTrinoCancelStatementRequest(
            status.StatementId,
            cancelContext,
            statementManager);

        // Assert
        cancelContext.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
    }

    [Fact]
    public async Task HandleTrinoQueuedStatementRequest_WhenStatementFailed_MasksInternalErrorMessage()
    {
        // Arrange
        var sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        serviceProvider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sqlExecutionService);

        var statementManager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance);
        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        tcs.SetException(new InvalidOperationException("Conversion failed when converting the varchar value 'SECRET_IBAN_42' to data type int"));

        sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var status = await statementManager.SubmitOrWaitAsync(
            new GovernedSqlQueryRequest("SELECT 1"),
            TestUser,
            TestTenant,
            TimeSpan.FromMilliseconds(10));

        var pollContext = new DefaultHttpContext
        {
            User = TestUser
        };
        pollContext.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        pollContext.Response.Body = new MemoryStream();

        // Act
        await WebSqlEndpoints.HandleTrinoQueuedStatementRequest(
            status.StatementId,
            pollContext,
            statementManager,
            NullLoggerFactory.Instance);

        // Assert
        pollContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        pollContext.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(pollContext.Response.Body);
        var root = doc.RootElement;
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        var msg = error.GetProperty("message").GetString();
        msg.ShouldNotBeNull();
        msg.ShouldNotContain("SECRET_IBAN");
        msg.ShouldBe("The SQL statement could not be executed. Contact support with the trace id.");
        error.GetProperty("errorType").GetString().ShouldBe("INTERNAL_ERROR");
        error.GetProperty("errorName").GetString().ShouldBe("INTERNAL_ERROR");
    }
}

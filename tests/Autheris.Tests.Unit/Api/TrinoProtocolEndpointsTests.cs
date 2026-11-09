namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class TrinoProtocolEndpointsTests
{
    private static readonly TenantId TestTenant = new("tenant-trino");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity([
        new Claim(ClaimTypes.PrimarySid, "user-trino"),
        new Claim("tenant_id", "tenant-trino"),
        new Claim("tid", "tenant-trino"),
        new Claim(ClaimTypes.Name, "TrinoUser")
    ], "TestAuth"));

    private static IIngressTransportContext TrinoIngress()
    {
        var ctx = new IngressTransportContext();
        ctx.Set(DataSourceTransportFlags.Trino);
        return ctx;
    }

    private static (DefaultHttpContext Context, IWebSqlStatementManager StatementManager, IGovernedSqlExecutionService SqlService) CreateContext()
    {
        var statementManager = Substitute.For<IWebSqlStatementManager>();
        var sqlService = Substitute.For<IGovernedSqlExecutionService>();

        var services = new ServiceCollection();
        services.AddSingleton(statementManager);
        services.AddSingleton(sqlService);
        services.AddSingleton(TrinoIngress());
        var sp = services.BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = sp,
            User = TestUser
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("gateway.example.com");
        context.Request.Path = "/v1/statement";
        context.Request.ContentType = "text/plain";
        context.Request.Headers["X-Trino-User"] = "TrinoUser";
        context.Request.Headers["X-Tenant-Id"] = TestTenant.Value;
        context.Response.Body = new MemoryStream();

        return (context, statementManager, sqlService);
    }

    [Fact]
    public async Task Submit_WithoutTrinoUser_Returns400()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Headers.Remove("X-Trino-User");
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1"));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("detail").GetString()!.ShouldContain("X-Trino-User");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Submit_TrinoUserHeader_IsNotUsedAsIdentity()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Headers["X-Trino-User"] = "malicious-admin";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1"));

        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StatementExecutionStatus("stmt-1", "FINISHED", ["num"], [[1]], 10)));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        await statementManager.Received(1).SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Is<Sid>(s => s.Value == "user-trino"),
            TestTenant,
            Arg.Any<TimeSpan>(),
            DataSourceTransportFlags.Trino,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_UsesTrinoRowLimitAndTransport()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1"));
        context.Request.Headers["X-Trino-Catalog"] = "sales_db";

        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StatementExecutionStatus("stmt-1", "FINISHED", ["num"], [[1]], 10)));

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { DefaultMaxRows = 100, MaxAllowedRows = 1000 },
            RowLimits = new TransportRowLimitsOptions { Trino = new ChannelRowLimitOptions { MaxAllowedRows = 500000 } }
        });

        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        await statementManager.Received(1).SubmitOrWaitAsync(
            Arg.Is<GovernedSqlQueryRequest>(r =>
                r.Transport == SqlTransports.Trino &&
                r.DataSourceName == "sales_db" &&
                r.RowLimit == new SqlRowLimit(100, 500000)),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            DataSourceTransportFlags.Trino,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_PolicyDenied_Returns200FailedPermissionDenied()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1"));

        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new SecurityException("Cross-tenant policy denied"));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("errorCode").GetInt32().ShouldBe(4);
        error.GetProperty("errorName").GetString().ShouldBe("PERMISSION_DENIED");
    }

    [Fact]
    public async Task Submit_SessionLimit_Returns200FailedQueryQueueFull()
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SELECT 1"));

        statementManager.SubmitOrWaitAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new GatewayThrottledException("Global statement session capacity exceeded.", 5));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("INSUFFICIENT_RESOURCES");
        error.GetProperty("errorCode").GetInt32().ShouldBe(131073);
        error.GetProperty("errorName").GetString().ShouldBe("QUERY_QUEUE_FULL");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Poll_UnknownOrForeignStatement_Returns404()
    {
        var (context, statementManager, _) = CreateContext();

        statementManager.GetStatusOrWaitAsync(
            Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new SecurityException("Cross-tenant access forbidden."));

        await TrinoProtocolEndpoints.HandleTrinoPoll("stmt-foreign", 0, context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Poll_TokenOutOfSequence_Returns404()
    {
        var (context, statementManager, _) = CreateContext();

        var status = new StatementExecutionStatus("stmt-seq", "FINISHED", ["id"], [[1]], 10);
        statementManager.GetStatusOrWaitAsync(
            "stmt-seq",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(status));

        // Total pages is 1 (token 0). Token 5 is out of sequence / out of bounds -> 404
        await TrinoProtocolEndpoints.HandleTrinoPoll("stmt-seq", 5, context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Poll_MaxWait_IsClampedTo300Seconds()
    {
        var (context, statementManager, _) = CreateContext();
        context.Request.QueryString = new QueryString("?maxWait=500s");

        var status = new StatementExecutionStatus("stmt-wait", "FINISHED", ["id"], [[1]], 10);
        statementManager.GetStatusOrWaitAsync(
            "stmt-wait",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<DataSourceTransportFlags>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(status));

        await TrinoProtocolEndpoints.HandleTrinoPoll("stmt-wait", 0, context, statementManager, NullLoggerFactory.Instance);

        await statementManager.Received(1).GetStatusOrWaitAsync(
            "stmt-wait",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            TimeSpan.FromSeconds(300),
            DataSourceTransportFlags.Trino,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancel_OnExecutingNextUri_Returns204()
    {
        var (context, statementManager, _) = CreateContext();

        statementManager.CancelStatementAsync(
            "stmt-1",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        await TrinoProtocolEndpoints.HandleTrinoCancel("stmt-1", 1, context, statementManager);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cancel_ForeignStatement_Returns404()
    {
        var (context, statementManager, _) = CreateContext();

        statementManager.CancelStatementAsync(
            "stmt-foreign",
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<Sid>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>())
            .ThrowsAsync(new SecurityException("Cross-tenant access forbidden."));

        await TrinoProtocolEndpoints.HandleTrinoCancel("stmt-foreign", 0, context, statementManager);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Theory]
    [InlineData("INSERT INTO t (a) VALUES (1)")]
    [InlineData("UPDATE t SET a = 1")]
    [InlineData("DELETE FROM t")]
    [InlineData("MERGE INTO t USING s ON t.a = s.a WHEN MATCHED THEN DELETE")]
    [Trait("Category", "Security")]
    public async Task Trino_write_statement_rejected_fail_closed(string writeSql)
    {
        var (context, statementManager, sqlService) = CreateContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(writeSql));

        var gatewayOptions = Options.Create(new GatewayOptions());
        await TrinoProtocolEndpoints.HandleTrinoSubmit(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        // Nothing must be executed
        await statementManager.DidNotReceiveWithAnyArgs().SubmitOrWaitAsync(
            default!, default!, default!, default!, default, default, default);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;

        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("FAILED");
        var error = root.GetProperty("error");
        error.GetProperty("errorType").GetString().ShouldBe("USER_ERROR");
        error.GetProperty("errorCode").GetInt32().ShouldBe(4);
        error.GetProperty("errorName").GetString().ShouldBe("PERMISSION_DENIED");
    }
}

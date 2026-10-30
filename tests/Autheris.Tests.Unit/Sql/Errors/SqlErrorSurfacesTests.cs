using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Sql.Errors;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Antlr4.Runtime.Misc;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Jobs.Interfaces;
using Autheris.Application.Jobs.Services;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Serialization;
using TrinoSqlEngine.Analysis;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Mcp;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>WP-X0a (SEC-X1-01): no governed error surface returns or persists <c>Exception.Message</c> in any environment.</summary>
public sealed class SqlErrorSurfacesTests
{
    private const string HostileMarker = "A-17'--";

    private static DefaultHttpContext Context(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddSingleton(env).BuildServiceProvider(),
            TraceIdentifier = "trace-1"
        };
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<(int Status, string Body)> ReadAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    public static TheoryData<string> Environments => new() { "Development", "Staging", "Production" };

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Response_NeverContainsExceptionMessage_ForHostileMarker(string environment)
    {
        foreach (var ex in new Exception[]
        {
            new WebSqlPolicyException(HostileMarker),
            new ParseCanceledException(HostileMarker),
            new SqlLimitExceededException(SqlLimitKind.InListItems, TargetSqlDialect.Oracle, 7, 5),
            new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, HostileMarker)
        })
        {
            var context = Context(environment);
            await WebSqlEndpoints.WriteWebSqlErrorAsync(context, ex, NullLogger.Instance, CancellationToken.None);
            var (_, body) = await ReadAsync(context);
            body.ShouldNotContain(HostileMarker);
            body.ShouldContain("\"code\"");
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    public async Task Response_NeverContainsExceptionMessage_InStaging(string environment)
    {
        var context = Context(environment);
        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, new WebSqlPolicyException(HostileMarker), NullLogger.Instance, CancellationToken.None);

        var (status, body) = await ReadAsync(context);

        status.ShouldBe(403);
        body.ShouldNotContain(HostileMarker);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("SQL_POLICY_VIOLATION");
    }

    [Fact]
    public async Task WebSql_Response_HasErrorCodeMessageTraceId_AndLimitKindOnly()
    {
        var context = Context("Production");
        await WebSqlEndpoints.WriteWebSqlErrorAsync(
            context, new SqlLimitExceededException(SqlLimitKind.BindParameters, TargetSqlDialect.Oracle, 987654, 321), NullLogger.Instance, CancellationToken.None);

        var (status, body) = await ReadAsync(context);

        status.ShouldBe(422);
        body.ShouldNotContain("987654");
        body.ShouldNotContain("321");
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("code").GetString().ShouldBe("SQL_LIMIT_EXCEEDED");
        root.GetProperty("limit").GetString().ShouldBe("BindParameters");
        root.GetProperty("traceId").GetString().ShouldBe("trace-1");
        root.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task WebSql_UnregisteredAndDeniedTable_AreByteIdentical_ExceptTraceId()
    {
        var a = Context("Production");
        var b = Context("Production");
        await WebSqlEndpoints.WriteWebSqlErrorAsync(a, SqlDataSourceResolver.TableDenied(new TableAccessTarget("ds", "dbo", "missing", null, "ds.dbo.missing")), NullLogger.Instance, CancellationToken.None);
        await WebSqlEndpoints.WriteWebSqlErrorAsync(b, new TrinoSqlEngine.Governance.PolicyConflictException("exists"), NullLogger.Instance, CancellationToken.None);

        (await ReadAsync(a)).ShouldBe(await ReadAsync(b));
    }

    [Fact]
    public async Task RecursiveCteDisabled_Is422ConstructNotSupported()
    {
        var context = Context("Production");
        await WebSqlEndpoints.WriteWebSqlErrorAsync(
            context, new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, HostileMarker), NullLogger.Instance, CancellationToken.None);

        var (status, body) = await ReadAsync(context);

        status.ShouldBe(422);
        body.ShouldNotContain(HostileMarker);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("SQL_CONSTRUCT_NOT_SUPPORTED");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, 530)]
    [InlineData(TargetSqlDialect.Oracle, 32044)]
    public async Task SEC_277_07_RecursionLimitProviderError_ResponseContainsNoDriverText(TargetSqlDialect dialect, int number)
    {
        var mapped = DmlErrorSanitizer.Map(dialect, new FakeRecursionDbException(HostileMarker, number));
        var context = Context("Production");

        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, mapped, NullLogger.Instance, CancellationToken.None);

        var (status, body) = await ReadAsync(context);
        status.ShouldBe(422);
        body.ShouldNotContain(HostileMarker);
        mapped.Message.ShouldNotContain(HostileMarker);
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("code").GetString().ShouldBe("SQL_LIMIT_EXCEEDED");
        root.GetProperty("detail").GetString().ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage("SQL_LIMIT_EXCEEDED"));
    }

    private sealed class FakeRecursionDbException(string message, int number) : System.Data.Common.DbException(message)
    {
        public int Number { get; } = number;
    }

    [Fact]
    public async Task TimeoutBeforeSanitizer_Stays504_OnWebSql()
    {
        var context = Context("Production");
        await WebSqlEndpoints.WriteWebSqlErrorAsync(context, new InvalidOperationException("w", new TimeoutException(HostileMarker)), NullLogger.Instance, CancellationToken.None);

        var (status, body) = await ReadAsync(context);

        status.ShouldBe(504);
        body.ShouldNotContain(HostileMarker);
    }

    [Fact]
    public async Task GlobalHandler_MapsCompilerErrors_WithCodeExtension()
    {
        var context = Context("Staging");
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);

        await handler.TryHandleAsync(context, new SqlLimitExceededException(SqlLimitKind.QueryLength, TargetSqlDialect.Oracle, 9, 8), CancellationToken.None);

        var (status, body) = await ReadAsync(context);
        status.ShouldBe(422);
        var root = JsonDocument.Parse(body).RootElement;
        root.GetProperty("code").GetString().ShouldBe("SQL_LIMIT_EXCEEDED");
        root.GetProperty("limit").GetString().ShouldBe("QueryLength");
    }

    [Theory]
    [MemberData(nameof(Environments))]
    public async Task Flight_MapsStatusAndCode_WithoutMessage(string environment)
    {
        var server = Substitute.For<IArrowFlightSqlServer>();
        server.DoGetStreamAsync(Arg.Any<FlightSqlTicket>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ThrowingStream(new WebSqlPolicyException(HostileMarker)));
        var context = Context(environment);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1"), new Claim("tenant_id", "t")], "Test"));

        var result = await ArrowFlightSqlEndpoints.HandleStreamAsync(new FlightSqlTicket("t", "t", "SELECT 1", DateTimeOffset.UtcNow, "sig"), context, server);
        await result.ExecuteAsync(context);

        var (status, body) = await ReadAsync(context);
        status.ShouldBe(403);
        body.ShouldNotContain(HostileMarker);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("SQL_POLICY_VIOLATION");
    }

    private static async IAsyncEnumerable<Apache.Arrow.RecordBatch> ThrowingStream(Exception ex)
    {
        await Task.Yield();
        throw ex;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static McpToolDefinition Tool(string name) => new(name, name, "{}", "");

    private static McpSessionContext Session() =>
        new("s1", "spn-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: ["Reader"]);

    [Fact]
    public async Task Mcp_ToolError_CarriesCode()
    {
        var handler = Substitute.For<IMcpToolExecutionHandler>();
        handler.CanHandle(Arg.Any<string>()).Returns(true);
        handler.ExecuteToolAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new SqlLimitExceededException(SqlLimitKind.InListItems, TargetSqlDialect.Oracle, 7, 5));
        var executor = new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(), Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, toolExecutionHandler: handler);

        var json = await executor.ExecuteOperationAsync(Tool("query_sql"), "{}", Session());

        var error = JsonDocument.Parse(json).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("SQL_LIMIT_EXCEEDED");
        json.ShouldNotContain("InListItems");
    }

    [Fact]
    public async Task McpExecutor_ArgumentException_DoesNotEchoMessage()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.DescribeDatasetAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpDatasetDescription>>(_ => throw new ArgumentException(HostileMarker));
        var executor = new GatewayMcpQueryExecutor(
            Substitute.For<IRequestExecutorProvider>(), Substitute.For<IGatewayExecutionService>(),
            NullLogger<GatewayMcpQueryExecutor>.Instance, datasetCatalog: catalog);

        var json = await executor.ExecuteOperationAsync(Tool(McpDatasetTools.DescribeDataset), """{"dataset":"a.b.c"}""", Session());

        json.ShouldContain("isError\":true");
        json.ShouldNotContain(HostileMarker);
    }

    [Fact]
    public async Task AsyncStatement_ErrorMessage_IsFixedMessageOfCode()
    {
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IGovernedSqlExecutionService)).Returns(sql);
        using var manager = new WebSqlStatementManager(scopeFactory, NullLogger<WebSqlStatementManager>.Instance, retentionPeriod: TimeSpan.FromMinutes(5));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "u")], "T"));
        var request = new GovernedSqlQueryRequest("SELECT 1 FROM a.b.c");
        var tcs = new TaskCompletionSource<GovernedSqlResult>();
        tcs.SetException(new SqlLimitExceededException(SqlLimitKind.QueryLength, TargetSqlDialect.Oracle, 9, 8));
        sql.ExecuteQueryBufferedAsync(request, user, new TenantId("t"), Arg.Any<CancellationToken>()).Returns(tcs.Task);

        var status = await manager.SubmitOrWaitAsync(request, user, new Sid("u"), new TenantId("t"), TimeSpan.FromSeconds(2));

        status.State.ShouldBe("FAILED");
        status.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage("SQL_LIMIT_EXCEEDED"));
    }

    [Fact]
    public async Task AsyncJob_PersistedError_IsFixedMessageOfCode()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "autheris-x0a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var env = Substitute.For<IHostEnvironment>();
            env.ContentRootPath.Returns(scratch);
            var audit = Substitute.For<IAuditLogRepository>();
            var manager = new AsyncQueryJobManager(audit, environment: env);
            var sql = Substitute.For<IGovernedSqlExecutionService>();
            sql.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns<Task<GovernedSqlResult>>(_ => throw new WebSqlPolicyException(HostileMarker + " password=hunter2"));
            var services = new ServiceCollection();
            services.AddScoped(_ => sql);
            var worker = new AsyncQueryJobBackgroundWorker(
                manager, manager, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), audit, NullLogger<AsyncQueryJobBackgroundWorker>.Instance);
            var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("sub", "alice"), new Claim("tenant_id", "t")], "T"));
            var descriptor = await manager.SubmitJobAsync(new AsyncQueryJobRequest("SELECT 1", Format: "json"), user, new TenantId("t"));

            await worker.StartAsync(CancellationToken.None);
            AsyncJobDescriptor? failed = null;
            await WaitHelper.WaitUntil(async () =>
            {
                var job = await manager.GetJobAsync(descriptor.JobId, new TenantId("t"), "alice");
                failed = job?.State == AsyncJobState.Failed ? job : null;
                return failed is not null;
            }, TimeSpan.FromSeconds(10), pollInterval: TimeSpan.FromMilliseconds(50));
            await worker.StopAsync(CancellationToken.None);

            failed.ShouldNotBeNull();
            failed.ErrorMessage.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage("SQL_POLICY_VIOLATION"));
        }
        finally
        {
            try { Directory.Delete(scratch, true); } catch { }
        }
    }
}

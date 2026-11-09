namespace Autheris.Tests.Unit.Api;

using System;
using System.IO;
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
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebSqlStatementTransportTests
{
    private static readonly TenantId TestTenant = new("tenant-websql");
    private static readonly ClaimsPrincipal TestUser = new(new ClaimsIdentity(new[]
    {
        new Claim(ClaimTypes.PrimarySid, "user-websql"),
        new Claim("tenant_id", "tenant-websql"),
        new Claim("tid", "tenant-websql"),
        new Claim(ClaimTypes.Name, "WebSqlUser")
    }, "TestAuth"));

    private sealed class RecordingStatementManager : IWebSqlStatementManager
    {
        public GovernedSqlQueryRequest? LastSubmittedRequest { get; private set; }
        public TimeSpan LastWaitTimeout { get; private set; }
        public DataSourceTransportFlags LastIngress { get; private set; }
        public StatementExecutionStatus? StatusToReturn { get; set; }

        public Task<StatementExecutionStatus> SubmitOrWaitAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            Sid owner,
            TenantId tenantId,
            TimeSpan waitTimeout,
            DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
            CancellationToken ct = default)
        {
            LastSubmittedRequest = request;
            LastWaitTimeout = waitTimeout;
            LastIngress = ingress;
            return Task.FromResult(StatusToReturn ?? new StatementExecutionStatus("stmt-1", "RUNNING", null, null, 0));
        }

        public Task<StatementExecutionStatus> GetStatusOrWaitAsync(
            string statementId,
            ClaimsPrincipal user,
            Sid owner,
            TenantId tenantId,
            TimeSpan waitTimeout,
            DataSourceTransportFlags ingress = DataSourceTransportFlags.None,
            CancellationToken ct = default)
        {
            return Task.FromResult(StatusToReturn ?? new StatementExecutionStatus(statementId, "RUNNING", null, null, 0));
        }

        public Task<bool> CancelStatementAsync(
            string statementId,
            ClaimsPrincipal user,
            Sid owner,
            TenantId tenantId,
            CancellationToken ct = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingSqlExecutionService : IGovernedSqlExecutionService
    {
        public GovernedSqlQueryRequest? LastRequest { get; private set; }
        public int ExecuteQueryCallCount { get; private set; }

        public Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            Func<System.Data.Common.DbDataReader, GovernedResultInfo, CancellationToken, Task> rowWriter,
            CancellationToken ct = default)
        {
            LastRequest = request;
            ExecuteQueryCallCount++;
            return Task.CompletedTask;
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
            GovernedSqlQueryRequest request,
            ClaimsPrincipal user,
            TenantId tenantId,
            CancellationToken ct = default)
        {
            LastRequest = request;
            ExecuteQueryCallCount++;
            return Task.FromResult(new GovernedSqlResult(request.Sql, ["col"], [], 0, 5));
        }

        public Task ValidateAsync(
            string rawSql,
            ClaimsPrincipal user,
            TenantId tenantId,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (DefaultHttpContext Context, RecordingStatementManager StatementManager, RecordingSqlExecutionService SqlService)
        CreateTestContext(DataSourceTransportFlags ingressFlag)
    {
        var statementManager = new RecordingStatementManager();
        var sqlService = new RecordingSqlExecutionService();

        var services = new ServiceCollection();
        services.AddSingleton<IWebSqlStatementManager>(statementManager);
        services.AddSingleton<IGovernedSqlExecutionService>(sqlService);

        var ingress = new IngressTransportContext();
        if (ingressFlag != DataSourceTransportFlags.None)
        {
            ingress.Set(ingressFlag);
        }
        services.AddSingleton<IIngressTransportContext>(ingress);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = TestUser
        };
        context.Response.Body = new MemoryStream();
        return (context, statementManager, sqlService);
    }

    [Fact]
    public async Task WebSqlWaitTimeout_UsesWebSqlTransportAndRowLimit()
    {
        var (context, statementManager, sqlService) = CreateTestContext(DataSourceTransportFlags.WebSql);
        context.Request.Path = "/api/v1/sql";
        context.Request.QueryString = new QueryString("?wait_timeout=2s");
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\"}"));

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { DefaultMaxRows = 100, MaxAllowedRows = 1000 },
            RowLimits = new TransportRowLimitsOptions { Trino = new ChannelRowLimitOptions { MaxAllowedRows = 5000000 } }
        });

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        statementManager.LastSubmittedRequest.ShouldNotBeNull();
        statementManager.LastSubmittedRequest.Transport.ShouldBe(SqlTransports.WebSql);
        statementManager.LastSubmittedRequest.RowLimit.ShouldBe(SqlRowLimit.For(gatewayOptions.Value.WebSql));
        statementManager.LastSubmittedRequest.RowLimit.ShouldNotBeNull();
        statementManager.LastSubmittedRequest.RowLimit.MaxAllowedRows.ShouldBe(1000);
        statementManager.LastIngress.ShouldBe(DataSourceTransportFlags.WebSql);
    }

    [Fact]
    public async Task WebSqlWaitTimeout_IgnoresTrinoHeaders()
    {
        var (context, statementManager, sqlService) = CreateTestContext(DataSourceTransportFlags.WebSql);
        context.Request.Path = "/api/v1/sql";
        // No wait_timeout query param; only Trino headers
        context.Request.Headers["X-Trino-Wait-Timeout"] = "2s";
        context.Request.Headers["X-Trino-Catalog"] = "ignored_catalog";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\"}"));

        var gatewayOptions = Options.Create(new GatewayOptions());

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        // Statement manager must NOT be called because X-Trino-Wait-Timeout is ignored on WebSql ingress
        statementManager.LastSubmittedRequest.ShouldBeNull();
        sqlService.ExecuteQueryCallCount.ShouldBe(1);
        sqlService.LastRequest.ShouldNotBeNull();
        sqlService.LastRequest.DataSourceName.ShouldBeNull();
    }

    [Fact]
    public async Task WebSqlPoll_NextUri_IsWebSqlRoute()
    {
        var (context, statementManager, _) = CreateTestContext(DataSourceTransportFlags.WebSql);
        context.Request.Path = "/api/v1/sql/statements/stmt-xyz";
        statementManager.StatusToReturn = new StatementExecutionStatus("stmt-xyz", "RUNNING", null, null, 10);

        await WebSqlEndpoints.HandleWebSqlStatementPollRequest(
            "stmt-xyz", context, statementManager, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var root = doc.RootElement;
        root.GetProperty("id").GetString().ShouldBe("stmt-xyz");
        root.GetProperty("stats").GetProperty("state").GetString().ShouldBe("RUNNING");
        var nextUri = root.GetProperty("nextUri").GetString();
        nextUri.ShouldBe("/api/v1/sql/statements/stmt-xyz");
        nextUri.ShouldNotStartWith("/v1/statement");
    }

    [Fact]
    public async Task UnstampedIngress_FailsClosed()
    {
        var (context, statementManager, sqlService) = CreateTestContext(DataSourceTransportFlags.None);
        context.Request.Path = "/api/v1/sql";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT 1\"}"));

        var gatewayOptions = Options.Create(new GatewayOptions());

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, gatewayOptions, NullLoggerFactory.Instance);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(Autheris.Domain.Exceptions.TransportNotAllowedException.Code);
        statementManager.LastSubmittedRequest.ShouldBeNull();
    }
}

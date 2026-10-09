namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Tests for OData hardening findings O1, O6, O7 and API-15 according to
/// docs/plans/rls-subquery-in-strategy.md and docs/plans/security-review-2026-10-07.md.
/// </summary>
public sealed class ODataHardeningTests
{
    private static readonly TableIdentifier Table = new("lwetem_prod", "fms", "air1");

    private static TableMetadata CreateSampleMetadata() => new()
    {
        Identifier = Table,
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "fms", TableName = "air1", SourceType = "SqlServer" },
        PrimaryKeyColumns = ["ts", "client_id"],
        Columns =
        [
            new TableColumn { ColumnName = "ts", DataType = "datetime2" },
            new TableColumn { ColumnName = "client_id", DataType = "int" },
            new TableColumn { ColumnName = "serv_break_press1", DataType = "float" },
            new TableColumn { ColumnName = "secret_col", DataType = "varchar" }
        ]
    };

    private static ClaimsPrincipal CreateUser(string sid = "S-1-5-21-LWE-DAVID") =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, sid),
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("objectSid", sid),
            new Claim(ClaimTypes.Name, "david"),
            new Claim("tenant_id", "lwetem_prod")
        ], "TestAuth", ClaimTypes.Name, ClaimTypes.Role));

    private sealed class CapturingExecutor : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;
        public List<IReadOnlyList<string>> RequestedFields { get; } = [];

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            RequestedFields.Add(context.RequestedFields);
            context.Items["RlsPushdownExecuted"] = true;
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }
    }

    private static (GatewayExecutionService Service, IAuditLogRepository AuditLog, CapturingExecutor Executor) CreateExecutionService()
    {
        var executor = new CapturingExecutor();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(CreateSampleMetadata());

        var decision = TableAccessDecision.Allowed(
            Table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["secret_col"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: true);

        var cache = Substitute.For<IConsentCacheService>();
        cache.GetCachedDecisionAsync(Arg.Any<TenantId>(), Arg.Any<Sid>(), Arg.Any<TableIdentifier>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        var auditLog = Substitute.For<IAuditLogRepository>();

        var service = new GatewayExecutionService(
            metadataRepo,
            Substitute.For<IConsentRepository>(),
            auditLog,
            Substitute.For<IConsentResolutionService>(),
            cache,
            Substitute.For<IColumnMaskingProvider>(),
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [executor]);

        return (service, auditLog, executor);
    }

    // ==========================================
    // O1: $select with unknown or Denied column
    // ==========================================

    [Fact]
    public async Task O1_SelectUnknownColumn_ThrowsGatewayInvalidQueryException_DoesNotAuditDenial()
    {
        var (service, auditLog, _) = CreateExecutionService();
        var user = CreateUser();

        var ex = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(user, Table, 100, 0, null, ["amount"], null));

        ex.Message.ShouldContain("amount");
        ex.Message.ShouldContain("does not exist or is not accessible");

        // Should NOT record a COLUMN_ACCESS_DENIED event because the column doesn't exist at all
        await auditLog.DidNotReceive().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "COLUMN_ACCESS_DENIED"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task O1_SelectDeniedColumn_ThrowsWithSameMessageAsUnknown_AndAuditsRealReason()
    {
        var (service, auditLog, _) = CreateExecutionService();
        var user = CreateUser();

        var exDenied = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(user, Table, 100, 0, null, ["secret_col"], null));

        var exUnknown = await Should.ThrowAsync<GatewayInvalidQueryException>(() =>
            service.ExecuteTableQueryAsync(user, Table, 100, 0, null, ["no_such_col"], null));

        // Same error message to prevent existence oracle
        exDenied.Message.Replace("secret_col", "X").ShouldBe(exUnknown.Message.Replace("no_such_col", "X"));

        // Must record audit entry with real reason
        await auditLog.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "COLUMN_ACCESS_DENIED" &&
                e.Decision == "DENY" &&
                e.DetailsJson.Contains("secret_col")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,")]
    public async Task O1_ODataHandler_SelectEmptyOrWhitespace_Returns400InvalidQueryOption(string emptySelect)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();
        var handler = new ODataHandler(metadataRepo, execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            CreateUser(),
            "http://localhost/odata/v4",
            Table,
            top: 10,
            skip: 0,
            select: emptySelect,
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorCode.ShouldBe("InvalidQueryOption");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("$select");
        await execService.DidNotReceiveWithAnyArgs().ExecuteTablePageAsync(default, default, default!, default);
    }

    [Fact]
    public async Task O1_ODataHandler_SelectUnknownColumn_Returns400InvalidQueryOption()
    {
        var (service, _, _) = CreateExecutionService();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var handler = new ODataHandler(metadataRepo, service, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            CreateUser(),
            "http://localhost/odata/v4",
            Table,
            top: 10,
            skip: 0,
            select: "amount",
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorCode.ShouldBe("InvalidQueryOption");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("amount");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("does not exist or is not accessible");
    }

    // ==========================================
    // O6: Upper bound for $skip (100,000)
    // ==========================================

    [Theory]
    [InlineData("?$skip=100001")]
    [InlineData("?$skip=1400000")]
    [InlineData("?$skip=9999999999")]
    public async Task O6_Endpoint_SkipExceedingMaxSkip_Returns400WithoutCallingHandler(string queryString)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 8080);
        context.Request.Path = "/odata/v4/sales/dbo/invoices";
        context.Request.QueryString = new QueryString(queryString);
        context.User = CreateUser();

        var handler = Substitute.For<IODataHandler>();

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var value = (result as IValueHttpResult)?.Value;
        var json = JsonSerializer.Serialize(value);
        json.ShouldContain("InvalidQueryOption");
        json.ShouldContain("100000");

        await handler.DidNotReceive().ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            Arg.Any<string>(),
            Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task O6_ODataHandler_SkipExceedingMaxSkip_Returns400()
    {
        var execService = Substitute.For<IGatewayExecutionService>();
        var handler = new ODataHandler(Substitute.For<ITableMetadataRepository>(), execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            CreateUser(),
            "http://localhost/odata/v4",
            Table,
            top: 10,
            skip: ODataHandler.MaxSkip + 1,
            select: null,
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.ErrorCode.ShouldBe("InvalidQueryOption");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain("$skip");
        result.ErrorMessage.ShouldNotBeNull().ShouldContain(ODataHandler.MaxSkip.ToString());
        await execService.DidNotReceiveWithAnyArgs().ExecuteTablePageAsync(default, default, default!, default);
    }

    // ==============================================================
    // O7: Timeout (504) and Unavailable (503) errors with Retry-After
    // ==============================================================

    private sealed class FakeDbException(int number, string message) : System.Data.Common.DbException(message)
    {
        public int Number { get; } = number;
    }

    [Theory]
    [InlineData(-2, "Execution Timeout Expired. The timeout period elapsed prior to completion.")]
    public async Task O7_ODataHandler_TimeoutError_Returns504WithRetryAfterAndCleanJson(int number, string internalDbMessage)
    {
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableQueryPage>>(_ => throw new FakeDbException(number, internalDbMessage));

        var handler = new ODataHandler(Substitute.For<ITableMetadataRepository>(), execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            CreateUser(),
            "http://localhost/odata/v4",
            Table,
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
        result.ErrorCode.ShouldBe("ExecutionTimeout");
        result.RetryAfterSeconds.ShouldBe(5);

        // Clean JSON without internal database details / stacktrace
        var json = JsonSerializer.Serialize(result.Payload);
        json.ShouldContain("ExecutionTimeout");
        json.ShouldNotContain(internalDbMessage);
        json.ShouldNotContain("FakeDbException");
        json.ShouldNotContain("Stack");
    }

    [Theory]
    [InlineData(1205, "Transaction was deadlocked on lock resources with another process.")]
    [InlineData(40613, "Database 'lwetem_prod' on server is not currently available.")]
    public async Task O7_ODataHandler_UnavailableError_Returns503WithRetryAfterAndCleanJson(int number, string internalDbMessage)
    {
        var execService = Substitute.For<IGatewayExecutionService>();
        execService.ExecuteTablePageAsync(Arg.Any<ClaimsPrincipal?>(), Arg.Any<TableIdentifier>(), Arg.Any<TablePageRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<TableQueryPage>>(_ => throw new FakeDbException(number, internalDbMessage));

        var handler = new ODataHandler(Substitute.For<ITableMetadataRepository>(), execService, NullLogger<ODataHandler>.Instance);

        var result = await handler.ExecuteEntitySetQueryAsync(
            CreateUser(),
            "http://localhost/odata/v4",
            Table,
            top: 10,
            skip: 0,
            select: null,
            includeCount: false,
            headers: null);

        result.Success.ShouldBeFalse();
        result.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        result.ErrorCode.ShouldBe("ServiceUnavailable");
        result.RetryAfterSeconds.ShouldBe(5);

        // Clean JSON without internal database details / stacktrace
        var json = JsonSerializer.Serialize(result.Payload);
        json.ShouldContain("ServiceUnavailable");
        json.ShouldNotContain(internalDbMessage);
        json.ShouldNotContain("FakeDbException");
        json.ShouldNotContain("Stack");
    }

    [Fact]
    public async Task O7_Endpoint_Propagates504WithRetryAfterHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 8080);
        context.Request.Path = "/odata/v4/sales/dbo/invoices";
        context.User = CreateUser();

        var payload = ODataResponseFormatter.FormatErrorResponse("ExecutionTimeout", "The query exceeded the execution time limit.");
        var handler = Substitute.For<IODataHandler>();
        handler.ExecuteEntitySetQueryAsync(
            Arg.Any<ClaimsPrincipal?>(), Arg.Any<string>(), Arg.Any<TableIdentifier>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<bool>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ODataQueryResult(false, StatusCodes.Status504GatewayTimeout, payload, "ExecutionTimeout", "The query exceeded the execution time limit.", RetryAfterSeconds: 5)));

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        context.Response.Headers.RetryAfter.ToString().ShouldBe("5");
        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
    }

    // ==============================================================
    // API-15: Swagger UI & Metadata challenge outside Development
    // ==============================================================

    [Fact]
    public void Api15_SwaggerAuth_RequiresChallengeForAnonymousOutsideDev()
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Production);
        var anonymous = new DefaultHttpContext();

        var result = ODataEndpoints.CheckSwaggerAuth(new GatewayOptions(), env, anonymous);

        result.ShouldNotBeNull();
        result.GetType().Name.ShouldContain("Challenge");
    }

    [Fact]
    public void Api15_SwaggerAuth_ForbidsAuthenticatedNonAdminOutsideDev()
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Production);
        var nonAdmin = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "CatalogReader")], "TestAuth"))
        };

        var result = ODataEndpoints.CheckSwaggerAuth(new GatewayOptions(), env, nonAdmin);

        result.ShouldNotBeNull();
        result.GetType().Name.ShouldContain("Forbid");
    }

    [Fact]
    public void Api15_MetadataChallenge_RequiresChallengeForAnonymousOutsideDev()
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns(Microsoft.Extensions.Hosting.Environments.Production);
        var anonymous = new DefaultHttpContext();

        ODataEndpoints.RequiresMetadataChallenge(new GatewayOptions(), env, anonymous).ShouldBeTrue();
    }

    [Fact]
    public async Task Befund_1_2_UnsupportedAcceptHeader_Returns406NotAcceptable()
    {
        var handler = Substitute.For<IODataHandler>();
        var context = new DefaultHttpContext();
        context.Request.Headers.Accept = "text/csv";

        var result = await ODataEndpoints.HandleEntitySetRequestAsync("sales", "dbo", "invoices", handler, context);

        var statusResult = result.ShouldBeAssignableTo<IStatusCodeHttpResult>();
        statusResult!.StatusCode.ShouldBe(StatusCodes.Status406NotAcceptable);
        await handler.DidNotReceiveWithAnyArgs().ExecuteEntitySetQueryAsync(
            default, default!, default, default, default, default, default, default, default, default, default);
    }

    [Theory]
    [InlineData("parquet")]
    [InlineData("application/vnd.apache.parquet")]
    public void Befund_1_2_ValidateSystemQueryOptions_AcceptsParquetFormat(string formatValue)
    {
        var query = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
        {
            ["$format"] = formatValue
        });

        var result = ODataEndpoints.ValidateSystemQueryOptions(query);
        result.ShouldBeNull();
    }
}




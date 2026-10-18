namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class BulkKeysetStreamingSecurityTests
{
    private static ClaimsPrincipal CreateReadOnlyPrincipal(string sid = "user-readonly")
    {
        var identity = new ClaimsIdentity([new Claim("oid", sid), new Claim("sid", sid)], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        return new ClaimsPrincipal(identity);
    }

    private static TableMetadata CreateTableMetadata(
        string tableName = "orders",
        IReadOnlyList<string>? primaryKeys = null,
        DatasetVisibility visibility = DatasetVisibility.Full)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", tableName),
            Table = new Table
            {
                TableName = tableName,
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL",
                Visibility = visibility
            },
            PrimaryKeyColumns = primaryKeys ?? ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "secret_ssn", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" }
            ]
        };
    }

    private static DbDataReader CreateDataTableReader(int rowCount)
    {
        var dt = new DataTable();
        dt.Columns.Add("tenant_id", typeof(string));
        dt.Columns.Add("id", typeof(int));
        dt.Columns.Add("secret_ssn", typeof(string));
        dt.Columns.Add("amount", typeof(decimal));

        for (int i = 1; i <= rowCount; i++)
        {
            dt.Rows.Add("tenant-1", i, $"000-00-{i:D4}", (decimal)(i * 10.0));
        }

        return dt.CreateDataReader();
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_01: Read-Only Token Whitelisting for Streaming & Mutation Blocking
    // -----------------------------------------------------------------------------------------
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream", true, StatusCodes.Status200OK)]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/bulk", true, StatusCodes.Status200OK)]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders", false, StatusCodes.Status403Forbidden)] // Mutation blocked!
    [InlineData("PUT", "/api/v1/data/sales/dbo/orders/1", false, StatusCodes.Status403Forbidden)]
    [InlineData("DELETE", "/api/v1/data/sales/dbo/orders/1", false, StatusCodes.Status403Forbidden)]
    public async Task T_SEC_DATA178_01_ReadOnlyToken_StreamingWhitelisted_MutationsBlocked(
        string method,
        string path,
        bool expectPassed,
        int expectedStatus)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ =>
        {
            passed = true;
            return Task.CompletedTask;
        }, "/graphql", "/mcp");

        var context = new DefaultHttpContext
        {
            User = CreateReadOnlyPrincipal()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        passed.ShouldBe(expectPassed);
        context.Response.StatusCode.ShouldBe(expectedStatus);
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_02: Path Traversal & Non-Normalized Characters Rejected With 403
    // -----------------------------------------------------------------------------------------
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream/../../admin")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream/extra")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders%2Fstream")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream;evil")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream\\evil")]
    [InlineData("POST", "/api/v1/data/sales/dbo/orders/stream?action=delete")]
    public async Task T_SEC_DATA178_02_PathTraversal_RejectedWith403(string method, string path)
    {
        var passed = false;
        var middleware = new ReadOnlyTokenMiddleware(_ =>
        {
            passed = true;
            return Task.CompletedTask;
        }, "/graphql", "/mcp");

        var context = new DefaultHttpContext
        {
            User = CreateReadOnlyPrincipal()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        passed.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_03: Typed Filter AST Rejects Dotted Identifier / Table Joins With 400
    // -----------------------------------------------------------------------------------------
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("other_table.column")]
    [InlineData("orders.id")]
    [InlineData("id; DROP TABLE orders--")]
    [InlineData("id union select")]
    public void T_SEC_DATA178_03_FilterAST_DottedIdentifierOrJoin_RejectedWith400(string injectedField)
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata();
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true);

        var filter = new TypedFilterNode(Field: injectedField, Op: "eq");
        var request = new BulkTableStreamRequest(Filter: filter);

        var ex = Should.Throw<ArgumentException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("strictly prohibited");
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_04: Typed Filter AST on Masked/Denied Column Rejected With 400
    // -----------------------------------------------------------------------------------------
    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA178_04_FilterAST_MaskedOrDeniedColumn_RejectedWith400()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata();
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["secret_ssn"] = ColumnAccessLevel.Mask
        };
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess, null, true);

        using var ssnVal = JsonDocument.Parse("\"000-11-2222\"");
        var filter = new TypedFilterNode(Field: "secret_ssn", Op: "eq", Value: ssnVal.RootElement);
        var request = new BulkTableStreamRequest(Filter: filter);

        var ex = Should.Throw<ArgumentException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("Filtering on masked column 'secret_ssn' is rejected");
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_05: Cursor Tampering With Invalid Keys Or Types Rejected With 400 INVALID_CURSOR
    // -----------------------------------------------------------------------------------------
    [Theory]
    [Trait("Category", "Security")]
    [InlineData("corrupted-base64-payload!@#$")]
    [InlineData("eyJpZCI6ICJub3QtYW4taW50ZWdlciJ9")] // {"id": "not-an-integer"}
    [InlineData("eyJpZCI6IDEwMCwgImV4dHJhIjogInRhbXBlcmVkIn0=")] // {"id": 100, "extra": "tampered"}
    public void T_SEC_DATA178_05_CursorTampering_InvalidKeysOrTypes_RejectedWith400(string tamperedCursor)
    {
        var encoder = new KeysetCursorEncoder();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);

        var ex = Should.Throw<InvalidCursorException>(() => encoder.DecodeCursor(tamperedCursor, metadata));
        ex.Message.ShouldContain("INVALID_CURSOR");
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_06: Cursor Seek Predicate Cannot Relax Or Bypass Active Tenant RLS Policy
    // -----------------------------------------------------------------------------------------
    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA178_06_CursorSeek_CannotBypassTenantRLSPolicy()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        // Active RLS policy enforces tenant partition
        var decision = TableAccessDecision.Allowed(
            metadata.Identifier,
            new Dictionary<string, ColumnAccessLevel>(),
            rowFilterSql: "\"tenant_id\" = 'tenant_A'",
            hasUnconstrainedColumnAllow: true);

        var encoder = new KeysetCursorEncoder();
        var cursor = encoder.EncodeCursor(new Dictionary<string, object?> { ["id"] = 999 });

        var request = new BulkTableStreamRequest(Cursor: cursor);
        var planned = planner.PlanKeysetStream(request, metadata, decision);

        // SQL must contain BOTH cursor seek AND mandatory RLS policy conjoined with AND
        planned.Sql.ShouldContain("(\"id\" > @cursor_0)");
        planned.Sql.ShouldContain("(\"tenant_id\" = 'tenant_A')");
        planned.Sql.ShouldContain("WHERE (\"id\" > @cursor_0) AND (\"tenant_id\" = 'tenant_A')");
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_07: Masked Primary Key Fails Closed With KEYSET_STREAMING_UNAVAILABLE
    // -----------------------------------------------------------------------------------------
    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA178_07_MaskedPrimaryKey_FailsClosedWithForbiddenOrUnavailable()
    {
        var planner = new GovernedTableReadPlanner();
        var metadata = CreateTableMetadata(primaryKeys: ["id"]);
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Mask
        };
        var decision = TableAccessDecision.Allowed(metadata.Identifier, columnAccess, null, true);

        var request = new BulkTableStreamRequest();
        var ex = Should.Throw<KeysetStreamingUnavailableException>(() => planner.PlanKeysetStream(request, metadata, decision));
        ex.Message.ShouldContain("KEYSET_STREAMING_UNAVAILABLE");
        ex.Message.ShouldContain("masked or denied");
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_08: Bounded Memory Working Set Direct To PipeWriter
    // -----------------------------------------------------------------------------------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA178_08_ZeroBufferStreaming_DirectPipeWriter_BackpressureHaltsReader()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = CreateReadOnlyPrincipal();

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        context.RequestServices = services.BuildServiceProvider();

        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();

        const int totalRows = 300;
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(totalRows);
                await writer(reader, CancellationToken.None);
            });

        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Rows-Streamed"].ToString().ShouldBe(totalRows.ToString());
    }

    // -----------------------------------------------------------------------------------------
    // T_SEC_DATA178_09: Quota Exhaustion Mid-Stream Halts Cleanly & Settles Actual Rows
    // -----------------------------------------------------------------------------------------
    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_DATA178_09_QuotaExhaustion_TruncatesStreamAndSettlesActualRows()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = CreateReadOnlyPrincipal();

        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(true, 5000, 0)));

        // Flag quota exhaustion during active streaming
        context.Items["QuotaExhausted"] = true;

        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var actualCost = callInfo.ArgAt<int>(2);
                return Task.FromResult(new CostQuotaResult(true, 5000 - actualCost, 0));
            });

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        services.AddSingleton(rateLimiter);
        context.RequestServices = services.BuildServiceProvider();

        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();

        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(10);
                await writer(reader, CancellationToken.None);
            });

        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Stream-Truncated"].ToString().ShouldBe("quota_exhausted");
        await rateLimiter.Received(1).SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>());
    }
}

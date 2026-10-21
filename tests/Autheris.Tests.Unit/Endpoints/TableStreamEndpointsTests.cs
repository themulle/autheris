namespace Autheris.Tests.Unit.Endpoints;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Caching.Interfaces;
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

public sealed class TableStreamEndpointsTests
{
    private static DefaultHttpContext CreateHttpContext(ClaimsPrincipal? principal = null)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.User = principal ?? new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "test-user"),
                new Claim("tenant", "default"),
                new Claim("sid", "S-1-5-21-test")
            ], "Bearer"));

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private static TableMetadata CreateTableMetadata(string tableName = "orders", DatasetVisibility visibility = DatasetVisibility.Full)
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
            PrimaryKeyColumns = ["id"],
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "item", DataType = "varchar" },
                new TableColumn { ColumnName = "price", DataType = "numeric" }
            ]
        };
    }

    private static DbDataReader CreateDataTableReader(int rowCount)
    {
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(int));
        dt.Columns.Add("item", typeof(string));
        dt.Columns.Add("price", typeof(decimal));

        for (int i = 1; i <= rowCount; i++)
        {
            dt.Rows.Add(i, $"Item {i}", (decimal)(i * 10.5));
        }

        return dt.CreateDataReader();
    }

    [Fact]
    public async Task HandleBulkStreamAsync_TableNotFound_Returns404()
    {
        var context = CreateHttpContext();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "nonexistent",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_HiddenTableNonAdmin_Returns404AntiEnumeration()
    {
        var context = CreateHttpContext();
        var hiddenTable = CreateTableMetadata(visibility: DatasetVisibility.Hidden);
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(hiddenTable));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_UnauthorizedTable_Returns404AntiEnumeration()
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Denied(table.Identifier, "Forbidden")));

        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_OffsetQueryParam_RejectedWith400()
    {
        var context = CreateHttpContext();
        context.Request.QueryString = new QueryString("?offset=50");

        var repo = Substitute.For<ITableMetadataRepository>();
        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        var planner = new GovernedTableReadPlanner();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_Success_StreamsNdjsonRowsAndHeaders()
    {
        var context = CreateHttpContext();
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
                using var reader = CreateDataTableReader(5);
                await writer(reader, CancellationToken.None);
            });

        var encoder = new KeysetCursorEncoder();
        var masking = Substitute.For<IColumnMaskingProvider>();

        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.Headers["X-Rows-Streamed"].ToString().ShouldBe("5");
        context.Response.Headers["X-Has-More"].ToString().ShouldBe("false");
        context.Response.Headers["X-Stream-Truncated"].ToString().ShouldBe("false");

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(5);

        using var firstDoc = JsonDocument.Parse(lines[0]);
        firstDoc.RootElement.GetProperty("id").GetInt32().ShouldBe(1);
        firstDoc.RootElement.GetProperty("item").GetString().ShouldBe("Item 1");
    }

    private static ClaimsPrincipal CreatePrincipal(string sub, string tenant) =>
        new(new ClaimsIdentity([new Claim("sub", sub), new Claim("tenant", tenant)], "Bearer"));

    private static void SetJsonBody(DefaultHttpContext context, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
    }

    [Fact]
    public async Task HandleBulkStreamAsync_EndpointIssuedCursor_DecodesInPlanner_AndReplayFailsClosed()
    {
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        // One DI-style encoder (configured signing key) shared by the endpoint and the planner, as in production.
        var encoder = new KeysetCursorEncoder("endpoint-planner-shared-signing-key-0123456789");
        var planner = new GovernedTableReadPlanner(encoder);

        var captured = new List<GovernedSqlQueryRequest>();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                captured.Add(callInfo.Arg<GovernedSqlQueryRequest>());
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(5);
                await writer(reader, CancellationToken.None);
            });
        var masking = Substitute.For<IColumnMaskingProvider>();

        // Page 1: the endpoint issues nextCursor for the last emitted row (id = 2).
        var first = CreateHttpContext(CreatePrincipal("alice", "tenant-a"));
        SetJsonBody(first, "{\"limit\":2,\"select\":[\"id\",\"item\"]}");
        await TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            first, repo, pdp, planner, sqlExecution, encoder, masking,
            NullLoggerFactory.Instance, CancellationToken.None);

        first.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        first.Response.Headers["X-Has-More"].ToString().ShouldBe("true");
        var nextCursor = first.Response.Headers["X-Next-Cursor"].ToString();
        nextCursor.ShouldNotBeNullOrWhiteSpace();

        async Task<DefaultHttpContext> ContinueAsync(ClaimsPrincipal principal, string bodyJson, string tableName = "orders")
        {
            var ctx = CreateHttpContext(principal);
            SetJsonBody(ctx, bodyJson);
            await TableStreamEndpoints.HandleBulkStreamAsync(
                "sales", "dbo", tableName,
                ctx, repo, pdp, planner, sqlExecution, encoder, masking,
                NullLoggerFactory.Instance, CancellationToken.None);
            return ctx;
        }

        var cursorJson = JsonSerializer.Serialize(nextCursor);

        // Page 2: same tenant, caller, table, filter and projection -> the planner decodes the endpoint's cursor.
        var second = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        second.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        captured.Count.ShouldBe(2);
        captured[1].Parameters!["@cursor_0"].ShouldBe(2);

        // Other caller
        var otherCaller = await ContinueAsync(CreatePrincipal("mallory", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        otherCaller.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other tenant
        var otherTenant = await ContinueAsync(CreatePrincipal("alice", "tenant-b"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}");
        otherTenant.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other filter
        var otherFilter = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"filter\":{{\"field\":\"item\",\"op\":\"eq\",\"value\":\"x\"}},\"cursor\":{cursorJson}}}");
        otherFilter.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other projection
        var otherSelect = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"price\"],\"cursor\":{cursorJson}}}");
        otherSelect.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Other table (same key shape)
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(CreateTableMetadata("invoices")));
        var otherTable = await ContinueAsync(CreatePrincipal("alice", "tenant-a"), $"{{\"limit\":2,\"select\":[\"id\",\"item\"],\"cursor\":{cursorJson}}}", "invoices");
        otherTable.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // None of the rejected continuations reached the database.
        captured.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HandleBulkStreamAsync_ClientAbort_SettlesQuotaWithoutRequestToken()
    {
        var context = CreateHttpContext();
        var table = CreateTableMetadata();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(table));

        var pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
        pdp.EvaluateAccessAsync(Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<SecurityPrincipalContext>(), Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), null, true)));

        var rateLimiter = Substitute.For<IRateLimiterService>();
        rateLimiter.ReserveCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CostQuotaResult(true, 5000, 0)));
        rateLimiter.SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.ArgAt<CancellationToken>(4).ThrowIfCancellationRequested();
                return Task.FromResult(new CostQuotaResult(true, 4990, 0));
            });

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<GatewayOptions>>(Options.Create(new GatewayOptions()));
        services.AddSingleton(rateLimiter);
        context.RequestServices = services.BuildServiceProvider();

        using var aborted = new CancellationTokenSource();
        var sqlExecution = Substitute.For<IGovernedSqlExecutionService>();
        sqlExecution.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<DbDataReader, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                aborted.Cancel();
                return Task.FromException(new IOException("client aborted"));
            });

        // The original stream failure surfaces (not an OperationCanceledException from settlement) and the reservation is settled.
        await Should.ThrowAsync<IOException>(() => TableStreamEndpoints.HandleBulkStreamAsync(
            "sales", "dbo", "orders",
            context, repo, pdp, new GovernedTableReadPlanner(), sqlExecution, new KeysetCursorEncoder(), Substitute.For<IColumnMaskingProvider>(),
            NullLoggerFactory.Instance, aborted.Token));

        await rateLimiter.Received(1).SettleCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }
}

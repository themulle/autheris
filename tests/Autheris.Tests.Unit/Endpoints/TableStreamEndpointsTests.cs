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
}

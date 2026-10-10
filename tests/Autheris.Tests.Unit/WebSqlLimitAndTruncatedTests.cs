namespace Autheris.Tests.Unit;

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
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebSqlLimitAndTruncatedTests
{
    private static TableMetadata CreateTableMetadata(string tableName = "orders")
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", tableName),
            Table = new Table
            {
                TableName = tableName,
                SchemaName = "dbo",
                SourceName = "sales",
                SourceType = "PostgreSQL"
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "amount", DataType = "numeric" }
            ]
        };
    }

    private static GovernedSqlExecutionService CreateService(long defaultMaxRows = 100, long maxAllowedRows = 10000)
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "sales",
                AllowedDataSources = ["sales"],
                DefaultMaxRows = defaultMaxRows,
                MaxAllowedRows = maxAllowedRows
            }
        };

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(CreateTableMetadata()));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(Array.Empty<Consent>()));

        return new GovernedSqlExecutionService(
            Options.Create(options),
            policyEnforcement: null,
            consentResolution: resolution,
            tableRepository: tableRepo,
            connectionFactory: null,
            clientIpResolver: null,
            environment: null,
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consentRepo);
    }

    private static ClaimsPrincipal CreateUser()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-WEBSQL-USER"),
            new(ClaimTypes.Name, "alice"),
            new("tenant_id", "tenant-1")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    [Fact]
    public async Task RewriteSqlAsync_WithoutLimit_AppliesDefaultMaxRows()
    {
        var service = CreateService(defaultMaxRows: 100, maxAllowedRows: 10000);
        var user = CreateUser();

        var sql = await service.RewriteSqlAsync("SELECT id, amount FROM sales.dbo.orders", user, new TenantId("tenant-1"));

        sql.ShouldContain("LIMIT 100");
    }

    [Fact]
    public async Task RewriteSqlAsync_WithExplicitLimitUnderMaxAllowed_PreservesRequestedLimit()
    {
        var service = CreateService(defaultMaxRows: 100, maxAllowedRows: 10000);
        var user = CreateUser();

        var sql = await service.RewriteSqlAsync("SELECT id, amount FROM sales.dbo.orders LIMIT 5000", user, new TenantId("tenant-1"));

        // Should NOT be clamped to DefaultMaxRows (100)
        sql.ShouldContain("LIMIT 5000");
        sql.ShouldNotContain("LIMIT 100");
    }

    [Fact]
    public async Task RewriteSqlAsync_WithExplicitLimitAboveMaxAllowed_ClampsToMaxAllowedRows()
    {
        var service = CreateService(defaultMaxRows: 100, maxAllowedRows: 10000);
        var user = CreateUser();

        var sql = await service.RewriteSqlAsync("SELECT id, amount FROM sales.dbo.orders LIMIT 25000", user, new TenantId("tenant-1"));

        // Clamped to MaxAllowedRows (10000)
        sql.ShouldContain("LIMIT 10000");
        sql.ShouldNotContain("LIMIT 25000");
    }

    private static DbDataReader CreateDataTableReader(int rowCount)
    {
        var table = new DataTable();
        table.Columns.Add("id", typeof(int));
        table.Columns.Add("amount", typeof(decimal));
        for (int i = 0; i < rowCount; i++)
        {
            table.Rows.Add(i + 1, 42.5m);
        }
        return table.CreateDataReader();
    }

    [Fact]
    public async Task WebSqlEndpoint_WhenRowsHitLimit_EmitsTruncatedTrue()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT id, amount FROM sales.dbo.orders\"}"));
        context.Response.Body = new MemoryStream();
        context.User = CreateUser();

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 1000 }
        };

        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        sqlService.ExecuteGovernedQueryAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<Func<DbDataReader, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                // The governed service delivers the limit (100) and reads one probe row beyond it.
                using var reader = CreateDataTableReader(101);
                await writer(new RowLimitedDataReader(reader, 100), CancellationToken.None);
            });

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);

        json.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(100);
        json.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeTrue();
    }

    [Fact]
    public async Task WebSqlEndpoint_WhenRowsExactlyAtLimit_EmitsTruncatedFalse()
    {
        // WebSQL findings 2.4: a table with exactly the limit is complete.
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT id, amount FROM sales.dbo.orders\"}"));
        context.Response.Body = new MemoryStream();
        context.User = CreateUser();
        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 1000 } };

        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        sqlService.ExecuteGovernedQueryAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<Func<DbDataReader, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(100);
                await writer(new RowLimitedDataReader(reader, 100), CancellationToken.None);
            });

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);
        json.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(100);
        json.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeFalse();
    }

    [Fact]
    public async Task WebSqlEndpoint_WhenRowsLessThanLimit_EmitsTruncatedFalse()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"sql\":\"SELECT id, amount FROM sales.dbo.orders\"}"));
        context.Response.Body = new MemoryStream();
        context.User = CreateUser();

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultMaxRows = 100, MaxAllowedRows = 1000 }
        };

        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        sqlService.ExecuteGovernedQueryAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<Func<DbDataReader, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var writer = callInfo.Arg<Func<DbDataReader, CancellationToken, Task>>();
                using var reader = CreateDataTableReader(5);
                await writer(reader, CancellationToken.None);
            });

        await WebSqlEndpoints.HandleWebSqlRequest(context, sqlService, Options.Create(options), NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await JsonDocument.ParseAsync(context.Response.Body);

        json.RootElement.GetProperty("rowCount").GetInt32().ShouldBe(5);
        json.RootElement.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        context.Response.Headers.ContainsKey("X-Autheris-Truncated").ShouldBeFalse();
    }
}

namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TrinoSqlEngine;
using Xunit;

public sealed class WebSqlIndexHintTests
{
    private static TableMetadata CreateOrdersMetadata(bool indexCustomerId = false)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier("crm", "public", "orders"),
            Table = new Table
            {
                TableName = "orders",
                SchemaName = "public",
                SourceName = "crm",
                SourceType = "PostgreSQL",
                IsActive = true,
                DataSourceType = DataSourceType.Sql
            },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "customer_id", DataType = "int" },
                new TableColumn { ColumnName = "secret_col", DataType = "varchar" },
                new TableColumn { ColumnName = "total", DataType = "decimal" }
            ],
            PrimaryKeyColumns = ["id"],
            Indexes = indexCustomerId
                ? [new TableIndexDefinition("idx_orders_customer", ["customer_id"], false, null)]
                : []
        };
    }

    [Fact]
    public void Unindexed_group_by_surcharged_and_hinted()
    {
        const string sql = "SELECT customer_id, count(*) FROM orders GROUP BY customer_id";
        var metadata = FastSqlEngine.Default.Analyze(sql.AsMemory());

        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 };
        var analysisOptions = new QueryAnalysisOptions();
        var webSqlCost = new WebSqlCostOptions();
        var rowLimit = new SqlRowLimit(1000, 10000);

        // 1. Without metadata: base cost calculation
        int baseCost = SqlStatementCostEstimator.EstimateCost(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCost);

        // 2. With unindexed metadata: cost is surcharged by multiplier 2.0
        var (surchargedCost, hints) = SqlStatementCostEstimator.EstimateCostWithHints(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCost,
            getTableMetadata: name => ordersMeta);

        surchargedCost.Should().Be((int)Math.Ceiling(baseCost * 2.0));
        hints.Should().HaveCount(1);
        hints[0].Table.Should().Be("orders");
        hints[0].Reason.Should().Be("NO_USABLE_INDEX");
        hints[0].HelpfulIndexColumns.Should().ContainSingle().Which.Should().Be("customer_id");

        // 3. With indexed metadata: no surcharge, no hints
        var indexedOrdersMeta = CreateOrdersMetadata(indexCustomerId: true);
        var (indexedCost, indexedHints) = SqlStatementCostEstimator.EstimateCostWithHints(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCost,
            getTableMetadata: name => indexedOrdersMeta);

        indexedCost.Should().Be(baseCost);
        Assert.Empty(indexedHints);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Hint_never_contains_statement_text()
    {
        const string statementText = "SELECT customer_id, count(*) FROM orders WHERE secret_col = 'TOP_SECRET_VALUE_9999' GROUP BY customer_id";
        var metadata = FastSqlEngine.Default.Analyze(statementText.AsMemory());

        var ordersMeta = CreateOrdersMetadata(indexCustomerId: false);
        var costOptions = new QueryCostOptions { UnindexedRootListSurchargeMultiplier = 2.0 };
        var analysisOptions = new QueryAnalysisOptions();
        var webSqlCost = new WebSqlCostOptions();
        var rowLimit = new SqlRowLimit(1000, 10000);

        var (cost, hints) = SqlStatementCostEstimator.EstimateCostWithHints(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCost,
            getTableMetadata: name => ordersMeta);

        Assert.NotEmpty(hints);

        var json = JsonSerializer.Serialize(hints);

        // The SQL statement text is NEVER echoed in indexHints
        json.Should().NotContain("TOP_SECRET_VALUE_9999");
        json.Should().NotContain("SELECT");
        json.Should().NotContain("WHERE");
        json.Should().NotContain("GROUP BY");
        json.Should().NotContain(statementText);

        foreach (var hint in hints)
        {
            hint.Table.Should().NotContain(statementText);
            hint.Reason.Should().Be("NO_USABLE_INDEX");
            foreach (var col in hint.HelpfulIndexColumns)
            {
                col.Should().NotContain("TOP_SECRET_VALUE_9999");
                col.Should().NotContain("SELECT");
            }
        }

        // Columns should only be listed when they are not denied
        var (_, hintsWithDenial) = SqlStatementCostEstimator.EstimateCostWithHints(
            metadata,
            rowLimit,
            costOptions,
            analysisOptions,
            webSqlCost,
            getTableMetadata: name => ordersMeta,
            getDeniedColumns: name => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "secret_col" });

        hintsWithDenial.Should().HaveCount(1);
        hintsWithDenial[0].HelpfulIndexColumns.Should().NotContain("secret_col");
    }

    [Fact]
    public async Task WebSql_Endpoint_Emits_IndexHints_In_Response()
    {
        const string sql = "SELECT customer_id, count(*) FROM orders GROUP BY customer_id";

        var sqlService = Substitute.For<IGovernedSqlExecutionService>();
        var capturedHints = new List<WebSqlIndexHint>
        {
            new("orders", "NO_USABLE_INDEX", ["customer_id"])
        };

        sqlService.ExecuteGovernedQueryAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<Func<System.Data.Common.DbDataReader, GovernedResultInfo, CancellationToken, Task>>(),
            Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var req = ci.Arg<GovernedSqlQueryRequest>();
                req.OnIndexHints?.Invoke(capturedHints);

                var writer = ci.Arg<Func<System.Data.Common.DbDataReader, GovernedResultInfo, CancellationToken, Task>>();
                var mockReader = Substitute.For<System.Data.Common.DbDataReader>();
                mockReader.FieldCount.Returns(0);
                mockReader.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

                var info = new GovernedResultInfo([], System.Collections.Immutable.ImmutableArray<bool>.Empty, capturedHints);
                await writer(mockReader, info, CancellationToken.None);
            });

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "crm" }
        });

        var context = new DefaultHttpContext();
        context.Items[Autheris.Domain.Security.SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-1");
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "user-1"),
            new Claim("sub", "user-1"),
            new Claim("tenant_id", "tenant-1")
        ], "TestAuth"));
        context.Request.ContentType = "application/json";
        var requestBody = JsonSerializer.Serialize(new WebSqlEndpoints.WebSqlRequestDto(sql, null, "crm"));
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody));
        context.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleWebSqlRequest(
            context,
            sqlService,
            gatewayOptions,
            NullLoggerFactory.Instance);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseJson = await reader.ReadToEndAsync();

        responseJson.Should().Contain("\"indexHints\"");
        responseJson.Should().Contain("\"NO_USABLE_INDEX\"");
        responseJson.Should().Contain("\"customer_id\"");
    }
}

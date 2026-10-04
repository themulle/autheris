namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.SqlEndpoints.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class SqlEndpointsTests
{
    private const string UserCustomerRevenueSql = @"
    -- @name: getCustomerRevenue
    -- @summary: Ermittelt den Umsatz nach Land und Mindestbetrag
    SELECT
        c.id,
        c.company_name,
        c.email,
        SUM(o.amount) AS total_revenue
    FROM customers c
    INNER JOIN orders o ON c.id = o.customer_id
    WHERE c.country = @country
      AND o.order_date >= @fromDate
    GROUP BY c.id, c.company_name, c.email
    HAVING SUM(o.amount) >= @minRevenue;";

    [Fact]
    public void SqlEndpointLoader_ParsesUserQueryAndCommentsAccurately()
    {
        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var endpoint = loader.ParseSqlContent(UserCustomerRevenueSql, "defaultName");

        endpoint.ShouldNotBeNull();
        endpoint.Name.ShouldBe("getCustomerRevenue");
        endpoint.Summary.ShouldBe("Ermittelt den Umsatz nach Land und Mindestbetrag");

        // Parameters extracted
        endpoint.Parameters.Count.ShouldBe(3);

        var countryParam = endpoint.Parameters.FirstOrDefault(p => p.Name == "country");
        countryParam.ShouldNotBeNull();
        countryParam.Token.ShouldBe("@country");
        countryParam.TargetColumn.ShouldBe("country");
        countryParam.ComparisonOperator.ShouldBe("=");

        var fromDateParam = endpoint.Parameters.FirstOrDefault(p => p.Name == "fromDate");
        fromDateParam.ShouldNotBeNull();
        fromDateParam.Token.ShouldBe("@fromDate");
        fromDateParam.TargetColumn.ShouldBe("order_date");
        fromDateParam.ComparisonOperator.ShouldBe(">=");

        var minRevenueParam = endpoint.Parameters.FirstOrDefault(p => p.Name == "minRevenue");
        minRevenueParam.ShouldNotBeNull();
        minRevenueParam.Token.ShouldBe("@minRevenue");

        // Projections
        endpoint.Projections.Count.ShouldBe(4);
        endpoint.Projections[0].ColumnName.ShouldBe("id");
        endpoint.Projections[1].ColumnName.ShouldBe("company_name");
        endpoint.Projections[2].ColumnName.ShouldBe("email");
        endpoint.Projections[3].ColumnName.ShouldBe("total_revenue");

        // Referenced tables
        endpoint.ReferencedTables.ShouldContain("customers");
        endpoint.ReferencedTables.ShouldContain("orders");
    }

    [Fact]
    public void SqlEndpointLoader_ExtractsLeadingCommentsWhenSummaryAnnotationOmitted()
    {
        const string sqlWithDocComment = @"
        -- Liefert eine Liste aller aktiven Produkte im Katalog.
        -- Sortiert nach Aktualisierungsdatum.
        SELECT p.id, p.name, p.price
        FROM products p
        WHERE p.is_active = @isActive;";

        var registry = new InMemorySqlEndpointRegistry();
        using var loader = new SqlEndpointLoader(registry);

        var endpoint = loader.ParseSqlContent(sqlWithDocComment, "active_products");

        endpoint.ShouldNotBeNull();
        endpoint.Name.ShouldBe("active_products");
        endpoint.Summary.ShouldContain("Liefert eine Liste aller aktiven Produkte im Katalog.");
        endpoint.Parameters.Count.ShouldBe(1);
        endpoint.Parameters[0].Name.ShouldBe("isActive");
    }

    [Fact]
    public void SqlEndpointLoader_OptionAAndOptionB_SyncAndLoadCycle()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "gql_queries_" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new InMemorySqlEndpointRegistry();
            using var loader = new SqlEndpointLoader(registry);

            // Option B: dbt auto-sync writes model to directory
            string writtenPath = loader.SyncDbtModelToFile(
                directoryPath: tempDir,
                name: "dbt_marts_monthly_sales",
                sql: "SELECT month, total_sales FROM analytics.monthly_sales WHERE year = @year",
                summary: "Monatlicher aggregierter Umsatz",
                dataSource: "analytics_db",
                parameters: [new SqlEndpointParameter("year", "@year", typeof(int), true)]);

            File.Exists(writtenPath).ShouldBeTrue();

            // Option A: Loader reads from directory
            int loaded = loader.LoadFromDirectory(tempDir, enableHotReload: false);
            loaded.ShouldBe(1);

            registry.TryGet("dbt_marts_monthly_sales", out var def).ShouldBeTrue();
            def.ShouldNotBeNull();
            def.Summary.ShouldBe("Monatlicher aggregierter Umsatz");
            def.DataSource.ShouldBe("analytics_db");
            def.Parameters.Count.ShouldBe(1);
            def.Parameters[0].Name.ShouldBe("year");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task SqlEndpointExecutionService_ValidatesAndCoercesParameters()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition(
            Name: "getCustomerRevenue",
            Summary: "Umsatzermittlung",
            RawSql: UserCustomerRevenueSql,
            Parameters:
            [
                new SqlEndpointParameter("country", "@country", typeof(string), IsRequired: true),
                new SqlEndpointParameter("fromDate", "@fromDate", typeof(DateTimeOffset), IsRequired: true),
                new SqlEndpointParameter("minRevenue", "@minRevenue", typeof(decimal), IsRequired: false, DefaultValue: 100m)
            ]));

        GovernedSqlQueryRequest? capturedRequest = null;
        var fakeSqlService = new FakeGovernedSqlExecutionService(req => capturedRequest = req);

        var options = Options.Create(new GatewayOptions());
        var executionService = new SqlEndpointExecutionService(registry, fakeSqlService, options);

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "test-user")]));
        var tenantId = new TenantId("tenant_123");

        // Call with string inputs (as received from HTTP query or JSON body)
        var rawInputs = new Dictionary<string, object?>
        {
            ["country"] = "DE",
            ["fromDate"] = "2026-01-01T00:00:00Z"
            // minRevenue omitted -> should take default 100m
        };

        var result = await executionService.ExecuteEndpointAsync("getCustomerRevenue", rawInputs, user, tenantId, CancellationToken.None);

        result.ShouldNotBeNull();
        capturedRequest.ShouldNotBeNull();
        capturedRequest.Parameters.ShouldNotBeNull();

        capturedRequest.Parameters["country"].ShouldBe("DE");
        capturedRequest.Parameters["fromDate"].ShouldBe(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        capturedRequest.Parameters["minRevenue"].ShouldBe(100m);
    }

    [Fact]
    public async Task SqlEndpointExecutionService_ThrowsOnMissingRequiredParameter()
    {
        var registry = new InMemorySqlEndpointRegistry();
        registry.Register(new SqlEndpointDefinition(
            Name: "test_query",
            Summary: "Test",
            RawSql: "SELECT 1 WHERE 1 = @req",
            Parameters: [new SqlEndpointParameter("req", "@req", typeof(string), IsRequired: true)]));

        var fakeSqlService = new FakeGovernedSqlExecutionService(_ => { });
        var options = Options.Create(new GatewayOptions());
        var executionService = new SqlEndpointExecutionService(registry, fakeSqlService, options);

        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var tenantId = new TenantId("default");

        await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await executionService.ExecuteEndpointAsync("test_query", new Dictionary<string, object?>(), user, tenantId);
        });
    }

    private sealed class FakeGovernedSqlExecutionService(Action<GovernedSqlQueryRequest> onRequest) : IGovernedSqlExecutionService
    {
        public Task<string> RewriteSqlAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
        {
            return Task.FromResult(rawSql);
        }

        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<System.Data.Common.DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default)
        {
            onRequest(request);
            return Task.CompletedTask;
        }

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
        {
            onRequest(request);
            return Task.FromResult(new GovernedSqlResult(
                OriginalSql: request.Sql,
                RewrittenSql: request.Sql,
                Columns: ["id", "company_name", "total_revenue"],
                Rows: [new Dictionary<string, object?> { ["id"] = 1, ["company_name"] = "Acme", ["total_revenue"] = 500m }],
                RowCount: 1,
                ElapsedMilliseconds: 1));
        }
    }
}

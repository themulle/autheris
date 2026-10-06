using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>
/// Correlated row filters (EXISTS ... WHERE dep.pk = target.fk) bind to the reserved target alias that the query
/// builders put on the filtered table.
/// </summary>
public sealed class CorrelatedRowFilterAliasTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"autheris-corr-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static ConsentRowFilter CustomerCountryFilter(
        string? targetAlias = null,
        string depAlias = "c",
        string? targetTemporalColumn = null,
        string? dependentValidFromColumn = null,
        IReadOnlyList<SubqueryJoinHop>? hops = null) => new()
    {
        FilterType = RowFilterType.SubqueryCorrelated,
        TargetTableAlias = targetAlias,
        DependentTable = new TableIdentifier("erp", "main", "customers"),
        DependentTableAlias = depAlias,
        ForeignKeyColumn = "customer_id",
        PrimaryKeyColumn = "id",
        SubqueryFilterPredicateJson = $"{{\"{depAlias}.country\": \"CH\"}}",
        TargetTemporalColumn = targetTemporalColumn,
        DependentValidFromColumn = dependentValidFromColumn,
        AdditionalHops = hops
    };

    [Fact]
    public void Generator_AlwaysReferencesReservedTargetAlias()
    {
        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(CustomerCountryFilter(), DatabaseDialect.Sqlite);

        sql.ShouldBe("EXISTS (SELECT 1 FROM \"customers\" AS \"c\" WHERE \"c\".\"id\" = \"autheris_target\".\"customer_id\" AND \"c\".\"country\" = 'CH')");
    }

    [Fact]
    public void Generator_RebasesLegacyTargetQualifierOntoReservedAlias()
    {
        var filter = CustomerCountryFilter(targetAlias: "i", targetTemporalColumn: "i.invoice_date", dependentValidFromColumn: "c.valid_from");

        var sql = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.SqlServer);

        sql.ShouldContain("[autheris_target].[invoice_date] >= [c].[valid_from]");
        sql.ShouldNotContain("[i].");
    }

    [Theory]
    [InlineData("target", null)]           // dependent alias equals the default target alias
    [InlineData("i", "i")]                 // dependent alias equals the configured target alias
    [InlineData("autheris_target", null)]  // dependent alias equals the reserved alias
    public void Generator_RejectsDependentAliasCollidingWithTarget(string depAlias, string? targetAlias)
    {
        var filter = CustomerCountryFilter(targetAlias: targetAlias, depAlias: depAlias);

        Should.Throw<InvalidOperationException>(() => AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.Sqlite));
    }

    [Fact]
    public void Generator_RejectsDuplicateHopAlias()
    {
        var filter = CustomerCountryFilter(hops:
        [
            new SubqueryJoinHop { Table = new TableIdentifier("erp", "main", "assets"), TableAlias = "c", LeftJoinColumn = "c.id", RightJoinColumn = "c.customer_id" }
        ]);

        Should.Throw<InvalidOperationException>(() => AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(filter, DatabaseDialect.Sqlite));
    }

    [Fact]
    public async Task SqlDataSourceExecutor_AppliesCorrelatedFilter_AgainstRealSqlite()
    {
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE customers (id INTEGER PRIMARY KEY, country TEXT NOT NULL);
CREATE TABLE invoices (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL, amount INTEGER NOT NULL);
INSERT INTO customers VALUES (1, 'CH'), (2, 'DE');
INSERT INTO invoices VALUES (10, 1, 100), (11, 2, 200), (12, 1, 300);";
            cmd.ExecuteNonQuery();
        }

        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("default", "main", "invoices"),
            Table = new Table { SourceName = "erp", SchemaName = "main", TableName = "invoices", DataSourceType = DataSourceType.Sql, SourceType = "Sqlite" },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "customer_id", DataType = "integer" },
                new TableColumn { ColumnName = "amount", DataType = "integer" }
            ]
        };

        var rowFilter = AdvancedRlsFilterGenerator.BuildCorrelatedSubquery(CustomerCountryFilter(), DatabaseDialect.Sqlite);
        var columnAccess = new Dictionary<string, ColumnAccessLevel>
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["customer_id"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Clear
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TEST")], "Test"));
        var context = new DataSourceExecutionContext(
            SourceName: "erp",
            Metadata: metadata,
            Principal: principal,
            AccessDecision: TableAccessDecision.Allowed(metadata.Identifier, columnAccess, rowFilterSql: rowFilter),
            Arguments: new Dictionary<string, object?>(),
            RequestedFields: ["id", "amount"]);

        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" }
                }
            }
        });

        var executor = new SqlDataSourceExecutor(new SqlConnectionFactory(), options, NullLogger<SqlDataSourceExecutor>.Instance);
        var rows = await executor.ExecuteAsync(context);

        rows.Select(r => Convert.ToInt64(r["id"])).OrderBy(x => x).ShouldBe(new long[] { 10, 12 });
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id = 1")]
    public void RlsListener_RejectsCorrelatedFilterOnDml(string dml)
    {
        var correlated = $"EXISTS (SELECT 1 FROM customers AS c WHERE c.id = {RowFilterAliases.Target}.customer_id)";
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider(correlated),
            EnforceReadOnlyQueries = false
        };

        Should.Throw<SecurityException>(() => new FastSqlEngine().RewriteRls(dml.AsMemory(), options));
    }

    [Fact]
    public void RlsListener_AliasesFilteredTableOnlyForCorrelatedFilters()
    {
        var engine = new FastSqlEngine();
        var correlated = $"EXISTS (SELECT 1 FROM customers AS c WHERE c.id = {RowFilterAliases.Target}.customer_id)";

        engine.RewriteRls("SELECT id FROM orders".AsMemory(), new RlsOptions { PolicyProvider = new DefaultRlsPolicyProvider(correlated) })
            .ShouldContain($"FROM orders AS {RowFilterAliases.Target} WHERE {correlated}");
        engine.RewriteRls("SELECT id FROM orders".AsMemory(), new RlsOptions { PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 1") })
            .ShouldContain("FROM orders WHERE tenant_id = 1");
    }
}

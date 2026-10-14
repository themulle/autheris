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
using Autheris.Tests.Unit.Sql;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
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

    private static ColumnReference Col(params string[] parts) =>
        new(new SqlQualifiedName(parts.Select(p => new SqlIdentifier(p, false)).ToList()));

    /// <summary>EXISTS (SELECT 1 FROM customers c WHERE c.id = autheris_target.customer_id): a correlated row policy.</summary>
    private static PolicyPredicate CorrelatedPolicy() => PolicyPredicate.Create(
        new ExistsExpression(new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new LiteralExpression(1L, LiteralType.Integer), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("customers", false) }), new SqlIdentifier("c", false)),
                new BinaryExpression(Col("c", "id"), BinaryOperator.Equal, Col(RowFilterAliases.Target, "customer_id")),
                null, null), null, null)),
        new Dictionary<string, PolicyValue>());

    private static PolicyPredicate AmountPolicy() => PolicyPredicate.Create(
        new BinaryExpression(Col("amount"), BinaryOperator.Equal, new PolicyParameterExpression("__pol_amount", SqlParameterType.Int64)),
        new Dictionary<string, PolicyValue> { ["__pol_amount"] = new(1L, SqlParameterType.Int64) });

    private static TestCompileEnv Env(PolicyPredicate policy) => new TestCompileEnv(TargetSqlDialect.Sqlite)
        .Table("orders", "tenant_id", "id:INTEGER", "customer_id:INTEGER", "amount:INTEGER", "tenant_id")
        .Table("customers", "tenant_id", "id:INTEGER", "country", "tenant_id")
        .Policy("orders", policy);

    // The legacy RlsListener tests (RlsListener_RejectsCorrelatedFilterOnDml, ..._AliasesFilteredTableOnlyForCorrelatedFilters) are
    // ported to the typed compiler path: the same properties, asserted on Compile.

    [Theory]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("UPDATE orders SET amount = 0 WHERE id = 1")]
    public void Compile_RejectsCorrelatedFilterOnDml(string dml)
    {
        var env = Env(CorrelatedPolicy());

        Should.Throw<SecurityException>(() => env.Compile(dml, statements: StatementPermissions.Update | StatementPermissions.Delete));
    }

    [Fact]
    public void Compile_AliasesFilteredTableOnlyForCorrelatedFilters()
    {
        Env(CorrelatedPolicy()).Compile("SELECT id FROM orders").Sql.ShouldContain(RowFilterAliases.Target);
        Env(AmountPolicy()).Compile("SELECT id FROM orders").Sql.ShouldNotContain(RowFilterAliases.Target);
    }
}

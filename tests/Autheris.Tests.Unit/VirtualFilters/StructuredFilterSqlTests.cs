namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 4: SQL of a structured filter for one object, per dialect, over the alias
/// <c>autheris_target</c>; and its effect on real SQLite data.
/// </summary>
public sealed class StructuredFilterSqlTests : IDisposable
{
    private static readonly VirtualFilter David = VirtualFilterModelTests.DavidFilter();
    private static readonly FilterBinding Binding = new() { FilterName = David.Name };

    private static TableMetadata Air1(DatabaseDialect dialect, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", "fms", "air1"),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = "fms", TableName = "air1", SourceType = dialect.ToString() },
        Columns = (columns.Length == 0 ? ["id", "client_id", "ts"] : columns).Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static string Build(RowFilterSubqueryStrategy strategy, DatabaseDialect dialect, VirtualFilter? filter = null, FilterBinding? binding = null) =>
        new StructuredFilterSqlBuilder(Options.Create(new GatewayOptions { RowFilters = new RowFilterOptions { SubqueryStrategy = strategy } }))
            .Build(filter ?? David, binding ?? Binding, Air1(dialect), dialect);

    private const string DavidSubquery =
        "FROM [conf].[client] AS [client] INNER JOIN [md].[crane] AS [crane] ON [crane].[serial_number] = [client].[crane_serial_number] WHERE [crane].[is_delivered] IS NULL";

    [Fact]
    public void SqlServer_InCorrelated()
    {
        Build(RowFilterSubqueryStrategy.InCorrelated, DatabaseDialect.SqlServer).ShouldBe(
            $"[autheris_target].[client_id] IN (SELECT [client].[client_id] {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id])");
    }

    [Fact]
    public void SqlServer_In_WithoutCorrelation()
    {
        Build(RowFilterSubqueryStrategy.In, DatabaseDialect.SqlServer).ShouldBe(
            $"[autheris_target].[client_id] IN (SELECT [client].[client_id] {DavidSubquery})");
    }

    [Fact]
    public void SqlServer_Exists()
    {
        Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.SqlServer).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id])");
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "EXISTS (SELECT 1 FROM \"conf\".\"client\" AS \"client\" INNER JOIN \"md\".\"crane\" AS \"crane\" ON \"crane\".\"serial_number\" = \"client\".\"crane_serial_number\" WHERE \"crane\".\"is_delivered\" IS NULL AND \"client\".\"client_id\" = \"autheris_target\".\"client_id\")")]
    [InlineData(DatabaseDialect.Sqlite, "EXISTS (SELECT 1 FROM \"client\" AS \"client\" INNER JOIN \"crane\" AS \"crane\" ON \"crane\".\"serial_number\" = \"client\".\"crane_serial_number\" WHERE \"crane\".\"is_delivered\" IS NULL AND \"client\".\"client_id\" = \"autheris_target\".\"client_id\")")]
    public void OtherDialects_UseExists_EvenWhenInIsConfigured(DatabaseDialect dialect, string expected)
    {
        Build(RowFilterSubqueryStrategy.InCorrelated, dialect).ShouldBe(expected);
    }

    [Fact]
    public void SeveralKeyColumns_AlwaysUseExists()
    {
        var twoKeys = David with { KeyColumns = ["client.client_id", "crane.ts"] };

        Build(RowFilterSubqueryStrategy.In, DatabaseDialect.SqlServer, twoKeys).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id] AND [crane].[ts] = [autheris_target].[ts])");
    }

    [Fact]
    public void ValidityWindow_ComparesTheBindingsTimeColumn()
    {
        var window = David with { ValidToColumn = "crane.date_of_delivery" };
        var binding = Binding with { TimeColumn = "ts" };

        Build(RowFilterSubqueryStrategy.In, DatabaseDialect.SqlServer, window, binding).ShouldBe(
            $"EXISTS (SELECT 1 {DavidSubquery} AND [client].[client_id] = [autheris_target].[client_id] AND ([crane].[date_of_delivery] IS NULL OR [autheris_target].[ts] < [crane].[date_of_delivery]))");

        var both = window with { ValidFromColumn = "crane.date_of_order" };
        Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.SqlServer, both, binding).ShouldEndWith(
            "AND ([crane].[date_of_order] IS NULL OR [autheris_target].[ts] >= [crane].[date_of_order]) AND ([crane].[date_of_delivery] IS NULL OR [autheris_target].[ts] < [crane].[date_of_delivery]))");
    }

    [Fact]
    public void ColumnMap_CorrelatesWithTheMappedTargetColumn()
    {
        var mapped = Binding with { ColumnMap = new Dictionary<string, string> { ["client_id"] = "cid" } };

        Build(RowFilterSubqueryStrategy.InCorrelated, DatabaseDialect.SqlServer, binding: mapped).ShouldBe(
            $"[autheris_target].[cid] IN (SELECT [client].[client_id] {DavidSubquery} AND [client].[client_id] = [autheris_target].[cid])");
    }

    [Fact]
    public void Conditions_FormatLiteralsSafely()
    {
        var conditions = David with
        {
            Structured = David.Structured! with
            {
                Where =
                [
                    new FilterCondition("crane.status", FilterConditionOperator.Eq, "O'Brien"),
                    new FilterCondition("crane.model", FilterConditionOperator.NotEq, "LTM 1300"),
                    new FilterCondition("crane.weight", FilterConditionOperator.Eq, "42"),
                    new FilterCondition("crane.sold_at", FilterConditionOperator.IsNotNull)
                ]
            }
        };

        Build(RowFilterSubqueryStrategy.In, DatabaseDialect.SqlServer, conditions).ShouldContain(
            "WHERE [crane].[status] = 'O''Brien' AND [crane].[model] <> 'LTM 1300' AND [crane].[weight] = 42 AND [crane].[sold_at] IS NOT NULL");
    }

    [Fact]
    public void GeneratedSql_PassesThePredicateValidator()
    {
        var sql = Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.SqlServer, David with { ValidToColumn = "crane.date_of_delivery" }, Binding with { TimeColumn = "ts" });

        Should.NotThrow(() => SqlSecurityValidator.ValidatePredicateSql(sql, "MandatoryRowPredicateSql"));
    }

    [Fact]
    public void MandatoryPredicates_MayExceed2000Characters_UpTo8000()
    {
        var predicate = string.Join(" AND ", Enumerable.Repeat($"({Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.SqlServer)})", 12));
        predicate.Length.ShouldBeInRange(2001, 8000);
        var decision = TableAccessDecision.Allowed(Air1(DatabaseDialect.SqlServer).Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        Should.Throw<ArgumentException>(() => SqlSecurityValidator.ValidateRowFilter(decision with { CombinedRowFilterSql = predicate }));
        Should.NotThrow(() => SqlSecurityValidator.ValidateRowFilter(decision.WithMandatoryPredicate(predicate, ["many"])));
    }

    // ------------------------------------------------------------------ real SQLite

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"autheris-vf-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task<IReadOnlyList<long>> ReadAir1IdsAsync(string predicate)
    {
        var metadata = Air1(DatabaseDialect.Sqlite) with
        {
            Identifier = new TableIdentifier("erp", "main", "air1"),
            Table = new Table { SourceName = "erp", SchemaName = "main", TableName = "air1", SourceType = "Sqlite", DataSourceType = DataSourceType.Sql }
        };
        var decision = TableAccessDecision.Allowed(metadata.Identifier,
                new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["client_id"] = ColumnAccessLevel.Clear, ["ts"] = ColumnAccessLevel.Clear })
            .WithMandatoryPredicate(predicate, ["nicht_ausgelieferte_krane"]);
        var executor = new SqlDataSourceExecutor(new SqlConnectionFactory(), Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions> { ["erp"] = new() { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" } }
            }
        }), NullLogger<SqlDataSourceExecutor>.Instance);

        var rows = await executor.ExecuteAsync(new DataSourceExecutionContext(
            "erp", metadata, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID")], "Test")),
            decision, new Dictionary<string, object?>(), ["id"], Limit: 100));
        return rows.Select(r => Convert.ToInt64(r["id"])).Order().ToList();
    }

    private void Seed()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE client (client_id INTEGER PRIMARY KEY, crane_serial_number TEXT);
CREATE TABLE crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER, date_of_delivery TEXT);
CREATE TABLE air1 (id INTEGER PRIMARY KEY, client_id INTEGER, ts TEXT);
INSERT INTO crane VALUES ('S1', 1, '2026-05-01 00:00:00'), ('S2', NULL, NULL), ('S3', NULL, '2026-06-01 00:00:00');
INSERT INTO client VALUES (1, 'S1'), (2, 'S2'), (3, 'S3'), (4, NULL);
INSERT INTO air1 VALUES
  (10, 1, '2026-04-01 00:00:00'), (11, 2, '2026-04-01 00:00:00'), (12, 3, '2026-05-31 23:59:00'),
  (13, 3, '2026-06-01 00:01:00'), (14, 4, '2026-04-01 00:00:00'), (15, NULL, '2026-04-01 00:00:00');";
        cmd.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(RowFilterSubqueryStrategy.Exists)]
    [InlineData(RowFilterSubqueryStrategy.InCorrelated)]
    public async Task Sqlite_OnlyRowsOfUndeliveredCranesAreVisible(RowFilterSubqueryStrategy strategy)
    {
        Seed();

        var ids = await ReadAir1IdsAsync(Build(strategy, DatabaseDialect.Sqlite));

        ids.ShouldBe([11L, 12L, 13L]);   // client 2 and 3 (undelivered); never client 1 (delivered), 4 (no crane) or NULL
    }

    [Fact]
    public async Task Sqlite_ValidityWindow_CutsAtTheDeliveryDate()
    {
        Seed();
        var window = David with { ValidToColumn = "crane.date_of_delivery" };

        var ids = await ReadAir1IdsAsync(Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.Sqlite, window, Binding with { TimeColumn = "ts" }));

        ids.ShouldBe([11L, 12L]);   // client 3: the row one minute after 2026-06-01 is cut off
    }

    [Fact]
    public void StructuredFilter_StringWithLeadingZero_IsQuotedAsStringLiteral()
    {
        var filter = new VirtualFilter
        {
            Name = "code_filter",
            TenantId = new TenantId("t1"),
            Source = "lwetem_prod",
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("lwetem_prod", "conf", "client"),
                FromAlias = "client",
                Where = [new FilterCondition("client.status_code", FilterConditionOperator.Eq, "01")]
            }
        };

        var sql = Build(RowFilterSubqueryStrategy.Exists, DatabaseDialect.SqlServer, filter);
        sql.ShouldContain("[client].[status_code] = '01'");
        sql.ShouldNotContain("[client].[status_code] = 1");
    }
}


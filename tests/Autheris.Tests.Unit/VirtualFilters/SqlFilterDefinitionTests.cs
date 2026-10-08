namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
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
/// Virtual filters, phase 7: a filter written as a correlated predicate in Trino syntax that addresses the protected
/// object as <c>target</c> (design 3.7). It is checked before it is stored (read only, target reserved, only tables of
/// the data source, allowed functions, translatable into the data source's dialect) and rendered per dialect with the
/// Trino date functions translated; time values are never baked into the SQL.
/// </summary>
public sealed class SqlFilterDefinitionTests : IDisposable
{
    private const string DavidLastDay = """
        from conf.client client
        join md.crane crane on crane.serial_number = client.crane_serial_number
        where crane.is_delivered is null
          and target.client_id = client.client_id
          and target.ts between date_add('day', -1, current_timestamp) and current_timestamp
        """;

    private static TableMetadata Catalogued(string schema, string table, DatabaseDialect dialect = DatabaseDialect.SqlServer) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = dialect.ToString() },
        Columns = [new TableColumn { ColumnName = "id" }]
    };

    private static readonly IReadOnlyList<TableMetadata> Catalog =
        [Catalogued("conf", "client"), Catalogued("md", "crane"), Catalogued("fms", "air1")];

    private static VirtualFilter SqlFilter(string sql, string name = "nicht_ausgelieferte_krane_letzter_tag") => new()
    {
        TenantId = new TenantId("tenant_lwe"),
        Name = name,
        Source = "lwetem_prod",
        Sql = sql
    };

    private static VirtualFilter Validate(string sql) => SqlFilterCompiler.Validate(SqlFilter(sql), Catalog);

    [Fact]
    public void DesignExample_IsAccepted_AndItsTargetColumnsAreDerived()
    {
        var validated = Validate(DavidLastDay);

        validated.SqlTargetColumns.ShouldBe(["client_id", "ts"], ignoreOrder: true);
        validated.TargetKeyColumns.ShouldBe(["client_id", "ts"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("from conf.client client where target.client_id = client.client_id; delete from conf.client", "statement")]
    [InlineData("select 1 from conf.client client where target.client_id = client.client_id", "from")]
    [InlineData("from conf.client target where target.client_id = 1", "target")]
    [InlineData("from conf.client client join md.crane target on target.serial_number = client.crane_serial_number where client.client_id = 1", "target")]
    [InlineData("from other_source.conf.client client where target.client_id = client.client_id", "data source")]
    [InlineData("from conf.unknown_table client where target.client_id = client.client_id", "unknown")]
    [InlineData("from conf.client client where target.client_id = client.client_id and random() < 0.5", "random")]
    [InlineData("from conf.client client where target.client_id = client.client_id and date_diff('day', target.ts, current_timestamp) < 2", "date_diff")]
    [InlineData("from conf.client client where target.client_id = ?", "parameter")]
    [InlineData("from conf.client client where client.client_id = 1", "target")]
    [InlineData("from conf.client client where target.client_id = client.client_id and client.name = 'target.x'", "target")]
    [InlineData("from conf.client client where target.client_id = client.client_id and target.ts > date_trunc('week', current_timestamp)", "SqlServer")]
    public void InvalidDefinitions_AreRejectedBeforeStoring(string sql, string reasonContains)
    {
        var ex = Should.Throw<ArgumentException>(() => Validate(sql));

        ex.Message.ShouldContain(reasonContains, Case.Insensitive);
    }

    [Fact]
    public void SqlFilter_CannotHaveStructuredParts()
    {
        Should.Throw<ArgumentException>(() => (SqlFilter(DavidLastDay) with { KeyColumns = ["client.client_id"] }).Validate());
        Should.Throw<ArgumentException>(() => (SqlFilter(DavidLastDay) with { Structured = VirtualFilterModelTests.DavidFilter().Structured }).Validate());
        Should.Throw<ArgumentException>(() => (VirtualFilterModelTests.DavidFilter() with { Sql = null, Structured = null }).Validate());
    }

    private static string Render(DatabaseDialect dialect, string sql = DavidLastDay)
    {
        var catalog = new List<TableMetadata> { Catalogued("conf", "client", dialect), Catalogued("md", "crane", dialect) };
        var filter = SqlFilterCompiler.Validate(SqlFilter(sql), catalog);
        return new StructuredFilterSqlBuilder().Build(filter, new FilterBinding { FilterName = filter.Name }, Catalogued("fms", "air1", dialect), dialect);
    }

    [Fact]
    public void SqlServer_TranslatesDatesAndCorrelatesWithTheReservedAlias()
    {
        var sql = Render(DatabaseDialect.SqlServer);

        sql.ShouldStartWith("EXISTS (SELECT 1 FROM [conf].[client] AS [client] INNER JOIN [md].[crane] AS [crane]");
        sql.ShouldContain("[autheris_target].[client_id] = [client].[client_id]");
        sql.ShouldContain("[autheris_target].[ts] BETWEEN DATEADD(day, -1, SYSDATETIMEOFFSET()) AND SYSDATETIMEOFFSET()");
        sql.ShouldNotContain("[target]");
        sql.ShouldNotContain("tenant_id");
    }

    [Fact]
    public void PostgreSql_TranslatesDates()
    {
        Render(DatabaseDialect.PostgreSql).ShouldContain("\"autheris_target\".\"ts\" BETWEEN (CURRENT_TIMESTAMP + (-1) * INTERVAL '1 day') AND CURRENT_TIMESTAMP");
    }

    [Fact]
    public void TimeValues_AreNeverBakedIntoTheSql()
    {
        var first = Render(DatabaseDialect.SqlServer);
        var second = Render(DatabaseDialect.SqlServer);

        second.ShouldBe(first);   // same text -> same plan cache entry, evaluated by the database at execution time
        Regex.IsMatch(first, @"\d{4}-\d{2}-\d{2}").ShouldBeFalse();
    }

    // ------------------------------------------------------------------ real SQLite

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"autheris-vf-sql-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Sqlite_LastDayWindow_IsEvaluatedByTheDatabase()
    {
        string Ts(TimeSpan ago) => DateTime.UtcNow.Subtract(ago).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE client (client_id INTEGER PRIMARY KEY, crane_serial_number TEXT);
                CREATE TABLE crane (serial_number TEXT PRIMARY KEY, is_delivered INTEGER);
                CREATE TABLE air1 (id INTEGER PRIMARY KEY, client_id INTEGER, ts TEXT);
                INSERT INTO crane VALUES ('S1', 1), ('S2', NULL);
                INSERT INTO client VALUES (1, 'S1'), (2, 'S2');
                INSERT INTO air1 VALUES (10, 1, '{Ts(TimeSpan.FromHours(2))}'), (11, 2, '{Ts(TimeSpan.FromHours(2))}'), (12, 2, '{Ts(TimeSpan.FromDays(2))}');
                """;
            cmd.ExecuteNonQuery();
        }

        const string sqliteDefinition = """
            from main.client client
            join main.crane crane on crane.serial_number = client.crane_serial_number
            where crane.is_delivered is null
              and target.client_id = client.client_id
              and target.ts between date_add('day', -1, current_timestamp) and current_timestamp
            """;
        var catalog = new List<TableMetadata>
        {
            Catalogued("main", "client", DatabaseDialect.Sqlite), Catalogued("main", "crane", DatabaseDialect.Sqlite)
        };
        var filter = SqlFilterCompiler.Validate(SqlFilter(sqliteDefinition), catalog);
        var air1 = Catalogued("main", "air1", DatabaseDialect.Sqlite) with
        {
            Identifier = new TableIdentifier("erp", "main", "air1"),
            Table = new Table { SourceName = "erp", SchemaName = "main", TableName = "air1", SourceType = "Sqlite", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "client_id" }, new TableColumn { ColumnName = "ts" }]
        };
        var predicate = new StructuredFilterSqlBuilder().Build(filter, new FilterBinding { FilterName = filter.Name }, air1, DatabaseDialect.Sqlite);
        var decision = TableAccessDecision.Allowed(air1.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear })
            .WithMandatoryPredicate(predicate, [filter.Name]);
        var executor = new SqlDataSourceExecutor(new SqlConnectionFactory(), Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions> { ["erp"] = new() { Provider = "Sqlite", ConnectionString = $"Data Source={_dbPath}" } }
            }
        }), NullLogger<SqlDataSourceExecutor>.Instance);

        var rows = await executor.ExecuteAsync(new DataSourceExecutionContext(
            "erp", air1, new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-LWE-DAVID")], "Test")),
            decision, new Dictionary<string, object?>(), ["id"], Limit: 100));

        rows.Select(r => Convert.ToInt64(r["id"])).ShouldBe([11L]);   // undelivered crane, within the last day
    }
}

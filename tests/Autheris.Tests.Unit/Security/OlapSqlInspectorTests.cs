namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// BC-OLAP-01..03: the client SQL is judged by DuckDB's own parser. Several expectations about the shape of
/// <c>json_serialize_sql</c> output are assumptions [verify]; each is a test so that a wrong assumption turns red.
/// </summary>
public sealed class OlapSqlInspectorTests
{
    private static DuckDbOlapEngine Engine() =>
        new(new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", QueryTimeoutSeconds = 10, MaxThreads = 1 }, NullLogger<DuckDbOlapEngine>.Instance);

    private static OlapTableSource Orders()
    {
        var id = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata { Identifier = id, Columns = [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "string" }] };
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [new Dictionary<string, object?> { ["id"] = 1, ["name"] = "a" }];
        return new OlapTableSource(id, rows, meta);
    }

    private static Task<OlapQueryResult> Execute(string sql) =>
        Engine().ExecuteOlapQueryAsync(new OlapQueryRequest(sql, [Orders()]));

    // ---- T3-01 / T3-02 / T3-03: escape strings, statement count, comments ----

    [Theory]
    [InlineData("SELECT E'\\'' ; CREATE MACRO m() AS 1")]
    [InlineData("SELECT E'\\''; SELECT 2")]
    [InlineData("SELECT e'\\'' ; ATTACH 'x.db'")]
    [InlineData("SELECT E'\\'' ; COPY orders TO 'x.csv'")]
    [InlineData("SELECT E'\\'' ; INSTALL httpfs")]
    [InlineData("SELECT E'\\'' ; LOAD httpfs")]
    [InlineData("SELECT E'\\'' ; SET threads=1")]
    [InlineData("SELECT E'\\'' ; PRAGMA database_list")]
    [InlineData("SELECT E'\\'' ; CALL pragma_version()")]
    [InlineData("SELECT U&'\\0041'")]
    [InlineData("SELECT 'a\\' ; SELECT 2")]
    public async Task EscapeStrings_AndHiddenSecondStatements_AreRejected(string sql)
    {
        await Assert.ThrowsAnyAsync<SecurityException>(() => Execute(sql));
    }

    [Theory]
    [InlineData("SELECT id FROM orders; SELECT 2")]
    [InlineData("SELECT id FROM orders; DROP TABLE orders")]
    [InlineData("SELECT id FROM orders /* /* */ ; DROP TABLE orders; */")]
    [InlineData("SELECT id FROM orders -- x\n; SELECT 3")]
    public async Task MultipleStatements_AreRejected(string sql)
    {
        await Assert.ThrowsAnyAsync<SecurityException>(() => Execute(sql));
    }

    [Fact]
    public void TheDuckDbParser_CountsStatements()
    {
        OlapSqlInspector.Inspect("SELECT 1").StatementCount.ShouldBe(1);
        OlapSqlInspector.Inspect("SELECT 1; SELECT 2").StatementCount.ShouldBe(2);
    }

    [Fact]
    public void NonSelectStatements_AreNotSerializable_AndRejected()
    {
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.Inspect("CREATE TABLE t (a INT)"));
    }

    [Fact]
    public void TextFloor_RejectsBackslashEscapeStringsNulAndLength()
    {
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.EnforceTextFloor("SELECT '\\'", 100));
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.EnforceTextFloor("SELECT E'x'", 100));
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.EnforceTextFloor("SELECT U&'x'", 100));
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.EnforceTextFloor("SELECT 1\0", 100));
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlInspector.EnforceTextFloor("SELECT 1 FROM t", 5));
        Should.NotThrow(() => OlapSqlInspector.EnforceTextFloor("SELECT DATE '2020-01-01', date'2020-01-02' FROM t", 100));
    }

    // ---- T3-04: blocklist and bounded functions ----

    [Theory]
    [InlineData("SELECT * FROM read_csv('/etc/passwd')")]
    [InlineData("SELECT query('SELECT 1')")]
    [InlineData("SELECT * FROM query_table('orders')")]
    [InlineData("SELECT json_execute_serialized_sql('{}')")]
    [InlineData("SELECT getvariable('x')")]
    [InlineData("SELECT \"read_csv\"('x')")]
    [InlineData("SELECT * FROM range(1000000000)")]
    [InlineData("SELECT repeat('x', 1000000000)")]
    public async Task BlockedFunctions_AreRejected(string sql)
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Execute(sql));
    }

    [Fact]
    public async Task NonSelectKeywords_AreRejected()
    {
        foreach (var sql in new[] { "ATTACH 'x.db'", "COPY orders TO 'x.csv'", "INSTALL httpfs", "LOAD httpfs", "SET threads=1", "PRAGMA database_list", "CALL pragma_version()", "CREATE MACRO m() AS 1" })
        {
            await Assert.ThrowsAnyAsync<SecurityException>(() => Execute(sql));
        }
    }

    // ---- T3-06: autoload is off ----

    [Fact]
    public void ExtensionAutoloadSettings_AreFalse_AfterTheSandboxSetup()
    {
        using var connection = new DuckDB.NET.Data.DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = DuckDbOlapEngine.BuildSetupSql(System.IO.Path.GetTempPath().Replace('\\', '/'), "256MB", "256MB", 1);
            setup.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT name, value FROM duckdb_settings() WHERE name IN ('autoload_known_extensions', 'autoinstall_known_extensions') ORDER BY name";
        using var reader = read.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(1));
        }

        values.Count.ShouldBe(2);
        values.ShouldAllBe(v => string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
    }

    // ---- inspection facts ([verify]: shape of the DuckDB AST) ----

    [Fact]
    public void Inspection_ReportsFunctionsTablesAndNoConstructs_ForAPlainJoin()
    {
        var i = OlapSqlInspector.Inspect("SELECT sum(a.id), upper(b.name) FROM orders a JOIN customers b ON a.id = b.id WHERE a.id > 1 GROUP BY b.name");

        i.Functions.ShouldContain("sum");
        i.Functions.ShouldContain("upper");
        i.BaseTables.OrderBy(x => x).ToArray().ShouldBe(new[] { "customers", "orders" });
        i.Constructs.ShouldBeEmpty();
        i.TableFunctions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SELECT * FROM a NATURAL JOIN b", OlapSqlInspector.ConstructNaturalJoin)]
    [InlineData("SELECT * FROM a POSITIONAL JOIN b", OlapSqlInspector.ConstructPositionalJoin)]
    [InlineData("SELECT * FROM a CROSS JOIN b", OlapSqlInspector.ConstructCrossJoin)]
    [InlineData("SELECT * FROM a, b", OlapSqlInspector.ConstructCrossJoin)]
    [InlineData("SELECT * FROM a JOIN b ON a.x > b.x", OlapSqlInspector.ConstructNonEquiJoin)]
    [InlineData("SELECT * FROM a JOIN b ON a.x = b.x OR a.y = b.y", OlapSqlInspector.ConstructNonEquiJoin)]
    [InlineData("SELECT COLUMNS(*) FROM a", OlapSqlInspector.ConstructColumns)]
    [InlineData("SELECT COLUMNS('^x') FROM a", OlapSqlInspector.ConstructColumns)]
    [InlineData("SELECT * EXCLUDE (x) FROM a", OlapSqlInspector.ConstructStarExclude)]
    [InlineData("SELECT * REPLACE (1 AS x) FROM a", OlapSqlInspector.ConstructStarReplace)]
    [InlineData("SELECT #1 FROM a", OlapSqlInspector.ConstructPositionalReference)]
    [InlineData("SELECT x, count(*) FROM a GROUP BY ALL", OlapSqlInspector.ConstructGroupByAll)]
    [InlineData("SELECT x FROM a ORDER BY ALL", OlapSqlInspector.ConstructStarOutsideSelectList)]
    [InlineData("SELECT x FROM a QUALIFY row_number() OVER () = 1", OlapSqlInspector.ConstructQualify)]
    [InlineData("SELECT x FROM a USING SAMPLE 10%", OlapSqlInspector.ConstructTableSample)]
    [InlineData("SELECT x FROM a TABLESAMPLE 10%", OlapSqlInspector.ConstructTableSample)]
    [InlineData("SELECT x FROM a, LATERAL (SELECT 1 WHERE a.x = 1) s", OlapSqlInspector.ConstructLateral)]
    public void Inspection_FlagsRiskyConstructs(string sql, string construct)
    {
        OlapSqlInspector.Inspect(sql).Constructs.ShouldContain(construct);
    }

    [Fact]
    public void Inspection_EqualityConjunctionJoin_IsNotNonEqui()
    {
        OlapSqlInspector.Inspect("SELECT * FROM a JOIN b ON a.x = b.x AND a.y = b.y").Constructs.ShouldBeEmpty();
    }

    [Fact]
    public void Inspection_TableFunctionsAndCteNames()
    {
        OlapSqlInspector.Inspect("SELECT * FROM range(3)").TableFunctions.ShouldContain("range");
        OlapSqlInspector.Inspect("WITH j AS (SELECT 1 AS x) SELECT x FROM j").BaseTables.ShouldBeEmpty();
    }

    // ---- policy ----

    [Fact]
    public void FunctionAllowlist_ContainsNoBlockedFunction()
    {
        OlapSqlPolicy.AllowedFunctions.Intersect(OlapSqlPolicy.BlockedFunctions, StringComparer.OrdinalIgnoreCase).ShouldBeEmpty();
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("columns");
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("query");
        OlapSqlPolicy.AllowedFunctions.ShouldNotContain("lpad");
    }

    [Theory]
    [InlineData("SELECT sum(x), coalesce(y, 0), lower(z) FROM a")]
    [InlineData("SELECT count(*) FROM a")]
    public void AllowlistedFunctions_PassTheCrossSourcePolicy(string sql)
    {
        var inspection = OlapSqlInspector.Inspect(sql);

        Should.NotThrow(() => OlapSqlPolicy.EnforceCrossSource(inspection, ["a"], allowNonEquiJoins: false));
    }

    [Theory]
    [InlineData("SELECT lpad('x', 10) FROM a")]
    [InlineData("SELECT list_value(1) FROM a")]
    [InlineData("SELECT query('SELECT 1') FROM a")]
    [InlineData("SELECT * FROM a, b")]
    [InlineData("SELECT * FROM unknown_table")]
    [InlineData("SELECT * FROM range(1)")]
    public void UnlistedFunctionsConstructsAndTables_AreRejectedByTheCrossSourcePolicy(string sql)
    {
        var inspection = OlapSqlInspector.Inspect(sql);

        Should.Throw<OlapSqlRejectedException>(() => OlapSqlPolicy.EnforceCrossSource(inspection, ["a"], allowNonEquiJoins: false));
    }

    [Fact]
    public void AllowNonEquiJoins_PermitsCrossAndRangeJoins_ButNothingElse()
    {
        var cross = OlapSqlInspector.Inspect("SELECT * FROM a, b");
        var natural = OlapSqlInspector.Inspect("SELECT * FROM a NATURAL JOIN b");

        Should.NotThrow(() => OlapSqlPolicy.EnforceCrossSource(cross, ["a", "b"], allowNonEquiJoins: true));
        Should.Throw<OlapSqlRejectedException>(() => OlapSqlPolicy.EnforceCrossSource(natural, ["a", "b"], allowNonEquiJoins: true));
    }

    // ---- masked column typing (T 24) ----

    [Fact]
    public async Task StagedMaskedBigintColumn_IsVarcharInDuckDb_AndQueryRuns()
    {
        var id = new TableIdentifier("hr", "public", "people");
        var meta = new TableMetadata { Identifier = id, Columns = [new TableColumn { ColumnName = "salary", DataType = "bigint" }] };
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = [new Dictionary<string, object?> { ["salary"] = "***" }];
        var source = new OlapTableSource(id, rows, meta, MaskedColumns: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "salary" });

        var result = await Engine().ExecuteOlapQueryAsync(new OlapQueryRequest("SELECT salary FROM people", [source]));

        result.Rows.Single()[0].ShouldBe("***");
    }

    [Fact]
    public void StagedNames_IncludeTableAndDomainView_AndCollideAfterSanitizing()
    {
        var a = new OlapTableSource(new TableIdentifier("sales", "public", "a-b"), [], new TableMetadata());
        var b = new OlapTableSource(new TableIdentifier("sales", "public", "a_b"), [], new TableMetadata());

        OlapStagedNames.For(a).ToArray().ShouldBe(new[] { "a_b", "sales_a_b" });
        OlapStagedNames.For(a).Intersect(OlapStagedNames.For(b), StringComparer.OrdinalIgnoreCase).ShouldNotBeEmpty();
    }
}

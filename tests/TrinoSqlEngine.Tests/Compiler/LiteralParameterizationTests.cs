using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.CompilerTestHelpers;

namespace TrinoSqlEngine.Tests.Compiler;

public class LiteralParameterizationTests
{
    [Fact]
    public void StringLiteral_BecomesParameter_WithRawValue()
    {
        var c = GenerateSqlServer("SELECT id FROM t WHERE name = 'O''Brien'");

        Assert.DoesNotContain('\'', c.Sql);
        var p = Assert.Single(c.Parameters);
        Assert.Equal("O'Brien", p.Value);
        Assert.Equal(SqlParameterType.String, p.Type);
        Assert.Equal(ParameterOrigin.QueryLiteral, p.Origin);
        Assert.Contains("[name] = @p0", c.Sql);
    }

    [Fact]
    public void NumericLiterals_BecomeTypedParameters()
    {
        var c = GenerateSqlServer("SELECT id FROM t WHERE a > 5 AND b < 9999999999 AND c = 1.50");

        Assert.Equal(3, c.Parameters.Length);
        Assert.Equal((SqlParameterType.Int32, 5), (c.Parameters[0].Type, Convert.ToInt32(c.Parameters[0].Value)));
        Assert.Equal(SqlParameterType.Int64, c.Parameters[1].Type);
        Assert.Equal(9999999999L, c.Parameters[1].Value);
        Assert.Equal(SqlParameterType.Decimal, c.Parameters[2].Type);
        Assert.Equal(1.50m, c.Parameters[2].Value);
    }

    [Fact]
    public void Dedup_SameLiteral_SameMarker_DifferentTypeDifferentMarker()
    {
        var c = GenerateSqlServer("SELECT id FROM t WHERE a = 'x' OR b = 'x' OR c = 1 OR d = '1'");

        Assert.Equal(3, c.Parameters.Length);
        Assert.Equal(2, CountOf(c.Sql, "@p0"));
    }

    [Fact]
    public void DateTimeTypedLiterals_AreBound_WithTypedValues()
    {
        var c = GenerateSqlServer("SELECT id FROM t WHERE d = DATE '2024-01-15' AND ts > TIMESTAMP '2024-01-15 10:30:00'");

        Assert.DoesNotContain('\'', c.Sql);
        Assert.Contains("CAST(@p0 AS date)", c.Sql);
        Assert.Equal(SqlParameterType.Date, c.Parameters[0].Type);
        Assert.Equal(new DateTime(2024, 1, 15), c.Parameters[0].Value);
        Assert.Equal(SqlParameterType.Timestamp, c.Parameters[1].Type);
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0), c.Parameters[1].Value);
    }

    [Fact]
    public void AllowListedInlinePositions_StayInline_AndPassTheChecker()
    {
        var c = GenerateSqlServer(
            "SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s, " +
            "CAST(x AS decimal(10, 2)) AS dx, CAST(y AS varchar(20)) AS vy, EXTRACT(year FROM d) AS yr " +
            "FROM t WHERE 1 = 1 GROUP BY 1, a, c, x, y, d ORDER BY 2 OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY");

        Assert.Empty(c.Parameters);
        Assert.Contains("2 PRECEDING", c.Sql);
        Assert.Contains("OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", c.Sql);
        Assert.Contains("nvarchar(20)", c.Sql);
        Assert.Contains("ORDER BY 2 ASC", c.Sql);
    }

    [Fact]
    public void CanonicalDenyAll_IsInline()
    {
        var c = GenerateSqlServer("SELECT id FROM t WHERE 1 = 0");
        Assert.Contains("1 = 0", c.Sql);
        Assert.Empty(c.Parameters);
    }

    public static IEnumerable<object[]> HostileValues() => new[]
    {
        "'; DROP TABLE t; --", "a' OR '1'='1", "x]]; --", "\"", "`", "\\", "/* c */", "$$q$$", "E'x'", "q'[x]'", "${x}",
        "\u0000", "‮evil", "aé日本", new string('x', 10_000), "N'abc'", "0x41", ";"
    }.Select(v => new object[] { v });

    [Theory]
    [MemberData(nameof(HostileValues))]
    public void NoLiteralTokensInOutput_HostileStringsAreOnlyBound(string hostile)
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new LiteralExpression(hostile, LiteralType.String), new SqlIdentifier("c", true)) },
                new NamedTableSource(new SqlQualifiedName("t"), null),
                new LikeExpression(new ColumnReference(new SqlQualifiedName("n")), new LiteralExpression(hostile, LiteralType.String),
                    new LiteralExpression("!", LiteralType.String)),
                null, null),
            null, null);

        var c = GenerateSqlServer(stmt);

        Assert.DoesNotContain('\'', c.Sql);
        Assert.DoesNotContain("--", c.Sql);
        Assert.DoesNotContain(';', c.Sql);
        Assert.Equal(hostile, c.Parameters[0].Value);
    }

    [Fact]
    public void HostileIdentifier_IsDelimited_AndCheckerPasses()
    {
        var stmt = new SelectStatement(null,
            new QuerySpecification(false,
                new SelectItem[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("a]; DROP -- /* ' \"", true) })), null) },
                new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("dbo", true), new SqlIdentifier("t]x", true) }), null),
                null, null, null),
            null, null);

        var c = GenerateSqlServer(stmt);
        Assert.Contains("[a]]; DROP -- /* ' \"]", c.Sql);
    }

    // Registration-gap detector: every SQL Server specific emission path must pass the production checker in bound mode.
    [Theory]
    [InlineData("SELECT id FROM t WHERE a IS DISTINCT FROM b")]
    [InlineData("SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c, a > 2 AS f FROM t")]
    [InlineData("SELECT a FROM t ORDER BY a DESC NULLS FIRST, b ASC NULLS LAST")]
    [InlineData("SELECT a FROM t ORDER BY a FETCH FIRST 3 ROWS WITH TIES")]
    [InlineData("SELECT substring(a FROM 2 FOR 3) AS s, substring(a FROM 2) AS s2 FROM t")]
    [InlineData("SELECT trim(a) AS x, trim(BOTH 'x' FROM a) AS y, position('a' IN b) AS z FROM t")]
    [InlineData("SELECT EXTRACT(DOW FROM d) AS dw, EXTRACT(week FROM d) AS wk FROM t")]
    [InlineData("SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)")]
    [InlineData("SELECT a, b, GROUPING(a, b) AS g FROM t GROUP BY CUBE (a, b)")]
    [InlineData("SELECT current_date, current_timestamp, localtimestamp FROM t")]
    [InlineData("WITH c AS (SELECT a FROM t) SELECT a FROM c UNION ALL SELECT a FROM c")]
    [InlineData("SELECT a FROM t WHERE a IN (1, 2, 3) AND b BETWEEN 1 AND 5 AND c LIKE 'a%'")]
    [InlineData("SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)")]
    [InlineData("SELECT t.a FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT 1 FROM v WHERE v.id = t.id)")]
    [InlineData("SELECT TRY_CAST(a AS integer) AS i, CAST(a AS timestamp) AS ts, CAST(a AS boolean) AS b, CAST(a AS double) AS d FROM t")]
    [InlineData("SELECT count(*) AS n, count(DISTINCT a) AS d FROM t")]
    [InlineData("SELECT a, ROW_NUMBER() OVER (PARTITION BY b ORDER BY c) AS rn FROM t")]
    [InlineData("SELECT a FROM t WHERE true AND NOT false")]
    [InlineData("SELECT TRUE AS x, FALSE AS y, NULL AS z FROM t")]
    [InlineData("SELECT -a AS n, a % 2 AS m, a || 'x' AS c FROM t")]
    public void SqlServerEmissionPaths_PassCheckerInBoundMode(string sql)
    {
        var c = GenerateSqlServer(sql);
        Assert.DoesNotContain('\'', c.Sql);
        Assert.DoesNotContain(';', c.Sql);
    }

    private static int CountOf(string s, string needle)
    {
        int c = 0, i = 0;
        while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { c++; i += needle.Length; }
        return c;
    }
}

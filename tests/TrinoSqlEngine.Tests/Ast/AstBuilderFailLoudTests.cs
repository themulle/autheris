namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 0: the AST builder must never drop a construct silently or fail with a NullReference/InvalidCast.
/// Anything it does not map is rejected with <see cref="AstBuildException"/> (WebSQL answers 400).
/// Constructs that later phases implement move from <see cref="Unsupported_Construct_IsRejected"/> to their own emission tests.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class AstBuilderFailLoudTests
{
    private readonly FastSqlEngine _engine = new();

    private SqlStatement Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        return new SqlAstBuilder().BuildStatement(tree);
    }

    [Theory]
    // grouping elements that are still not representable
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY AUTO")]
    // lambdas
    [InlineData("SELECT transform(tags, x -> upper(x)) FROM t")]
    // aggregate modifiers that are still not representable
    // window details that are still not representable
    [InlineData("SELECT lag(amount) IGNORE NULLS OVER (ORDER BY id) FROM t")]
    [InlineData("SELECT SUM(amount) OVER w FROM t WINDOW w AS (ORDER BY id)")]
    // special forms that stay unsupported (session information, JSON, overlay, listagg)
    [InlineData("SELECT current_user FROM t")]
    [InlineData("SELECT json_value(payload, 'lax $.a') FROM t")]
    [InlineData("SELECT overlay(name PLACING 'x' FROM 2) FROM t")]
    [InlineData("SELECT listagg(name, ',') WITHIN GROUP (ORDER BY name) FROM t")]
    // Sampling and pattern recognition (AP-1)
    [InlineData("SELECT id FROM t TABLESAMPLE BERNOULLI (10)")]
    [InlineData("SELECT id FROM t TABLESAMPLE SYSTEM (5)")]
    [InlineData("SELECT * FROM t PIVOT (SUM(amount) FOR dept IN ('Sales', 'Dev'))")]
    [InlineData("SELECT * FROM stock MATCH_RECOGNIZE (MEASURES A.price AS price ONE ROW PER MATCH AFTER MATCH SKIP PAST LAST ROW PATTERN (A+ B+) DEFINE A AS A.price > 10)")]
    public void Unsupported_Construct_IsRejected(string sql)
    {
        var ex = Assert.Throws<AstBuildException>(() => Build(sql));
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY dept HAVING COUNT(id) > 1")]
    [InlineData("SELECT row_number() OVER (PARTITION BY dept) FROM t")]
    [InlineData("SELECT DISTINCT dept FROM t ORDER BY dept DESC NULLS LAST LIMIT 5")]
    [InlineData("SELECT a FROM t UNION ALL SELECT a FROM u")]
    [InlineData("SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END FROM t WHERE b IN (SELECT b FROM u) AND c LIKE 'a%' ESCAPE '!'")]
    [InlineData("SELECT CAST(created_at AS date), EXTRACT(YEAR FROM created_at) FROM t")]
    [InlineData("WITH x AS (SELECT a FROM t) SELECT a FROM x WHERE EXISTS (SELECT 1 FROM u WHERE u.a = x.a)")]
    // Phase 4
    [InlineData("SELECT COUNT(id) FILTER (WHERE amount > 1) FROM t")]
    [InlineData("SELECT array_agg(id ORDER BY amount) FROM t")]
    [InlineData("SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t")]
    // Phase 6b
    [InlineData("SELECT id FROM t WHERE created_at > current_date - INTERVAL '1' DAY")]
    [InlineData("SELECT trim(name) FROM t")]
    [InlineData("SELECT substring(name FROM 2 FOR 3) FROM t")]
    [InlineData("SELECT position('a' IN name) FROM t")]
    [InlineData("SELECT current_timestamp FROM t")]
    // Phase 5
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY ROLLUP (dept)")]
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY CUBE (dept)")]
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY GROUPING SETS ((dept), ())")]
    [InlineData("SELECT dept, COUNT(id) FROM t GROUP BY DISTINCT dept")]
    [InlineData("SELECT id FROM t WHERE created_at > DATE '2024-01-01'")]
    [InlineData("SELECT id FROM t WHERE created_at > TIMESTAMP '2024-01-01 00:00:00'")]
    public void Supported_Construct_StillBuilds(string sql)
    {
        Assert.IsType<SelectStatement>(Build(sql));
    }

    [Theory]
    [InlineData("31", 31L)]
    [InlineData("0x1F", 31L)]
    [InlineData("0X1f", 31L)]
    [InlineData("0o17", 15L)]
    [InlineData("0b1010", 10L)]
    [InlineData("1_000", 1000L)]
    [InlineData("-0x10", -16L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0x7FFF_FFFF_FFFF_FFFF", long.MaxValue)]
    public void IntegerLiteral_AllTrinoRadixForms_Parse(string text, long expected)
    {
        Assert.Equal(expected, SqlAstBuilder.ParseIntegerLiteral(text));
    }

    [Theory]
    [InlineData("0x8000000000000000")]
    [InlineData("9223372036854775808")]
    [InlineData("0b2")]
    public void IntegerLiteral_OutOfRangeOrMalformed_IsATypedRejection(string text)
    {
        Assert.Throws<AstBuildException>(() => SqlAstBuilder.ParseIntegerLiteral(text));
    }

    [Theory]
    // fuzz seed 3882987415638235573: a hex ordinal reached the SQL Server generator as an unparsed string (FormatException)
    [InlineData("SELECT id FROM t ORDER BY 0x1F LIMIT 1", 31L)]
    [InlineData("SELECT id FROM t LIMIT 0x10", 16L)]
    public void HexIntegerLiteral_BuildsAsATypedInteger(string sql, long expected)
    {
        var statement = Assert.IsType<SelectStatement>(Build(sql));
        Assert.Contains(expected, Integers(statement));
    }

    [Fact]
    public void HexIntegerLiteral_BeyondBigint_IsATypedRejection()
    {
        Assert.Throws<AstBuildException>(() => Build("SELECT id FROM t WHERE id = 0xFFFFFFFFFFFFFFFFFF"));
    }

    private static System.Collections.Generic.IEnumerable<long> Integers(SelectStatement statement)
    {
        foreach (var element in statement.OrderBy?.Elements ?? [])
        {
            if (element.Expression is LiteralExpression { Type: LiteralType.Integer, Value: long ordinal }) yield return ordinal;
        }

        if (statement.Pagination?.Limit is LiteralExpression { Type: LiteralType.Integer, Value: long limit }) yield return limit;
    }
}

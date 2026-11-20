using TrinoSqlEngine.Ast.Builder;
using Xunit;

namespace TrinoSqlEngine.Tests.DialectSemantics;

[Trait("Category", "SqlGate")]
public sealed class DateAddQuarterAndConcatPrecedenceTests
{
    private static string Generate(string expression, TargetSqlDialect dialect) =>
        AstEmit.Inline($"SELECT id FROM orders WHERE ts >= {expression}", dialect, translateDates: true);

    [Fact]
    public void PostgreSql_DateAddQuarter_EmitsMakeIntervalMonthsTimesThree()
    {
        string sql = Generate("date_add('quarter', 2, ts)", TargetSqlDialect.PostgreSql);

        Assert.Contains("(\"ts\" + MAKE_INTERVAL(months => (2) * 3))", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("1 quarter", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oracle_DateAddQuarter_EmitsAddMonthsTimesThree()
    {
        string sql = Generate("date_add('quarter', 2, ts)", TargetSqlDialect.Oracle);

        Assert.Contains("ADD_MONTHS(\"TS\", 6)", sql, StringComparison.Ordinal);
        Assert.Contains("LEAST(EXTRACT(DAY FROM \"TS\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("QUARTER", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "DATEADD(quarter, 2, [ts])")]
    [InlineData(TargetSqlDialect.Databricks, "TIMESTAMPADD(QUARTER, 2, `ts`)")]
    public void SqlServer_And_Databricks_DateAddQuarter_EmitsQuarterUnit(TargetSqlDialect dialect, string expected) =>
        Assert.Contains(expected, Generate("date_add('quarter', 2, ts)", dialect), StringComparison.Ordinal);

    [Fact]
    public void Oracle_DateAddMonth_KeepsTheDayOfAnEndOfMonthSource()
    {
        string sql = Generate("date_add('month', 1, ts)", TargetSqlDialect.Oracle);

        Assert.Contains("EXTRACT(DAY FROM LAST_DAY(ADD_MONTHS(\"TS\", 1)))", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"name\" || (\"qty\" + 1)")]
    [InlineData(TargetSqlDialect.Oracle, "\"NAME\" || (\"QTY\" + 1)")]
    [InlineData(TargetSqlDialect.Databricks, "`name` || (`qty` + 1)")]
    [InlineData(TargetSqlDialect.SqlServer, "[name] + ([qty] + 1)")]
    [Trait("Category", "Security")]
    public void Concat_WithArithmeticChild_ParenthesizesChildAcrossAllDialects(TargetSqlDialect dialect, string expected)
    {
        string sql = AstEmit.Inline("SELECT id FROM orders WHERE label = name || (qty + 1)", dialect);

        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "(\"qty\" * 2) || \"name\"")]
    [InlineData(TargetSqlDialect.Oracle, "(\"QTY\" * 2) || \"NAME\"")]
    [InlineData(TargetSqlDialect.Databricks, "(`qty` * 2) || `name`")]
    [InlineData(TargetSqlDialect.SqlServer, "([qty] * 2) + [name]")]
    [Trait("Category", "Security")]
    public void Concat_WithArithmeticLeftChild_ParenthesizesChild(TargetSqlDialect dialect, string expected)
    {
        string sql = AstEmit.Inline("SELECT id FROM orders WHERE label = qty * 2 || name", dialect);

        Assert.Contains(expected, sql, StringComparison.Ordinal);
    }

    private static string Nested(int depth)
    {
        string expr = "ts";
        for (int i = 0; i < depth; i++) expr = $"date_add('month', 1, {expr})";
        return expr;
    }

    [Theory]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Databricks)]
    [Trait("Category", "Security")]
    public void NestedDateAdd_DoesNotDuplicateComplexTimestampOperand_And_RespectsLengthCap(TargetSqlDialect dialect)
    {
        int l10 = Generate(Nested(10), dialect).Length;
        int l20 = Generate(Nested(20), dialect).Length;
        int l40 = Generate(Nested(40), dialect).Length;

        // A compound operand is emitted once per level, so the emitted text grows linearly with the nesting depth.
        Assert.Equal(2 * (l20 - l10), l40 - l20);
        Assert.True(l40 < 4 * new FastSqlEngine().MaxQueryLength);
    }
}

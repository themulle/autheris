namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class AnalyticalDialectTests
{
    private readonly DuckDbDialectGenerator _duckDbGenerator = new();
    private readonly SnowflakeDialectGenerator _snowflakeGenerator = new();

    [Fact]
    public void DuckDb_BasicGeneration_EmitsPostgresStyleSyntax()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("col")), null) },
                From: new NamedTableSource(new SqlQualifiedName("tbl"), null),
                Where: new BinaryExpression(new ColumnReference(new SqlQualifiedName("x")), BinaryOperator.Equal, new ParameterReference("?")),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _duckDbGenerator.GenerateSql(stmt);
        Assert.Contains("\"col\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"tbl\"", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Snowflake_CaseFolding_FoldsUnquotedToUppercase()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[]
                {
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("lowercase_col")), null),
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("PreserveCase", IsQuoted: true) })), null)
                },
                From: new NamedTableSource(new SqlQualifiedName("my_table"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _snowflakeGenerator.GenerateSql(stmt);
        // Unquoted folded to uppercase and quoted
        Assert.Contains("\"LOWERCASE_COL\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"MY_TABLE\"", sql, StringComparison.Ordinal);
        // Quoted preserves case
        Assert.Contains("\"PreserveCase\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Snowflake_EmitsQuotedUpperIdentifiers_ProtectingKeywords()
    {
        string sql = "SELECT user, account, date FROM orders WHERE role = 'admin'";
        var engine = new FastSqlEngine();
        string generated = engine.GenerateGovernedSql(sql, new RlsOptions { TargetDialect = TargetSqlDialect.Snowflake });

        // Assert: Alle Bezeichner müssen gequotet und upper-case sein
        Assert.Contains("\"USER\"", generated);
        Assert.Contains("\"ACCOUNT\"", generated);
        Assert.Contains("\"DATE\"", generated);
        Assert.Contains("\"ORDERS\"", generated);
        Assert.Contains("\"ROLE\"", generated);
    }
}

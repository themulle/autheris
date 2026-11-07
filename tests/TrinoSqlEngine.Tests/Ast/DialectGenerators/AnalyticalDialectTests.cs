namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class AnalyticalDialectTests
{
    private readonly DuckDbDialectGenerator _duckDbGenerator = new();

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
}

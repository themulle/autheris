namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class PostgreSqlDialectTests
{
    private readonly PostgreSqlDialectGenerator _generator = new();

    [Fact]
    public void PostgreSql_IdentifierQuoting_DoubleQuotes()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("user\"col")), null) },
                From: new NamedTableSource(new SqlQualifiedName("my_table"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("\"user\"\"col\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"my_table\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_ParametersAndBooleans_DollarAndNative()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new LiteralExpression(true, LiteralType.Boolean), null) },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: new BinaryExpression(new ColumnReference(new SqlQualifiedName("id")), BinaryOperator.Equal, new ParameterReference("?")),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: new PaginationClause(new LiteralExpression(10L, LiteralType.Integer), new LiteralExpression(50L, LiteralType.Integer)));

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 50 OFFSET 10", sql, StringComparison.Ordinal);
    }
}

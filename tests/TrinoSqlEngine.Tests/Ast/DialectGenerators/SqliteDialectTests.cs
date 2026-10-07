namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class SqliteDialectTests
{
    private readonly SqliteDialectGenerator _generator = new();

    [Fact]
    public void Sqlite_ParameterBudget_Exceeding999_ThrowsException()
    {
        var paramsList = new List<Expression>();
        for (int i = 0; i < 1005; i++)
        {
            paramsList.Add(new ParameterReference("?"));
        }

        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new InListExpression(new ColumnReference(new SqlQualifiedName("id")), paramsList, false), null) },
                From: new NamedTableSource(new SqlQualifiedName("orders"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        Assert.Throws<DialectLimitExceededException>(() => _generator.GenerateSql(stmt));
    }

    [Fact]
    public void Sqlite_BooleansAndParameters_IntegerAndQuestionMark()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new LiteralExpression(true, LiteralType.Boolean), null) },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: new BinaryExpression(new ColumnReference(new SqlQualifiedName("x")), BinaryOperator.Equal, new ParameterReference("?")),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: new PaginationClause(null, new LiteralExpression(10L, LiteralType.Integer)));

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("SELECT 1 FROM", sql, StringComparison.Ordinal);
        Assert.Contains("?1", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 10", sql, StringComparison.Ordinal);
    }
}

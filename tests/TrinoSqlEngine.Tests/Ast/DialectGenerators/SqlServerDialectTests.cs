namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class SqlServerDialectTests
{
    private readonly SqlServerDialectGenerator _generator = new();

    [Fact]
    public void SqlServer_IdentifierQuoting_BracketAndDoubleBracket()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("col]name")), null) },
                From: new NamedTableSource(new SqlQualifiedName("tbl]name"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("[col]]name]", sql, StringComparison.Ordinal);
        Assert.Contains("[tbl]]name]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_StringLiterals_EmitsUnicodeNPrefixAndEscaping()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new LiteralExpression("O'Connor", LiteralType.String), null) },
                From: null,
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("N'O''Connor'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_Booleans_PredicateVsProjection()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new LiteralExpression(true, LiteralType.Boolean), null) },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: new LiteralExpression(true, LiteralType.Boolean),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(stmt);
        // Projection: 1
        Assert.StartsWith("SELECT 1 FROM", sql, StringComparison.OrdinalIgnoreCase);
        // WHERE: (1 = 1)
        Assert.Contains("WHERE (1 = 1)", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_Pagination_SyntheticOrderBy()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("id")), null) },
                From: new NamedTableSource(new SqlQualifiedName("orders"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: new PaginationClause(null, new LiteralExpression(25L, LiteralType.Integer)));

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_ParameterBudget_Exceeding2100_ThrowsException()
    {
        // Generate a statement with 2101 parameters
        var paramsList = new List<Expression>();
        for (int i = 0; i < 2105; i++)
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
}

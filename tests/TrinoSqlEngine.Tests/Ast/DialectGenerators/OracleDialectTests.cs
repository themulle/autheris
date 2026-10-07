namespace TrinoSqlEngine.Tests.Ast.DialectGenerators;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class OracleDialectTests
{
    private readonly OracleDialectGenerator _generator = new();

    [Fact]
    public void Oracle_IdentifierQuoting_DoubleQuotesAndUppercaseFolding()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[]
                {
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("CustomerId", IsQuoted: false) })), null),
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { new SqlIdentifier("ExactCase", IsQuoted: true) })), null)
                },
                From: new NamedTableSource(new SqlQualifiedName(new[] { new SqlIdentifier("my_table", IsQuoted: false) }), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("\"CUSTOMERID\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"ExactCase\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"MY_TABLE\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_TableAlias_OmitsAsKeyword()
    {
        var subquery = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("id")), null) },
                From: new NamedTableSource(new SqlQualifiedName("orders"), new SqlIdentifier("o")),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        var outer = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("id")), null) },
                From: new SubqueryTableSource(subquery, new SqlIdentifier("sub")),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        string sql = _generator.GenerateSql(outer);
        
        // Oracle throws ORA-00933 if AS is present in table/subquery aliases
        Assert.DoesNotContain("AS \"o\"", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AS \"sub\"", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"ORDERS\" \"O\"", sql, StringComparison.Ordinal);
        Assert.Contains(") \"SUB\"", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_ParametersAndBooleans_ColonPAndNumericBooleans()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[]
                {
                    new ColumnSelectItem(new LiteralExpression(true, LiteralType.Boolean), new SqlIdentifier("flag")),
                    new ColumnSelectItem(new BinaryExpression(new ColumnReference(new SqlQualifiedName("val")), BinaryOperator.GreaterThan, new LiteralExpression(10L, LiteralType.Integer)), new SqlIdentifier("is_greater"))
                },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: new BinaryExpression(new ColumnReference(new SqlQualifiedName("id")), BinaryOperator.Equal, new ParameterReference("?")),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: new PaginationClause(new LiteralExpression(10L, LiteralType.Integer), new LiteralExpression(50L, LiteralType.Integer)));

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains(":p1", sql, StringComparison.Ordinal);
        Assert.Contains("1 AS \"FLAG\"", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN \"VAL\" > 10 THEN 1 ELSE 0 END AS \"IS_GREATER\"", sql, StringComparison.Ordinal);
        Assert.Contains("OFFSET 10 ROWS FETCH NEXT 50 ROWS ONLY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_Pagination_LimitOnly_UsesFetchFirst()
    {
        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("id")), null) },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: null,
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: new PaginationClause(null, new LiteralExpression(25L, LiteralType.Integer)));

        string sql = _generator.GenerateSql(stmt);
        Assert.Contains("FETCH FIRST 25 ROWS ONLY", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Oracle_BudgetLimit_Enforces1000Parameters()
    {
        var items = new List<Expression>();
        for (int i = 0; i < 1001; i++)
        {
            items.Add(new ParameterReference("?"));
        }

        var stmt = new SelectStatement(
            With: null,
            Body: new QuerySpecification(
                Distinct: false,
                Projections: new[] { new ColumnSelectItem(new ColumnReference(new SqlQualifiedName("id")), null) },
                From: new NamedTableSource(new SqlQualifiedName("t"), null),
                Where: new InListExpression(new ColumnReference(new SqlQualifiedName("id")), items, IsNotIn: false),
                GroupBy: null,
                Having: null),
            OrderBy: null,
            Pagination: null);

        Assert.Throws<DialectLimitExceededException>(() => _generator.GenerateSql(stmt));
    }

    [Fact]
    public void FastSqlEngine_GenerateGovernedSql_OracleDialect_GeneratesValidOracleSql()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            TargetDialect = TargetSqlDialect.Oracle,
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 'tenant-42'")
        };

        string sql = "SELECT id, name FROM orders ORDER BY id OFFSET 5 LIMIT 10";
        string compiled = engine.GenerateGovernedSql(sql, options);

        // Verify:
        // 1. Double quotes on tables/columns with uppercase folding for unquoted identifiers
        Assert.Contains("\"ORDERS\"", compiled, StringComparison.Ordinal);
        Assert.Contains("\"ID\"", compiled, StringComparison.Ordinal);
        Assert.Contains("\"NAME\"", compiled, StringComparison.Ordinal);
        Assert.Contains("\"TENANT_ID\" = 'tenant-42'", compiled, StringComparison.Ordinal);
        // 2. Table alias without AS keyword
        Assert.DoesNotContain(") AS \"ORDERS\"", compiled, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(") \"ORDERS\"", compiled, StringComparison.Ordinal);
        // 3. Oracle pagination
        Assert.Contains("OFFSET 5 ROWS FETCH NEXT 10 ROWS ONLY", compiled, StringComparison.Ordinal);
    }
}

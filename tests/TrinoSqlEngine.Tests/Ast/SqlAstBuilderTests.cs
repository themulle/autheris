namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

public sealed class SqlAstBuilderTests
{
    private readonly FastSqlEngine _engine = new();

    private SqlStatement ParseToAst(string sql, AstBuilderOptions? options = null)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(options);
        return builder.BuildStatement(tree);
    }

    [Fact]
    public void BuildStatement_SimpleSelect_MapsCorrectly()
    {
        string sql = "SELECT id, name AS full_name FROM users WHERE active = true";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        var spec = Assert.IsType<QuerySpecification>(select.Body);

        Assert.Equal(2, spec.Projections.Count);
        var p0 = Assert.IsType<ColumnSelectItem>(spec.Projections[0]);
        var colRef0 = Assert.IsType<ColumnReference>(p0.Expression);
        Assert.Equal("id", colRef0.Name.SimpleName);
        Assert.Null(p0.Alias);

        var p1 = Assert.IsType<ColumnSelectItem>(spec.Projections[1]);
        Assert.NotNull(p1.Alias);
        Assert.Equal("full_name", p1.Alias.Value);

        var from = Assert.IsType<NamedTableSource>(spec.From);
        Assert.Equal("users", from.Name.SimpleName);

        var where = Assert.IsType<BinaryExpression>(spec.Where);
        Assert.Equal(BinaryOperator.Equal, where.Operator);
    }

    [Fact]
    public void BuildStatement_WildcardAndTableWildcard_MappedCorrectly()
    {
        string sql = "SELECT *, u.* FROM users u";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        var spec = Assert.IsType<QuerySpecification>(select.Body);

        Assert.Equal(2, spec.Projections.Count);
        var w0 = Assert.IsType<WildcardSelectItem>(spec.Projections[0]);
        Assert.Null(w0.Qualifier);

        var w1 = Assert.IsType<WildcardSelectItem>(spec.Projections[1]);
        Assert.NotNull(w1.Qualifier);
        Assert.Equal("u", w1.Qualifier.SimpleName);
    }

    [Fact]
    public void BuildStatement_JoinsAndConditions_MappedCorrectly()
    {
        string sql = "SELECT * FROM orders o LEFT JOIN customers c ON o.customer_id = c.id CROSS JOIN logs";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        var spec = Assert.IsType<QuerySpecification>(select.Body);

        var crossJoin = Assert.IsType<JoinedTableSource>(spec.From);
        Assert.Equal(JoinType.Cross, crossJoin.Type);

        var leftJoin = Assert.IsType<JoinedTableSource>(crossJoin.Left);
        Assert.Equal(JoinType.LeftOuter, leftJoin.Type);
        var onCond = Assert.IsType<OnJoinCondition>(leftJoin.Condition);
        Assert.IsType<BinaryExpression>(onCond.Predicate);
    }

    [Fact]
    public void BuildStatement_UsingJoin_MappedCorrectly()
    {
        string sql = "SELECT * FROM orders JOIN customers USING (customer_id, tenant_id)";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        var spec = Assert.IsType<QuerySpecification>(select.Body);
        var join = Assert.IsType<JoinedTableSource>(spec.From);
        Assert.Equal(JoinType.Inner, join.Type);

        var usingCond = Assert.IsType<UsingJoinCondition>(join.Condition);
        Assert.Equal(2, usingCond.Columns.Count);
        Assert.Equal("customer_id", usingCond.Columns[0].Value);
        Assert.Equal("tenant_id", usingCond.Columns[1].Value);
    }

    [Fact]
    public void BuildStatement_WithCte_MappedCorrectly()
    {
        string sql = "WITH regional_sales AS (SELECT region, SUM(amount) AS total FROM orders GROUP BY region) SELECT * FROM regional_sales";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        Assert.NotNull(select.With);
        Assert.False(select.With.IsRecursive);
        Assert.Single(select.With.Ctes);

        var cte = select.With.Ctes[0];
        Assert.Equal("regional_sales", cte.Name.Value);
        Assert.IsType<SelectStatement>(cte.Query);
    }

    [Fact]
    public void BuildStatement_SetOperations_MappedCorrectly()
    {
        string sql = "SELECT id FROM t1 UNION ALL SELECT id FROM t2 INTERSECT SELECT id FROM t3";
        var stmt = ParseToAst(sql);

        var select = Assert.IsType<SelectStatement>(stmt);
        var setOp = Assert.IsType<SetOperationQuery>(select.Body);
        Assert.Equal(SetOperator.Union, setOp.Operator);
        Assert.False(setOp.Distinct); // UNION ALL
    }

    [Fact]
    public void BuildStatement_DmlStatements_MappedCorrectly()
    {
        var delStmt = ParseToAst("DELETE FROM orders WHERE id = 10", new AstBuilderOptions { EnforceReadOnlyQueries = false });
        var delete = Assert.IsType<DeleteStatement>(delStmt);
        Assert.Equal("orders", delete.TargetTable.Name.SimpleName);
        Assert.NotNull(delete.Where);

        var updStmt = ParseToAst("UPDATE orders SET status = 'DONE' WHERE id = 10", new AstBuilderOptions { EnforceReadOnlyQueries = false });
        var update = Assert.IsType<UpdateStatement>(updStmt);
        Assert.Equal("orders", update.TargetTable.Name.SimpleName);
        Assert.Single(update.Assignments);
        Assert.Equal("status", update.Assignments[0].Column.Value);

        var insStmt = ParseToAst("INSERT INTO orders (id, amount) VALUES (1, 100)", new AstBuilderOptions { EnforceReadOnlyQueries = false });
        var insert = Assert.IsType<InsertStatement>(insStmt);
        Assert.Equal("orders", insert.TargetTable.Name.SimpleName);
        Assert.Equal(2, insert.Columns!.Count);
        Assert.IsType<ValuesQueryBody>(insert.Source);
    }

    [Fact]
    public void BuildStatement_DdlRejection_ThrowsSecurityException()
    {
        Assert.Throws<SecurityException>(() => ParseToAst("DROP TABLE users"));
        Assert.Throws<SecurityException>(() => ParseToAst("CREATE TABLE users (id int)"));
        Assert.Throws<SecurityException>(() => ParseToAst("ALTER TABLE users ADD COLUMN x int"));
        Assert.Throws<SecurityException>(() => ParseToAst("TRUNCATE TABLE users"));
    }

    [Fact]
    public void BuildStatement_ReadOnlyEnforcement_RejectsDml()
    {
        Assert.Throws<SecurityException>(() => ParseToAst("DELETE FROM orders WHERE id = 1", new AstBuilderOptions { EnforceReadOnlyQueries = true }));
        Assert.Throws<SecurityException>(() => ParseToAst("UPDATE orders SET id = 2", new AstBuilderOptions { EnforceReadOnlyQueries = true }));
        Assert.Throws<SecurityException>(() => ParseToAst("INSERT INTO orders VALUES (1)", new AstBuilderOptions { EnforceReadOnlyQueries = true }));
    }

    [Fact]
    public void BuildStatement_MethodCalls_ThrowsSecurityException()
    {
        Assert.Throws<SecurityException>(() => ParseToAst("SELECT (obj).method() FROM t"));
        Assert.Throws<SecurityException>(() => ParseToAst("SELECT Type::staticMethod() FROM t"));
    }
}

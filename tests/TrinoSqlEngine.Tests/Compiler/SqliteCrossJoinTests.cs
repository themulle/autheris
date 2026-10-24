using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Issue #339: SQLite treats CROSS JOIN as a join-order hint (the planner never reorders it), so comma joins and explicit CROSS JOINs
/// render as a plain JOIN there. Every other dialect keeps CROSS JOIN.
/// </summary>
public class SqliteCrossJoinTests
{
    [Theory]
    [InlineData("SELECT a FROM t1, t2", "SELECT \"a\" FROM \"t1\" JOIN \"t2\"")]
    [InlineData("SELECT a FROM t1 CROSS JOIN t2", "SELECT \"a\" FROM \"t1\" JOIN \"t2\"")]
    [InlineData("SELECT a FROM t1, t2, t3", "SELECT \"a\" FROM \"t1\" JOIN \"t2\" JOIN \"t3\"")]
    public void Sqlite_CrossJoin_RendersAsPlainJoin(string sql, string expected)
    {
        var generated = CompilerTestHelpers.Generate(TargetSqlDialect.Sqlite, sql).Sql;

        Assert.Equal(expected, generated);
        Assert.DoesNotContain("CROSS JOIN", generated);
    }

    [Fact]
    public void Sqlite_InnerAndLeftJoin_AreUnchanged() =>
        Assert.Equal(
            "SELECT \"a\" FROM \"t1\" INNER JOIN \"t2\" ON \"t1\".\"x\" = \"t2\".\"x\" LEFT OUTER JOIN \"t3\" ON \"t2\".\"y\" = \"t3\".\"y\"",
            CompilerTestHelpers.Generate(TargetSqlDialect.Sqlite, "SELECT a FROM t1 INNER JOIN t2 ON t1.x = t2.x LEFT JOIN t3 ON t2.y = t3.y").Sql);

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT \"a\" FROM \"t1\" CROSS JOIN \"t2\"", "SELECT \"a\" FROM \"t1\" CROSS JOIN \"t2\" CROSS JOIN \"t3\"")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT [a] FROM [t1] CROSS JOIN [t2]", "SELECT [a] FROM [t1] CROSS JOIN [t2] CROSS JOIN [t3]")]
    [InlineData(TargetSqlDialect.DuckDb, "SELECT \"a\" FROM \"t1\" CROSS JOIN \"t2\"", "SELECT \"a\" FROM \"t1\" CROSS JOIN \"t2\" CROSS JOIN \"t3\"")]
    [InlineData(TargetSqlDialect.Oracle, "SELECT \"A\" FROM \"T1\" CROSS JOIN \"T2\"", "SELECT \"A\" FROM \"T1\" CROSS JOIN \"T2\" CROSS JOIN \"T3\"")]
    [InlineData(TargetSqlDialect.Databricks, "SELECT `a` FROM `t1` CROSS JOIN `t2`", "SELECT `a` FROM `t1` CROSS JOIN `t2` CROSS JOIN `t3`")]
    public void OtherDialects_KeepCrossJoin(TargetSqlDialect dialect, string two, string three)
    {
        Assert.Equal(two, CompilerTestHelpers.Generate(dialect, "SELECT a FROM t1, t2").Sql);
        Assert.Equal(two, CompilerTestHelpers.Generate(dialect, "SELECT a FROM t1 CROSS JOIN t2").Sql);
        Assert.Equal(three, CompilerTestHelpers.Generate(dialect, "SELECT a FROM t1, t2, t3").Sql);
    }
}

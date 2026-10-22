using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>SQLE-03: a right-nested set operation (INTERSECT precedence) is rejected unless the dialect declares <c>IntersectBindsTighter</c>.</summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class SqleSetOperationPrecedenceTests
{
    private static void Validate(TargetSqlDialect dialect, string sql) =>
        DialectCapabilityValidator.Validate(CompilerTestHelpers.Build(sql), DialectCapabilityTable.Default.Get(dialect));

    [Fact]
    public void UnionThenIntersect_Oracle_Rejected() =>
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            Validate(TargetSqlDialect.Oracle, "SELECT a FROM t UNION SELECT a FROM u INTERSECT SELECT a FROM v"));

    [Fact]
    public void ExceptThenIntersect_Sqlite_Rejected() =>
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            Validate(TargetSqlDialect.Sqlite, "SELECT a FROM t EXCEPT SELECT a FROM u INTERSECT SELECT a FROM v"));

    [Fact]
    public void UnionThenIntersect_PostgreSql_EmitsFlat()
    {
        const string sql = "SELECT a FROM t UNION SELECT a FROM u INTERSECT SELECT a FROM v";
        Validate(TargetSqlDialect.PostgreSql, sql);

        var emitted = CompilerTestHelpers.Generate(TargetSqlDialect.PostgreSql, sql).Sql;

        Assert.Contains("UNION", emitted);
        Assert.Contains("INTERSECT", emitted);
    }

    [Fact]
    public void IntersectThenUnion_Sqlite_Allowed() =>
        Validate(TargetSqlDialect.Sqlite, "SELECT a FROM t INTERSECT SELECT a FROM u UNION SELECT a FROM v");

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, true)]
    [InlineData(TargetSqlDialect.DuckDb, true)]
    [InlineData(TargetSqlDialect.PostgreSql, true)]
    [InlineData(TargetSqlDialect.Databricks, true)]
    [InlineData(TargetSqlDialect.Oracle, false)]
    [InlineData(TargetSqlDialect.Sqlite, false)]
    public void CapabilityTable_IntersectBindsTighter_ExplicitPerDialect(TargetSqlDialect dialect, bool expected) =>
        Assert.Equal(expected, DialectCapabilityTable.Default.Get(dialect).IntersectBindsTighter);
}

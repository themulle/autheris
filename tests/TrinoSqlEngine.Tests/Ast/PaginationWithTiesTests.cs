namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class PaginationWithTiesTests
{
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "FETCH NEXT 5 ROWS WITH TIES")]
    [InlineData(TargetSqlDialect.PostgreSql, "FETCH FIRST 5 ROWS WITH TIES")]
    [InlineData(TargetSqlDialect.Oracle, "FETCH FIRST 5 ROWS WITH TIES")]
    [InlineData(TargetSqlDialect.Snowflake, "FETCH FIRST 5 ROWS WITH TIES")]
    public void WithTies_SupportedDialects_EmitCorrectClause(TargetSqlDialect dialect, string expectedSnippet)
    {
        string sql = "SELECT id, score FROM scores ORDER BY score DESC FETCH FIRST 5 ROWS WITH TIES";
        string generated = AstEmit.Inline(sql, dialect);
        Assert.Contains(expectedSnippet, generated);
    }

    [Theory]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public void WithTies_UnsupportedDialects_ThrowAstBuildException(TargetSqlDialect dialect)
    {
        string sql = "SELECT id, score FROM scores ORDER BY score DESC FETCH FIRST 5 ROWS WITH TIES";
        var ex = Assert.Throws<AstBuildException>(() => AstEmit.Inline(sql, dialect));
        Assert.Contains("WITH TIES", ex.Message);
    }

    [Fact]
    public void WithTies_WithoutOrderBy_ThrowsValidationException()
    {
        string sql = "SELECT id FROM scores FETCH FIRST 5 ROWS WITH TIES";
        Assert.Throws<AstBuildException>(() => AstEmit.Inline(sql, TargetSqlDialect.SqlServer));
    }
}

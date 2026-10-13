namespace TrinoSqlEngine.Tests;

using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>SQL-1: SQL Server / SQLite lex [...] as quoted identifier; the gateway grammar treats it as array syntax.</summary>
public class Sql1BracketLexerDifferentialTests
{
    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT ARRAY[1,2][1] FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT '[' , x FROM orders WHERE a = ']--'")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT '[' || x || ']' FROM orders")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT 'a--b' FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT 'a/*b' FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT \"a[b\" FROM orders")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT [id] FROM orders")]
    public void BracketsAndBracketLiterals_AreRejected(TargetSqlDialect dialect, string sql)
    {
        Assert.Throws<ParseCanceledException>(() => new ProbeEnv(dialect).Compile(sql));
    }

    [Fact]
    public void PostgreSql_ArraySubscripts_AreRejectedByTheStrictGuards_ButNotByTheDialectGuards()
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);

        // The governed path runs with the strict token guards on every dialect; that is stricter than the legacy rewriter, which
        // kept array subscripts for PostgreSQL targets.
        Assert.Throws<ParseCanceledException>(() => env.Compile("SELECT tags[1] FROM orders"));

        // The bracket guard is dialect specific where a caller chooses the guards from the dialect.
        var ex = Record.Exception(() => env.Engine.Compile("SELECT ARRAY[1,2][1] AS x FROM orders".AsMemory(),
            env.Request(guards: SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.PostgreSql)), System.Threading.CancellationToken.None));
        Assert.False(ex is ParseCanceledException, "the bracket guard must not apply to PostgreSQL when chosen from the dialect");
    }

    [Fact]
    public void SqlServer_PlainLiterals_StillWork()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.SqlServer).Compile("SELECT 'abc' FROM orders");

        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "abc"));
    }

    [Fact]
    public void TokenGuards_ForceTheBracketCheckForSqlServerAndSqlite()
    {
        Assert.True(SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.SqlServer).RejectBracketLexerDifferentials);
        Assert.True(SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.Sqlite).RejectBracketLexerDifferentials);
        Assert.False(SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.PostgreSql).RejectBracketLexerDifferentials);
    }

    [Fact]
    public void UEscape_IsRejected()
    {
        Assert.Throws<ParseCanceledException>(() =>
            new ProbeEnv(TargetSqlDialect.PostgreSql).Compile("SELECT U&'d!0061t' UESCAPE '!' FROM orders"));
    }
}

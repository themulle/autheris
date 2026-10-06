namespace TrinoSqlEngine.Tests;

using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using Xunit;

/// <summary>SQL-1: SQL Server / SQLite lex [...] as quoted identifier; the gateway grammar treats it as array syntax.</summary>
public class Sql1BracketLexerDifferentialTests
{
    private readonly FastSqlEngine _engine = new();

    private static RlsOptions Options(TargetSqlDialect dialect) => new()
    {
        TargetDialect = dialect,
        RejectComments = true,
        RejectBackslashInStrings = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = dialect != TargetSqlDialect.PostgreSql,
        EnforceReadOnlyQueries = false,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42")
    };

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
        Assert.Throws<ParseCanceledException>(() => _engine.RewriteRls(sql.AsMemory(), Options(dialect)));
    }

    [Fact]
    public void PostgreSql_ArraySubscripts_StillWork()
    {
        string secured = _engine.RewriteRls("SELECT tags[1], ARRAY[1,2] FROM orders".AsMemory(), Options(TargetSqlDialect.PostgreSql));
        Assert.Contains("[1]", secured);
    }

    [Fact]
    public void SqlServer_PlainLiterals_StillWork()
    {
        string secured = _engine.RewriteRls("SELECT 'abc' FROM orders".AsMemory(), Options(TargetSqlDialect.SqlServer));
        Assert.Contains("'abc'", secured);
    }

    [Fact]
    public void FromRlsOptions_ForcesBracketCheckForSqlServerAndSqlite()
    {
        Assert.True(SqlTokenSecurityOptions.FromRlsOptions(new RlsOptions { TargetDialect = TargetSqlDialect.SqlServer }).RejectBracketLexerDifferentials);
        Assert.True(SqlTokenSecurityOptions.FromRlsOptions(new RlsOptions { TargetDialect = TargetSqlDialect.Sqlite }).RejectBracketLexerDifferentials);
        Assert.False(SqlTokenSecurityOptions.FromRlsOptions(new RlsOptions { TargetDialect = TargetSqlDialect.PostgreSql }).RejectBracketLexerDifferentials);
    }

    [Fact]
    public void UEscape_IsRejected()
    {
        Assert.Throws<ParseCanceledException>(() =>
            _engine.RewriteRls("SELECT U&'d!0061t' UESCAPE '!' FROM orders".AsMemory(), Options(TargetSqlDialect.PostgreSql)));
    }
}

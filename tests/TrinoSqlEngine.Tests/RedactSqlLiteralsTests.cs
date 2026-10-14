using TrinoSqlEngine.Ast.Visitors;
using Xunit;

namespace TrinoSqlEngine.Tests;

/// <summary>AU-06 / L-2: the audit redaction replaces literal tokens and keeps every other character of the statement (whitespace and comments included).</summary>
[Trait("Category", "SqlGate")]
public class RedactSqlLiteralsTests
{
    [Theory]
    [InlineData("SELECT 1", "SELECT @p_redacted")]
    [InlineData("SELECT  a ,  'x'  FROM t WHERE id=42 AND n = 1.5 AND d = 1e3", "SELECT  a ,  @p_redacted  FROM t WHERE id=@p_redacted AND n = @p_redacted AND d = @p_redacted")]
    [InlineData("SELECT /* secret 'a' */ id -- 'b' 7\nFROM \"Odd Name\" WHERE x = 'it''s'", "SELECT /* secret 'a' */ id -- 'b' 7\nFROM \"Odd Name\" WHERE x = @p_redacted")]
    [InlineData("SELECT U&'d\\0061' , X'AB', $$tag$$ FROM t", "SELECT @p_redacted , @p_redacted, @p_redacted FROM t")]
    [InlineData("  \t SELECT a\r\n  FROM t  ", "  \t SELECT a\r\n  FROM t  ")]
    public void LiteralTokens_AreReplaced_EverythingElseIsKept(string sql, string expected) =>
        Assert.Equal(expected, AstSecurityVisitor.RedactSqlLiterals(sql));

    [Theory]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT a FROM t WHERE x = 'abc")]
    public void AnUnrecognisedStatement_IsFullyRedacted(string sql) =>
        Assert.Equal(AstSecurityVisitor.FullyRedactedStatement, AstSecurityVisitor.RedactSqlLiterals(sql));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingToRedact_IsEmpty(string? sql) => Assert.Equal(string.Empty, AstSecurityVisitor.RedactSqlLiterals(sql));
}

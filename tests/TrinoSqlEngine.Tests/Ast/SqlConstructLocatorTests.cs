namespace TrinoSqlEngine.Tests.Ast;

using TrinoSqlEngine.Ast.Builder;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class SqlConstructLocatorTests
{
    [Fact]
    public void Keyword_InStringLiteral_NotMatched()
    {
        Assert.False(SqlConstructLocator.TryLocateTokens("SELECT 'LATERAL' AS a FROM t", ["LATERAL"], out _, out _));
        Assert.False(SqlConstructLocator.TryLocateTokens("SELECT \"LATERAL\" FROM t", ["LATERAL"], out _, out _));
    }

    [Fact]
    public void Keyword_InComment_NotMatched()
    {
        Assert.False(SqlConstructLocator.TryLocateTokens("SELECT a /* LATERAL */ FROM t -- LATERAL", ["LATERAL"], out _, out _));
    }

    [Fact]
    public void MultiLine_ReturnsOneBasedPosition()
    {
        Assert.True(SqlConstructLocator.TryLocateTokens("SELECT a\nFROM t\n  GROUP BY ALL", ["GROUP", "BY", "ALL"], out int line, out int column));
        Assert.Equal(3, line);
        Assert.Equal(3, column);
    }

    [Fact]
    public void Identifier_CaseInsensitive_FirstOccurrence()
    {
        Assert.True(SqlConstructLocator.TryLocateIdentifier("SELECT 'Foo', Foo, foo FROM t", "FOO", out int line, out int column));
        Assert.Equal(1, line);
        Assert.Equal(15, column);
    }

    [Fact]
    public void Identifier_Quoted_NotMatched()
    {
        Assert.False(SqlConstructLocator.TryLocateIdentifier("SELECT \"foo\" FROM t", "foo", out _, out _));
    }

    [Theory]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT \u0000 FROM t")]
    [InlineData("")]
    public void MalformedText_NeverThrows(string sql)
    {
        Assert.False(SqlConstructLocator.TryLocateTokens(sql, ["LATERAL"], out _, out _));
    }

    [Fact]
    public void EmptyKeywordList_NotFound()
    {
        Assert.False(SqlConstructLocator.TryLocateTokens("SELECT 1", [], out _, out _));
    }
}

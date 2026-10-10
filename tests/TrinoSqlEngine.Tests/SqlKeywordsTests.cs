namespace TrinoSqlEngine.Tests;

using TrinoSqlEngine;
using Xunit;

public sealed class SqlKeywordsTests
{
    [Theory]
    [InlineData(SqlBaseLexer.SELECT, true)]
    [InlineData(SqlBaseLexer.WHERE, true)]
    [InlineData(SqlBaseLexer.GROUP, true)]
    [InlineData(SqlBaseLexer.UTF8, true)]
    [InlineData(SqlBaseLexer.UTF16, true)]
    [InlineData(SqlBaseLexer.UTF32, true)]
    [InlineData(SqlBaseLexer.JSON_TABLE, true)]
    [InlineData(SqlBaseLexer.MATCH_RECOGNIZE, true)]
    public void Keywords_WithAlphanumerics_AreRecognized(int tokenType, bool expected)
    {
        Assert.Equal(expected, SqlKeywords.IsKeyword(tokenType));
    }

    [Theory]
    [InlineData(SqlBaseLexer.IDENTIFIER, false)]
    [InlineData(SqlBaseLexer.INTEGER_VALUE, false)]
    [InlineData(SqlBaseLexer.EQ, false)]
    [InlineData(SqlBaseLexer.PLUS, false)]
    [InlineData(-1, false)]
    [InlineData(9999, false)]
    public void NonKeywordsAndSymbols_AreNotKeywords(int tokenType, bool expected)
    {
        Assert.Equal(expected, SqlKeywords.IsKeyword(tokenType));
    }
}

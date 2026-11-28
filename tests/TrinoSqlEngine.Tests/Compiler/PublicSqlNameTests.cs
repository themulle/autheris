using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>The single sanitizer of client-text names that an error response may echo.</summary>
[Trait("Category", "SqlGate")]
public class PublicSqlNameTests
{
    [Theory]
    [InlineData("pg_sleep")]
    [InlineData("_x1$")]
    [InlineData("schema.fn")]
    [InlineData("cat.schema.fn")]
    public void TryCreate_SimpleAndQualified_Accepted(string raw)
    {
        Assert.True(PublicSqlName.TryCreate(raw, out var name));
        Assert.Equal(raw, name);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\tb")]
    [InlineData("a\r\nHTTP/1.1 200")]
    [InlineData("a\u001Fb")]
    public void TryCreate_ControlCharsAndNewlines_Rejected(string raw)
    {
        Assert.False(PublicSqlName.TryCreate(raw, out var name));
        Assert.Equal(string.Empty, name);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("<script>")]
    [InlineData("a\"b")]
    [InlineData("\"quoted\"")]
    [InlineData("a'b")]
    [InlineData("`a`")]
    [InlineData("[a]")]
    [InlineData("a b")]
    [InlineData("a&b")]
    [InlineData("{0}")]
    public void TryCreate_HtmlAndQuoteChars_Rejected(string raw) => Assert.False(PublicSqlName.TryCreate(raw, out _));

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("a..b")]
    [InlineData(".a")]
    [InlineData("a.")]
    [InlineData("1a")]
    [InlineData("$a")]
    [InlineData("a.1b")]
    public void TryCreate_EmptySegment_Rejected(string raw) => Assert.False(PublicSqlName.TryCreate(raw, out _));

    [Fact]
    public void TryCreate_NullRejected() => Assert.False(PublicSqlName.TryCreate(null, out _));

    [Fact]
    [Trait("Category", "Security")]
    public void TryCreate_Over128_Rejected()
    {
        Assert.True(PublicSqlName.TryCreate(new string('a', 128), out _));
        Assert.False(PublicSqlName.TryCreate(new string('a', 129), out _));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TryCreate_FourSegments_Rejected()
    {
        Assert.True(PublicSqlName.TryCreate("a.b.c", out _));
        Assert.False(PublicSqlName.TryCreate("a.b.c.d", out _));
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("pаssword")]
    [InlineData("café")]
    [InlineData("ｐg_sleep")]
    public void TryCreate_NonAsciiHomoglyph_Rejected(string raw) => Assert.False(PublicSqlName.TryCreate(raw, out _));
}

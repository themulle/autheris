namespace Autheris.Tests.Unit.Mcp;

using System.Linq;
using System.Text.Json;
using Autheris.Application.Mcp.Services;
using Shouldly;
using Xunit;

/// <summary>PLAN-CAT-146-A WP-A1: the read-side sanitiser for catalog display names (SEC-CAT146A-02..04).</summary>
public sealed class McpCatalogTextTests
{
    private const string Table = "orders";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullEmptyOrWhitespace_FallsBackToTableName(string? stored) =>
        McpCatalogText.DisplayName(stored, Table).ShouldBe(Table);

    [Fact]
    public void PlainUnicodeText_Unchanged() =>
        McpCatalogText.DisplayName("Aufträge", Table).ShouldBe("Aufträge");

    [Fact]
    public void WhitespaceRuns_CollapsedAndTrimmed() =>
        McpCatalogText.DisplayName("  Monthly     revenue  ", Table).ShouldBe("Monthly revenue");

    [Fact]
    public void ControlAndBidiOverrideCharacters_Removed() =>
        McpCatalogText.DisplayName("Or\u0000de‮rs\u0007", Table).ShouldBe("Or ders");

    [Fact]
    public void LongerThan256_Truncated_NoLoneSurrogate()
    {
        var result = McpCatalogText.DisplayName(new string('a', 255) + "\U0001F600\U0001F600", Table);

        result.Length.ShouldBe(255);
        result.ShouldBe(new string('a', 255));
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
        McpCatalogText.DisplayName(new string('b', 1000), Table).Length.ShouldBe(256);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Sanitiser_NewlineAndTab_BecomeSingleSpace_WordsNotFused()
    {
        // SEC-CAT146A-02 / T-CAT146A-01: control whitespace is a word break, U+0085 and U+2028/2029 included.
        McpCatalogText.DisplayName("Sales\n\tOrders\r\nGermany\u0085EU\u2028Q1\u2029end", Table)
            .ShouldBe("Sales Orders Germany EU Q1 end");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Sanitiser_TagCharactersAndZeroWidthAndBidi_Removed()
    {
        // SEC-CAT146A-03 / T-CAT146A-02: ASCII smuggling (Tags block), zero-width, bidi isolates, word joiner, VS16, soft hyphen.
        var tags = string.Concat("ignore previous".Select(c => char.ConvertFromUtf32(0xE0000 + c)));
        var input = $"A{tags}​b‮c⁦d⁠e️f­gh͸i﻿j᠎k";

        McpCatalogText.DisplayName(input, Table).ShouldBe("Abcdefghijk");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Sanitiser_LoneSurrogates_NeverThrow_ValidJson()
    {
        // SEC-CAT146A-04 / T-CAT146A-03
        foreach (var input in new[] { "a\uD800b", "\uDC00x", "x\uD83D", "\uD83D😀", "\uDE00\uD83D" })
        {
            var result = McpCatalogText.DisplayName(input, Table);

            Should.NotThrow(() => JsonSerializer.Serialize(result));
            result.ShouldNotContain("�");
            JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(result)).ShouldBe(result);
        }

        McpCatalogText.DisplayName("a\uD800b", Table).ShouldBe("ab");
        McpCatalogText.DisplayName("\uD83D😀", Table).ShouldBe("\U0001F600");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Sanitiser_CapAtSurrogatePair_NoSplit_AndCountedAfterSanitising()
    {
        // SEC-CAT146A-04 / T-CAT146A-04: invisible padding does not consume the cap; a pair straddling it is dropped whole.
        var padded = string.Concat(Enumerable.Repeat("​", 500)) + new string('a', 256) + "tail";
        McpCatalogText.DisplayName(padded, Table).ShouldBe(new string('a', 256));

        var straddle = new string('a', 255) + "\U0001F600";
        var result = McpCatalogText.DisplayName(straddle, Table);
        result.ShouldBe(new string('a', 255));
        JsonSerializer.Serialize(result).ShouldNotBeNull();

        var exact = new string('a', 254) + "\U0001F600";
        McpCatalogText.DisplayName(exact, Table).ShouldBe(exact);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Sanitiser_AllInvisibleInput_FallsBackToTableName()
    {
        // SEC-CAT146A-02/03 / T-CAT146A-05
        McpCatalogText.DisplayName("​‮\U000E0041\uD800 \n\t️", Table).ShouldBe(Table);
    }
}

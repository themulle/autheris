namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Search;
using Shouldly;
using Xunit;

public sealed class DiacriticNormalizerTests
{
    [Fact]
    public void NormalizeAscii_WhenPureAscii_ReturnsExactSameInstance()
    {
        var input = "pure_ascii_text_123";
        var result = DiacriticNormalizer.StripDiacritics(input);
        object.ReferenceEquals(input, result).ShouldBeTrue();
    }

    [Theory]
    [InlineData("März", "Maerz")]
    [InlineData("Köpfe", "Koepfe")]
    [InlineData("Füße", "Fuesse")]
    [InlineData("groß", "gross")]
    [InlineData("STRAßE", "STRASSE")]
    [InlineData("Äpfel", "Aepfel")]
    [InlineData("Öfen", "Oefen")]
    [InlineData("Über", "Ueber")]
    public void ExpandGermanUmlauts_ReplacesUmlautsAndEszett(string input, string expected)
    {
        var result = DiacriticNormalizer.ExpandGermanUmlauts(input);
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("café", "cafe")]
    [InlineData("crème", "creme")]
    [InlineData("château", "chateau")]
    [InlineData("façade", "facade")]
    [InlineData("año", "ano")]
    [InlineData("Łódź", "Lodz")]
    [InlineData("Zürich", "Zurich")]
    public void StripDiacritics_StripsAccents(string input, string expected)
    {
        var result = DiacriticNormalizer.StripDiacritics(input);
        result.ShouldBe(expected);
    }

    [Fact]
    public void NormalizeAll_ExpandsUmlautsAndStripsDiacritics()
    {
        var input = "Übermäßig geschützte Façade";
        var umlautExpanded = DiacriticNormalizer.ExpandGermanUmlauts(input);
        var fullyStripped = DiacriticNormalizer.StripDiacritics(umlautExpanded);

        umlautExpanded.ShouldBe("Uebermaessig geschuetzte Façade");
        fullyStripped.ShouldBe("Uebermaessig geschuetzte Facade");
    }
}

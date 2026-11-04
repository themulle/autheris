namespace Autheris.Tests.Unit.DataCatalog;

using Autheris.Application.DataCatalog.Services;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogMinGroupSizeRulesTests
{
    [Theory]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("+5")]
    [InlineData("-5")]
    [InlineData("5.0")]
    [InlineData("99999999999999999999999")]
    [InlineData("abc")]
    [InlineData("0")]
    public void Rules_Overflow_Whitespace_Sign_Decimal_Rejected(string invalid)
    {
        var ok = CatalogMinGroupSizeRules.TryParseMinGroupSize(invalid, out var value, out var error, allowNone: false);
        ok.ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void Ingest_NoneKeyword_Rejected()
    {
        var ok = CatalogMinGroupSizeRules.TryParseMinGroupSize("none", out var value, out var error, allowNone: false);
        ok.ShouldBeFalse();
        value.ShouldBeNull();
    }

    [Fact]
    public void Governance_NoneKeyword_Accepted()
    {
        var ok = CatalogMinGroupSizeRules.TryParseMinGroupSize("none", out var value, out var error, allowNone: true);
        ok.ShouldBeTrue();
        value.ShouldBeNull();
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("5", 5)]
    [InlineData("1000000", 1000000)]
    public void TryParseMinGroupSize_ValidIntegers_Succeed(string input, int expected)
    {
        var ok = CatalogMinGroupSizeRules.TryParseMinGroupSize(input, out var value, out var error, allowNone: false);
        ok.ShouldBeTrue();
        value.ShouldBe(expected);
        error.ShouldBeNull();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public void TryParseNonPersonal_Valid_Succeeds(string input, bool expected)
    {
        var ok = CatalogMinGroupSizeRules.TryParseNonPersonal(input, out var value);
        ok.ShouldBeTrue();
        value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData(" true")]
    [InlineData("2")]
    [InlineData("t")]
    public void TryParseNonPersonal_Invalid_Fails(string input)
    {
        var ok = CatalogMinGroupSizeRules.TryParseNonPersonal(input, out var value);
        ok.ShouldBeFalse();
        value.ShouldBeNull();
    }
}

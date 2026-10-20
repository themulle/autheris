namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using Autheris.Application.Catalog.Localization;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class LanguageNegotiationTests
{
    private readonly LanguageNegotiator _negotiator = new();
    private static readonly string[] Supported = ["de", "en", "fr", "de-CH"];

    [Theory]
    [InlineData("de", "de")]
    [InlineData("en", "en")]
    [InlineData("fr", "fr")]
    [InlineData("de-CH", "de-CH")]
    public void Negotiate_ExactMatch_ReturnsMatchingLanguage(string requested, string expected)
    {
        var result = _negotiator.NegotiateLanguage(requested, Supported, "en");
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("de-AT", "de")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-CA", "fr")]
    public void Negotiate_SubtagTruncation_FallsBackToPrimarySubtag(string requested, string expected)
    {
        var result = _negotiator.NegotiateLanguage(requested, Supported, "en");
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData("DE", "de")]
    [InlineData("EN-us", "en")]
    [InlineData("DE-ch", "de-CH")]
    public void Negotiate_CaseInsensitive_MatchesCanonicalTag(string requested, string expected)
    {
        var result = _negotiator.NegotiateLanguage(requested, Supported, "en");
        result.ShouldBe(expected);
    }

    [Fact]
    public void Negotiate_QualityWeights_SelectsHighestWeightSupported()
    {
        var header = "fr;q=0.4, de;q=0.9, en;q=0.7";
        var result = _negotiator.NegotiateLanguage(header, Supported, "en");
        result.ShouldBe("de");
    }

    [Fact]
    public void Negotiate_QualityWeights_WhenHighestUnsupported_FallsBackToNextSupported()
    {
        var header = "es;q=1.0, it;q=0.9, fr;q=0.8, de;q=0.5";
        var result = _negotiator.NegotiateLanguage(header, Supported, "en");
        result.ShouldBe("fr");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ja, zh;q=0.9")]
    public void Negotiate_NoMatchOrEmpty_ReturnsDefaultLanguage(string? requested)
    {
        var result = _negotiator.NegotiateLanguage(requested, Supported, "en");
        result.ShouldBe("en");
    }

    [Fact]
    public void Negotiate_Wildcard_SelectsDefaultOrFirstSupported()
    {
        var result = _negotiator.NegotiateLanguage("*", Supported, "de");
        result.ShouldBe("de");
    }

    [Fact]
    public void Bcp47LanguageTag_TryCanonicalize_ValidTags_Succeeds()
    {
        Bcp47LanguageTag.TryCanonicalize("de-de", out var de).ShouldBeTrue();
        de.ShouldBe("de-DE");

        Bcp47LanguageTag.TryCanonicalize("en-us", out var en).ShouldBeTrue();
        en.ShouldBe("en-US");

        Bcp47LanguageTag.TryCanonicalize("zh-hans-cn", out var zh).ShouldBeTrue();
        zh.ShouldBe("zh-Hans-CN");
    }

    [Fact]
    public void Bcp47LanguageTag_TryCanonicalize_InvalidTags_FailsClosed()
    {
        Bcp47LanguageTag.TryCanonicalize("", out _).ShouldBeFalse();
        Bcp47LanguageTag.TryCanonicalize("invalid_tag_with_underscore", out _).ShouldBeFalse();
        Bcp47LanguageTag.TryCanonicalize("1234567890", out _).ShouldBeFalse();
        Bcp47LanguageTag.TryCanonicalize("toolongsubtagnamehere", out _).ShouldBeFalse();
    }
}

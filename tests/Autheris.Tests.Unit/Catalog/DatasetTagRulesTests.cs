namespace Autheris.Tests.Unit.Catalog;

using System.Collections.Generic;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Issue #111 T-1 items 1-2: tag normalization, limits, stored-visibility mapping and visibility parsing.</summary>
public sealed class DatasetTagRulesTests
{
    [Theory]
    [InlineData("PII", "pii")]
    [InlineData("  Finance.Core  ", "finance.core")]
    [InlineData("a:b_c-d.e", "a:b_c-d.e")]
    public void TryNormalize_TrimsAndLowerCases(string raw, string expected)
    {
        DatasetTagRules.TryNormalize(raw, out var tag).ShouldBeTrue();
        tag.ShouldBe(expected);
    }

    [Fact]
    public void TryNormalize_LengthBoundary_64Ok_65Rejected()
    {
        DatasetTagRules.TryNormalize(new string('a', 64), out _).ShouldBeTrue();
        DatasetTagRules.TryNormalize(new string('a', 65), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("a;drop")]
    [InlineData("<b>")]
    [InlineData("a\u0000b")]
    [InlineData("a\nb")]
    [InlineData("аdmin")] // Cyrillic a look-alike
    [InlineData("café")]
    public void TryNormalize_RejectsIllegalInput(string? raw)
    {
        DatasetTagRules.TryNormalize(raw, out var tag).ShouldBeFalse();
        tag.ShouldBe(string.Empty);
    }

    [Fact]
    public void TryNormalizeSet_MergesDuplicatesCaseInsensitively()
    {
        DatasetTagRules.TryNormalizeSet(["PII", "pii", " Pii ", "x"], 32, out var tags, out var error).ShouldBeTrue();
        error.ShouldBe(string.Empty);
        tags.ShouldBe(["pii", "x"]);
    }

    [Fact]
    public void TryNormalizeSet_LimitBoundary_ConfiguredCapRespected()
    {
        var ok = new List<string?>();
        for (var i = 0; i < 32; i++)
        {
            ok.Add("t" + i);
        }

        DatasetTagRules.TryNormalizeSet(ok, 32, out _, out _).ShouldBeTrue();

        ok.Add("t32");
        DatasetTagRules.TryNormalizeSet(ok, 32, out _, out var error).ShouldBeFalse();
        error.ShouldBe(DatasetTagRules.TagLimitExceeded);

        DatasetTagRules.TryNormalizeSet(ok, 33, out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void TryNormalizeSet_InvalidTag_ReportsInvalidTag()
    {
        DatasetTagRules.TryNormalizeSet(["ok", "bad tag"], 32, out _, out var error).ShouldBeFalse();
        error.ShouldBe(DatasetTagRules.InvalidTag);
    }

    [Fact]
    public void TryNormalizeSet_NullEntry_IsInvalid()
    {
        DatasetTagRules.TryNormalizeSet(["a", null], 32, out _, out var error).ShouldBeFalse();
        error.ShouldBe(DatasetTagRules.InvalidTag);
    }



}

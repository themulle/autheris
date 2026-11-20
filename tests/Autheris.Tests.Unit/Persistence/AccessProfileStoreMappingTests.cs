using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence;

/// <summary>The masking mode of a profile is stored as its PII classification level; every read that cannot be mapped exactly resolves to the most restrictive mode.</summary>
[Trait("Category", "Security")]
public sealed class AccessProfileStoreMappingTests
{
    [Theory]
    [InlineData(MaskingPolicyMode.Strict, 0)]
    [InlineData(MaskingPolicyMode.Default, 1)]
    [InlineData(MaskingPolicyMode.Unmasked, 2)]
    public void Mapping_RoundTrips_AllDefinedModes(MaskingPolicyMode mode, short level)
    {
        AccessProfileStore.ToAccessLevel(mode).ShouldBe(level);
        AccessProfileStore.ToMaskingMode([("PII", level)]).ShouldBe(mode);
    }

    [Fact]
    public void MissingPiiRow_ResolvesStrict() => AccessProfileStore.ToMaskingMode([]).ShouldBe(MaskingPolicyMode.Strict);

    [Theory]
    [InlineData("pii")]
    [InlineData("OTHER")]
    [InlineData("")]
    public void NonPiiRow_ResolvesStrict(string code) => AccessProfileStore.ToMaskingMode([(code, 2)]).ShouldBe(MaskingPolicyMode.Strict);

    [Fact]
    public void ExtraRowBesideAPiiRow_ResolvesStrict() =>
        AccessProfileStore.ToMaskingMode([("PII", 2), ("OTHER", 2)]).ShouldBe(MaskingPolicyMode.Strict);

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(7)]
    public void OutOfRangeLevel_ResolvesStrict(short level) => AccessProfileStore.ToMaskingMode([("PII", level)]).ShouldBe(MaskingPolicyMode.Strict);

    [Fact]
    public void DuplicatePiiRowsWithMixedLevels_ResolvesStrict() =>
        AccessProfileStore.ToMaskingMode([("PII", 2), ("PII", 1)]).ShouldBe(MaskingPolicyMode.Strict);

    [Fact]
    public void ToAccessLevel_UndefinedMode_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => AccessProfileStore.ToAccessLevel((MaskingPolicyMode)7));

    [Fact]
    public void DistinctItems_KeepsFirstOccurrenceVerbatim_AndRefusesBlankOrOverlongEntries()
    {
        AccessProfileStore.DistinctItems(["b.t", "A.*", "b.t", "a.*", "A.* "]).ShouldBe(["b.t", "A.*", "a.*"]);
        Should.Throw<ArgumentException>(() => AccessProfileStore.DistinctItems(["a.b", " "]));
        var over = new string('x', 513);
        var ex = Should.Throw<ArgumentException>(() => AccessProfileStore.DistinctItems([over]));
        ex.Message.ShouldNotContain("xxxx");
        AccessProfileStore.DistinctItems([new string('x', 512)]).Count.ShouldBe(1);
    }
}

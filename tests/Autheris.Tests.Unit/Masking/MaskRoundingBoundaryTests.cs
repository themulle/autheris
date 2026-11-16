using Autheris.Application.Services;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Masking;

/// <summary>
/// .NET 11 makes <c>Math.Round</c> and the decimal/BigInteger conversions correctly rounded. Masking precision must not change at
/// the .5 boundaries: exactly representable midpoints round away from zero, and the result never carries more digits than configured.
/// </summary>
[Trait("Category", "Security")]
public sealed class MaskRoundingBoundaryTests
{
    private static double Round(double value, int decimals) =>
        (double)new ColumnMaskingProvider().MaskValue("v", value, new MaskingRule { RuleType = "GEO_JITTER", Decimals = decimals })!;

    [Theory]
    [InlineData(0.5, 0, 1.0)]
    [InlineData(1.5, 0, 2.0)]
    [InlineData(2.5, 0, 3.0)]
    [InlineData(-0.5, 0, -1.0)]
    [InlineData(-2.5, 0, -3.0)]
    [InlineData(0.125, 2, 0.13)]
    [InlineData(0.375, 2, 0.38)]
    [InlineData(-0.125, 2, -0.13)]
    [InlineData(48.3, 2, 48.3)]
    public void GeoRound_ExactMidpoints_RoundAwayFromZero(double input, int decimals, double expected)
    {
        Round(input, decimals).ShouldBe(expected);
    }

    [Theory]
    [InlineData(48.298198, 0)]
    [InlineData(48.298198, 1)]
    [InlineData(48.298198, 2)]
    [InlineData(48.298198, 4)]
    [InlineData(-122.419416, 3)]
    [InlineData(48.285, 2)]
    [InlineData(1.15, 1)]
    public void GeoRound_NeverCarriesMoreDigitsThanConfigured(double input, int decimals)
    {
        var rounded = Round(input, decimals);

        var scaled = rounded * Math.Pow(10, decimals);
        Math.Abs(scaled - Math.Round(scaled)).ShouldBeLessThan(1e-6);
        Math.Abs(rounded - input).ShouldBeLessThanOrEqualTo(0.5 * Math.Pow(10, -decimals) + 1e-9);
    }
}

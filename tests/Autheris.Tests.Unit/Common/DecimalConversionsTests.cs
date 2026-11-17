using Autheris.Domain.Common;
using Shouldly;
using TrinoSqlEngine.Numerics;
using Xunit;

namespace Autheris.Tests.Unit.Common;

/// <summary>
/// .NET 11 converts double and float to decimal exactly ((decimal)0.8d is 0.8000000000000000444...). Quota thresholds, filter values
/// and key sets written as decimal text must keep matching, so the gateway converts with the 15 and 7 significant digit rounding.
/// </summary>
[Trait("Category", "Security")]
public sealed class DecimalConversionsTests
{
    [Theory]
    [InlineData(0.8, "0.8")]
    [InlineData(0.1, "0.1")]
    [InlineData(-0.1, "-0.1")]
    [InlineData(123456.789, "123456.789")]
    [InlineData(1e-10, "0.0000000001")]
    [InlineData(0.0, "0")]
    [InlineData(100.0, "100")]
    [InlineData(2.675, "2.675")]
    public void FromDouble_RoundsToFifteenSignificantDigits(double input, string expected)
    {
        var result = DecimalConversions.FromDouble(input);

        result.ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        result.ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0.1f, "0.1")]
    [InlineData(0.8f, "0.8")]
    [InlineData(1.5f, "1.5")]
    public void FromSingle_RoundsToSevenSignificantDigits(float input, string expected)
    {
        DecimalConversions.FromSingle(input).ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
    }

    [Fact]
    public void FromDouble_OutOfRange_ThrowsLikeTheCast()
    {
        Should.Throw<OverflowException>(() => DecimalConversions.FromDouble(1e30));
    }

    [Fact]
    public void FromDouble_HugeValueInRange_IsKeptExact()
    {
        DecimalConversions.FromDouble(1e20).ShouldBe(100000000000000000000m);
    }

    [Fact]
    public void ToDecimal_UsesTheRoundingForBinaryFloatingPointAndConvertForTheRest()
    {
        DecimalConversions.ToDecimal(0.8d).ShouldBe(0.8m);
        DecimalConversions.ToDecimal(0.1f).ShouldBe(0.1m);
        DecimalConversions.ToDecimal(42).ShouldBe(42m);
        DecimalConversions.ToDecimal("1.25").ShouldBe(1.25m);
        DecimalConversions.ToDecimal(3.5m).ShouldBe(3.5m);
    }

    [Theory]
    [InlineData(0.8d, "0.8")]
    [InlineData(0.1d, "0.1")]
    [InlineData(-2.675d, "-2.675")]
    [InlineData(0d, "0")]
    public void SqlBinderConversion_MatchesTheGatewayRounding_ForDouble(double input, string expected)
    {
        BinaryFloatingPointDecimal.ToDecimal(input).ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe(expected);
        BinaryFloatingPointDecimal.ToDecimal(0.1f).ToString(System.Globalization.CultureInfo.InvariantCulture).ShouldBe("0.1");
        BinaryFloatingPointDecimal.ToDecimal(7).ShouldBe(7m);
        BinaryFloatingPointDecimal.ToDecimal(1e20d).ShouldBe(100000000000000000000m);
    }
}

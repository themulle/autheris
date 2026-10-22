using Autheris.Application.Data.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

/// <summary>MISC-10: the k-anonymity floor cannot be lowered to 1 (or below) without AllowDisabled, in any of the three places it is set.</summary>
[Trait("Category", "Security")]
public sealed class AnalyticsMinGroupSizeValidationTests
{
    private static bool Fails(AnalyticsOptions options) => new AnalyticsOptionsValidator().Validate(null, options).Failed;

    [Fact]
    public void DefaultOptions_Pass_MISC10() => Fails(new AnalyticsOptions()).ShouldBeFalse();

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void DefaultMinGroupSizeBelowTwo_WithoutAllowDisabled_Fails_MISC10(int value)
    {
        var options = new AnalyticsOptions();
        options.Privacy.DefaultMinGroupSize = value;

        Fails(options).ShouldBeTrue();
    }

    [Fact]
    public void DefaultMinGroupSizeOne_WithAllowDisabled_Passes_MISC10()
    {
        var options = new AnalyticsOptions();
        options.Privacy.AllowDisabled = true;
        options.Privacy.DefaultMinGroupSize = 1;

        Fails(options).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void TableOverrideBelowTwo_Fails_MISC10(int value)
    {
        var options = new AnalyticsOptions();
        options.TableMinimumGroupSizes["patients"] = value;

        Fails(options).ShouldBeTrue();
    }

    [Fact]
    public void DatasetOverrideBelowTwo_Fails_MISC10()
    {
        var options = new AnalyticsOptions();
        options.Privacy.Datasets["patients"] = new DatasetPrivacyOverrideOptions { MinGroupSize = 1 };

        Fails(options).ShouldBeTrue();
    }
}

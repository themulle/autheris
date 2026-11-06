namespace Autheris.Tests.Unit.Data;

using Autheris.Application.DataSources.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AnalyticsOptionsValidatorCatalogFloorTests
{
    private static bool Fails(AnalyticsOptions options) => new AnalyticsOptionsValidator().Validate(null, options).Failed;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void CatalogMinGroupSizeFloor_OutsideRange_Fails(int floor)
    {
        var options = new AnalyticsOptions();
        options.Privacy.CatalogMinGroupSizeFloor = floor;
        Fails(options).ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(100)]
    [InlineData(1_000_000)]
    public void CatalogMinGroupSizeFloor_InsideRange_Passes(int floor)
    {
        var options = new AnalyticsOptions();
        options.Privacy.CatalogMinGroupSizeFloor = floor;
        Fails(options).ShouldBeFalse();
    }
}

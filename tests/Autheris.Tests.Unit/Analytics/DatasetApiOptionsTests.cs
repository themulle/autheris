using Autheris.Application.Data.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

/// <summary>#302 (SEC-RG-13): the REST offset cap defaults to 10000 (the GraphQL cap) and a non-positive or oversized value fails startup validation.</summary>
public sealed class DatasetApiOptionsTests
{
    private static bool Fails(int maxOffset) =>
        new AnalyticsOptionsValidator().Validate(null, new AnalyticsOptions { DatasetApi = new DatasetApiOptions { MaxOffset = maxOffset } }).Failed;

    [Fact]
    public void MaxOffset_DefaultIs10000() => new DatasetApiOptions().MaxOffset.ShouldBe(10_000);

    [Fact]
    public void MaxOffset_DefaultEqualsGraphQlCap() =>
        new DatasetApiOptions().MaxOffset.ShouldBe(new GraphQLOptions().MaxAllowedOffset);

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(DatasetApiOptions.UpperBoundMaxOffset + 1)]
    [InlineData(int.MaxValue)]
    public void MaxOffsetNonPositive_FailsValidation(int value) => Fails(value).ShouldBeTrue();

    [Theory]
    [InlineData(1)]
    [InlineData(10_000)]
    [InlineData(DatasetApiOptions.UpperBoundMaxOffset)]
    public void MaxOffsetInRange_Passes(int value) => Fails(value).ShouldBeFalse();
}

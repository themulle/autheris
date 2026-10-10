namespace Autheris.Tests.Unit.Catalog;

using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class TableSensitivityTests
{
    [Theory]
    [InlineData("PUBLIC", 1)]
    [InlineData("1_PUBLIC", 1)]
    [InlineData("1_public", 1)]
    [InlineData("INTERNAL", 2)]
    [InlineData("2_INTERNAL", 2)]
    [InlineData("2_internal", 2)]
    [InlineData("NORMAL", 2)]
    [InlineData("CONFIDENTIAL", 3)]
    [InlineData("3_CONFIDENTIAL", 3)]
    [InlineData("3_confidential", 3)]
    [InlineData("RESTRICTED", 3)]
    [InlineData("SECRET", 4)]
    [InlineData("4_SECRET", 4)]
    [InlineData("4_secret", 4)]
    [InlineData("STRICTLY_CONFIDENTIAL", 4)]
    [InlineData("UNKNOWN_CUSTOM", 2)]
    public void SensitivityRank_StripsNumericPrefixes_AndRanksCorrectly(string sensitivity, int expectedRank)
    {
        Table.SensitivityRank(sensitivity).ShouldBe(expectedRank);
    }

    [Theory]
    [InlineData("PUBLIC", false)]
    [InlineData("1_public", false)]
    [InlineData("INTERNAL", false)]
    [InlineData("2_internal", false)]
    [InlineData("CONFIDENTIAL", true)]
    [InlineData("3_confidential", true)]
    [InlineData("SECRET", true)]
    [InlineData("4_secret", true)]
    public void IsSensitivityHigh_ReturnsTrue_OnlyForRank3AndAbove(string sensitivity, bool expectedHigh)
    {
        Table.IsSensitivityHigh(sensitivity).ShouldBe(expectedHigh);
    }
}

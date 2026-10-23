using Autheris.Application.Sql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

/// <summary>SQLA-04: an unbounded MaxAllowedRows (0) still caps an explicit LIMIT at an internal ceiling.</summary>
public sealed class SqlaRowLimitTests
{
    [Fact]
    public void Effective_NoBoundConfigured_CapsHugeExplicitLimit()
    {
        var limit = new SqlRowLimit(1000, 0);

        limit.Effective(long.MaxValue).ShouldBe(SqlRowLimit.HardMaxRows);
        limit.Effective(100_000_000).ShouldBe(SqlRowLimit.HardMaxRows);
        (limit.Effective(long.MaxValue) + 1).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Effective_NoBoundConfigured_KeepsSmallExplicitLimit() =>
        new SqlRowLimit(1000, 0).Effective(5000).ShouldBe(5000);

    [Fact]
    public void Effective_ConfiguredBound_StillWins() =>
        new SqlRowLimit(1000, 10_000).Effective(long.MaxValue).ShouldBe(10_000);
}

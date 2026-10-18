namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Shouldly;
using Xunit;

/// <summary>Issue #107: SEC-RF107-09 (a) counts-only <c>ToString</c>, SEC-RF107-13 (a) size estimate.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetEntryTests
{
    private static RowFilterKeySetEntry Entry(params object[] keys)
        => new(keys, false, DateTimeOffset.UnixEpoch, "tenant", "ds");

    [Fact]
    public void ToString_CountsOnly_SEC_RF107_09_a()
    {
        var text = new RowFilterKeySetEntry(["secret-key-4711", 4711L, 4711.5m], false, DateTimeOffset.UnixEpoch, "tenant-secret", "ds-secret").ToString();

        text.ShouldContain("Keys = 3");
        text.ShouldNotContain("4711");
        text.ShouldNotContain("tenant-secret");
        text.ShouldNotContain("ds-secret");
    }

    [Fact]
    public void EstimateBytes_CountsStringsAndNumbersPerKey_SEC_RF107_13_a()
    {
        long empty = Entry().EstimateBytes();

        (Entry("abcde").EstimateBytes() - empty).ShouldBe(8 + 2 * 5 + 40);       // reference slot + string
        (Entry(1L).EstimateBytes() - empty).ShouldBe(8 + 24);                   // reference slot + boxed numeric
        (Entry(1m).EstimateBytes() - empty).ShouldBe(8 + 24);
        empty.ShouldBeGreaterThanOrEqualTo(2 * 64 + 40);                        // at least the 64 character cache key
    }

    [Fact]
    public void EstimateBytes_CountsTenantAndDataSource()
    {
        var small = new RowFilterKeySetEntry([], false, DateTimeOffset.UnixEpoch, "t", "d").EstimateBytes();
        var large = new RowFilterKeySetEntry([], false, DateTimeOffset.UnixEpoch, new string('t', 64), new string('d', 100)).EstimateBytes();

        (large - small).ShouldBe(2 * (63 + 99));
    }

    [Fact]
    public void EstimateBytes_GrowsLinearlyWithTheKeyCount()
    {
        var keys = Enumerable.Range(0, 1000).Select(i => (object)(long)i).ToArray();

        (Entry(keys).EstimateBytes() - Entry().EstimateBytes()).ShouldBe(1000 * (8 + 24));
    }

    [Fact]
    public void OverflowSentinel_IsSmall()
    {
        var sentinel = RowFilterKeySetEntry.OverflowSentinel(DateTimeOffset.UnixEpoch, "tenant", "ds");

        sentinel.Overflow.ShouldBeTrue();
        sentinel.Keys.ShouldBeEmpty();
        sentinel.EstimateBytes().ShouldBeLessThan(1024);
    }
}

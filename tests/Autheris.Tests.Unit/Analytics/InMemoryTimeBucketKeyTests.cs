using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Analytics;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

public sealed class InMemoryTimeBucketKeyTests
{
    [Fact]
    public void InMemory_DayBucket_BerlinDstChangeDay_OneKeyWithStartOffset()
    {
        var spec = new TimeBucketSpecification("ts", TimeBucketInterval.Day, "Europe/Berlin");
        var val = new DateTime(2026, 10, 25, 14, 30, 0, DateTimeKind.Unspecified);
        var key = GovernedAggregationService.TruncateValueToTimeBucket(val, spec);
        key.ShouldBe("2026-10-25T00:00:00+02:00");
    }

    [Fact]
    public void InMemory_HourFold_TwoDistinctKeys()
    {
        var spec = new TimeBucketSpecification("ts", TimeBucketInterval.Hour, "Europe/Berlin");
        // 2026-10-25 00:30:00 UTC is 02:30:00 +02:00 (first fold)
        // 2026-10-25 01:30:00 UTC is 02:30:00 +01:00 (second fold)
        var key1 = GovernedAggregationService.TruncateValueToTimeBucket(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), spec);
        var key2 = GovernedAggregationService.TruncateValueToTimeBucket(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), spec);

        key1.ShouldBe("2026-10-25T02:00:00+02:00");
        key2.ShouldBe("2026-10-25T02:00:00+01:00");
        key1.ShouldNotBe(key2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void InMemory_OffsetlessString_IsUtcNotServerLocal()
    {
        var spec = new TimeBucketSpecification("ts", TimeBucketInterval.Hour, "UTC");
        var key = GovernedAggregationService.TruncateValueToTimeBucket("2026-06-01 10:30:00", spec);
        key.ShouldBe("2026-06-01T10:00:00+00:00");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void InMemory_UnknownZone_FailsClosed()
    {
        var spec = new TimeBucketSpecification("ts", TimeBucketInterval.Day, "Mars/Olympus");
        Should.Throw<ArgumentException>(() => GovernedAggregationService.TruncateValueToTimeBucket(DateTime.UtcNow, spec));
    }

    [Fact]
    public void Buckets_OrderByBucketKey_FoldHours_Chronological()
    {
        var firstFold = "2026-10-25T02:00:00+02:00";
        var secondFold = "2026-10-25T02:00:00+01:00";

        var c1 = GovernedAggregationService.NormalizeComparable(firstFold);
        var c2 = GovernedAggregationService.NormalizeComparable(secondFold);

        c1.ShouldBeOfType<long>();
        c2.ShouldBeOfType<long>();
        ((long)c1!).ShouldBeLessThan((long)c2!);

        var list = new[] { secondFold, firstFold };
        var sorted = list.OrderBy(k => GovernedAggregationService.NormalizeComparable(k)).ToList();
        sorted.ShouldBe([firstFold, secondFold]);
    }

    [Fact]
    public void InMemory_NonTemporalValue_TimeBucketUnsupported()
    {
        var spec = new TimeBucketSpecification("ts", TimeBucketInterval.Day, "UTC");
        Should.Throw<AggregationTimeBucketUnsupportedException>(() =>
            GovernedAggregationService.TruncateValueToTimeBucket(12345, spec));
        Should.Throw<AggregationTimeBucketUnsupportedException>(() =>
            GovernedAggregationService.TruncateValueToTimeBucket("not-a-date", spec));
    }
}

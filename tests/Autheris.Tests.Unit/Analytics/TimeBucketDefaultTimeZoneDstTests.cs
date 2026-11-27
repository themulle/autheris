using Autheris.Application.Analytics;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.GraphQL;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

/// <summary>#1190 / #326: a bucket whose zone comes from the configured default behaves exactly like an explicit zone, across DST transitions.</summary>
public sealed class TimeBucketDefaultTimeZoneDstTests
{
    private static TimeBucketSpecification DefaultSpec(TimeBucketInterval interval)
    {
        var bucket = new HotChocolate.Language.ObjectValueNode(
            new HotChocolate.Language.ObjectFieldNode("column", new HotChocolate.Language.EnumValueNode("created_at")),
            new HotChocolate.Language.ObjectFieldNode("bucket", new HotChocolate.Language.EnumValueNode(interval == TimeBucketInterval.Hour ? "HOUR" : "DAY")));
        return TimeBucketDefaultTimeZoneParserTests.ParseTimeBuckets(bucket, new AggregationOptions { DefaultTimeZone = "Europe/Berlin" }).TimeBuckets![0];
    }

    private static TimeBucketSpecification ExplicitSpec(TimeBucketInterval interval) =>
        new("created_at", interval, "Europe/Berlin");

    [Theory]
    [InlineData(TimeBucketInterval.Hour)]
    [InlineData(TimeBucketInterval.Day)]
    public void DefaultBerlinSpec_EqualsExplicitBerlinSpec(TimeBucketInterval interval) =>
        DefaultSpec(interval).ShouldBe(ExplicitSpec(interval));

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "timestamptz", TimeBucketInterval.Hour)]
    [InlineData(DatabaseDialect.PostgreSql, "timestamptz", TimeBucketInterval.Day)]
    [InlineData(DatabaseDialect.PostgreSql, "timestamp", TimeBucketInterval.Hour)]
    [InlineData(DatabaseDialect.PostgreSql, "timestamp", TimeBucketInterval.Day)]
    [InlineData(DatabaseDialect.SqlServer, "datetimeoffset", TimeBucketInterval.Hour)]
    [InlineData(DatabaseDialect.SqlServer, "datetimeoffset", TimeBucketInterval.Day)]
    public void Planner_DefaultBerlin_SameExpressionAsExplicit(DatabaseDialect dialect, string dataType, TimeBucketInterval interval)
    {
        var fromDefault = GovernedTableReadPlanner.BuildTimeBucketExpression("created_at", DefaultSpec(interval), dialect, dataType);
        var fromExplicit = GovernedTableReadPlanner.BuildTimeBucketExpression("created_at", ExplicitSpec(interval), dialect, dataType);

        fromDefault.ShouldBe(fromExplicit);
        if (interval == TimeBucketInterval.Day)
        {
            fromDefault.SelectExpr.ShouldContain("Europe/Berlin");
        }
    }

    [Fact]
    public void InMemory_SpringForward20260329_DefaultEqualsExplicit()
    {
        foreach (var (utc, interval, expected) in new[]
        {
            (new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero), TimeBucketInterval.Hour, "2026-03-29T01:00:00+01:00"),
            (new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), TimeBucketInterval.Hour, "2026-03-29T03:00:00+02:00"),
            (new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), TimeBucketInterval.Day, "2026-03-29T00:00:00+01:00")
        })
        {
            GovernedAggregationService.TruncateValueToTimeBucket(utc, DefaultSpec(interval)).ShouldBe(expected);
            GovernedAggregationService.TruncateValueToTimeBucket(utc, ExplicitSpec(interval)).ShouldBe(expected);
        }
    }

    [Fact]
    public void InMemory_FallBack20261025_DefaultEqualsExplicit()
    {
        foreach (var (utc, interval, expected) in new[]
        {
            (new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), TimeBucketInterval.Hour, "2026-10-25T02:00:00+02:00"),
            (new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), TimeBucketInterval.Hour, "2026-10-25T02:00:00+01:00"),
            (new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), TimeBucketInterval.Day, "2026-10-25T00:00:00+02:00")
        })
        {
            GovernedAggregationService.TruncateValueToTimeBucket(utc, DefaultSpec(interval)).ShouldBe(expected);
            GovernedAggregationService.TruncateValueToTimeBucket(utc, ExplicitSpec(interval)).ShouldBe(expected);
        }
    }

    [Theory]
    [InlineData(TimeBucketInterval.Hour)]
    [InlineData(TimeBucketInterval.Day)]
    public void Planner_DefaultBerlin_UnsupportedDialect_RefusedFailClosed(TimeBucketInterval interval)
    {
        var ex = Should.Throw<AggregationTimeBucketUnsupportedException>(() =>
            GovernedTableReadPlanner.BuildTimeBucketExpression("created_at", DefaultSpec(interval), DatabaseDialect.Databricks));

        ex.Message.ShouldBe(AggregationTimeBucketUnsupportedException.FixedMessage);
    }
}

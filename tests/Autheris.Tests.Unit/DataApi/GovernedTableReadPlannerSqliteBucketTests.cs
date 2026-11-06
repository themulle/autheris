using System.Globalization;
using Autheris.Application.Analytics;
using Autheris.Application.DataSources.Services;
using Autheris.Domain.Common;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.DataApi;

/// <summary>#325 (GAP-034) QUARTER bucket and #326 (GAP-035) DST-aware HOUR bucket on SQLite, run against an in-memory database.</summary>
public sealed class GovernedTableReadPlannerSqliteBucketTests
{
    private static List<string> Keys(TimeBucketInterval interval, string timeZone, params string[] utcValues)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE t (ts TEXT NOT NULL)";
            create.ExecuteNonQuery();
        }

        foreach (var value in utcValues)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO t (ts) VALUES ($v)";
            insert.Parameters.AddWithValue("$v", value);
            insert.ExecuteNonQuery();
        }

        var (_, groupBy, _) = GovernedTableReadPlanner.BuildTimeBucketExpression("ts", new TimeBucketSpecification("ts", interval, timeZone), DatabaseDialect.Sqlite);
        using var select = connection.CreateCommand();
        select.CommandText = $"SELECT {groupBy} FROM t ORDER BY rowid";
        using var reader = select.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
        {
            keys.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return keys;
    }

    [Fact]
    public void SqliteQuarterBucket_FirstDayOfQuarter()
    {
        var keys = Keys(
            TimeBucketInterval.Quarter,
            "UTC",
            "2025-01-15 10:00:00",
            "2025-03-31 23:59:59",
            "2025-04-01 00:00:00",
            "2025-05-20 08:00:00",
            "2025-08-31 12:00:00",
            "2025-10-01 00:00:00",
            "2025-12-31 23:00:00");

        keys.ShouldBe(["2025-01-01", "2025-01-01", "2025-04-01", "2025-04-01", "2025-07-01", "2025-10-01", "2025-10-01"]);
    }

    [Fact]
    public void SqliteQuarterBucket_WithTimeZone_UsesTheLocalDate()
    {
        // 2025-03-31 23:30 UTC is already 2025-04-01 in Berlin (summer time starts on 2025-03-30).
        Keys(TimeBucketInterval.Quarter, "Europe/Berlin", "2025-03-31 23:30:00", "2025-03-31 21:30:00")
            .ShouldBe(["2025-04-01", "2025-01-01"]);
    }

    [Fact]
    public void SqliteMonthBucket_Unchanged()
    {
        Keys(TimeBucketInterval.Month, "UTC", "2025-02-14 10:00:00").ShouldBe(["2025-02-01"]);
    }

    [Fact]
    public void SqliteBucket_Week_ReturnsMondayDate()
    {
        // 2025-01-15 is Wednesday. Monday of that week is 2025-01-13.
        Keys(TimeBucketInterval.Week, "UTC", "2025-01-15 10:00:00").ShouldBe(["2025-01-13"]);
    }

    [Fact]
    public void SqliteBucket_Hour_ReturnsZoneAlignedUtcInstant()
    {
        // Winter: UTC+1. Instant 10:30 UTC -> 10:00 UTC.
        // Summer: UTC+2. Instant 15:30 UTC -> 15:00 UTC.
        var keys = Keys(TimeBucketInterval.Hour, "Europe/Berlin", "2025-01-15 10:30:00", "2025-07-15 15:30:00");

        keys.ShouldBe(["2025-01-15 10:00:00", "2025-07-15 15:00:00"]);
    }

    [Fact]
    public void SqliteHourBucketBerlin_ChangeoverInstants()
    {
        // 2025-03-30 01:00 UTC switches Berlin from +01:00 to +02:00; 2025-10-26 01:00 UTC switches back.
        // Fold hours on 2025-10-26 stay distinct UTC instants: 00:00:00 and 01:00:00.
        Keys(
                TimeBucketInterval.Hour,
                "Europe/Berlin",
                "2025-03-30 00:59:59",
                "2025-03-30 01:00:00",
                "2025-10-26 00:59:59",
                "2025-10-26 01:00:00")
            .ShouldBe(["2025-03-30 00:00:00", "2025-03-30 01:00:00", "2025-10-26 00:00:00", "2025-10-26 01:00:00"]);
    }

    [Fact]
    public void SqliteHourBucket_HalfHourZone()
    {
        Keys(TimeBucketInterval.Hour, "Asia/Kolkata", "2025-07-15 10:10:00").ShouldBe(["2025-07-15 09:30:00"]);
    }

    [Fact]
    public void SqliteBucket_DateColumn_IgnoresZone()
    {
        var (select, groupBy, shape) = GovernedTableReadPlanner.BuildTimeBucketExpression(
            "d", new TimeBucketSpecification("d", TimeBucketInterval.Day, "Europe/Berlin"), DatabaseDialect.Sqlite, "date");
        shape.ShouldBe(Autheris.Application.DataSources.Interfaces.TimeBucketKeyShape.Date);
        groupBy.ShouldBe("strftime('%Y-%m-%d', \"d\")");
        groupBy.ShouldNotContain("datetime");
    }
}

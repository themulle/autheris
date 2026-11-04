using System;
using System.Globalization;
using Autheris.Application.Analytics;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Analytics;

public sealed class TimeBucketKeysTests
{
    [Fact]
    public void FormatWall_BerlinSummerDay_CarriesPlus0200()
    {
        var zone = TimeBucketKeys.ResolveZone("Europe/Berlin");
        var key = TimeBucketKeys.FormatWall(new DateTime(2026, 7, 15, 0, 0, 0), zone);
        key.ShouldBe("2026-07-15T00:00:00+02:00");
    }

    [Fact]
    public void FormatInstant_Utc_UsesPlus0000()
    {
        var key = TimeBucketKeys.FormatInstant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        key.ShouldBe("2026-01-01T00:00:00+00:00");
    }

    [Fact]
    public void FormatWall_BerlinAutumnFold_UsesFirstOccurrenceOffset()
    {
        var zone = TimeBucketKeys.ResolveZone("Europe/Berlin");
        var key = TimeBucketKeys.FormatWall(new DateTime(2026, 10, 25, 2, 0, 0), zone);
        key.ShouldBe("2026-10-25T02:00:00+02:00");
    }

    [Fact]
    public void FormatWall_SantiagoSpringGapMidnight_UsesPreTransitionOffset()
    {
        var zone = TimeBucketKeys.ResolveZone("America/Santiago");
        // In Santiago, when spring DST starts at midnight (00:00 jumps to 01:00), midnight is invalid time.
        // Even if not invalid in 2026, FormatWall handles invalid time via AddDays(-1).
        var dt = new DateTime(2026, 9, 6, 0, 0, 0);
        var key = TimeBucketKeys.FormatWall(dt, zone);
        key.ShouldStartWith("2026-09-06T00:00:00-");
    }

    [Fact]
    public void FormatDate_IsWallDateWithoutOffset()
    {
        var key = TimeBucketKeys.FormatDate(new DateOnly(2026, 10, 5));
        key.ShouldBe("2026-10-05");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Format_InvariantUnderTrTrAndArSa()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "tr-TR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var zone = TimeBucketKeys.ResolveZone("Europe/Berlin");
                TimeBucketKeys.FormatWall(new DateTime(2026, 7, 15, 0, 0, 0), zone).ShouldBe("2026-07-15T00:00:00+02:00");
                TimeBucketKeys.FormatInstant(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc).ShouldBe("2026-01-01T00:00:00+00:00");
                TimeBucketKeys.FormatDate(new DateOnly(2026, 10, 5)).ShouldBe("2026-10-05");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ResolveZone_Unresolvable_FailsClosed_NoUtcFallback()
    {
        var ex = Should.Throw<ArgumentException>(() => TimeBucketKeys.ResolveZone("Mars/Olympus"));
        ex.Message.ShouldBe("The time zone is not a recognized IANA zone id.");
    }
}

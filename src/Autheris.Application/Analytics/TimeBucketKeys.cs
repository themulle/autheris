namespace Autheris.Application.Analytics;

using System;
using System.Globalization;
using TrinoSqlEngine;

internal static class TimeBucketKeys
{
    public const string TimestampKeyFormat = "yyyy-MM-dd'T'HH:mm:sszzz";
    public const string DateKeyFormat = "yyyy-MM-dd";

    public static string FormatInstant(DateTimeOffset instant, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.ToString(TimestampKeyFormat, CultureInfo.InvariantCulture);
    }

    public static string FormatWall(DateTime wallStart, TimeZoneInfo zone)
    {
        TimeSpan offset;
        if (zone.IsAmbiguousTime(wallStart))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(wallStart);
            offset = offsets[0];
            for (var i = 1; i < offsets.Length; i++)
            {
                if (offsets[i] > offset) offset = offsets[i];
            }
        }
        else if (zone.IsInvalidTime(wallStart))
        {
            offset = zone.GetUtcOffset(wallStart.AddDays(-1));
        }
        else
        {
            offset = zone.GetUtcOffset(wallStart);
        }

        var dto = new DateTimeOffset(wallStart, offset);
        return dto.ToString(TimestampKeyFormat, CultureInfo.InvariantCulture);
    }

    public static string FormatDate(DateOnly date) => date.ToString(DateKeyFormat, CultureInfo.InvariantCulture);

    public static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.Utc;
        }

        if (!TimeZoneRegistry.Contains(id))
        {
            throw new ArgumentException("The time zone is not a recognized IANA zone id.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("The time zone is not available on this host.");
        }
    }
}

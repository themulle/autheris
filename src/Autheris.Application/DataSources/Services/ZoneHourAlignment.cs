namespace Autheris.Application.DataSources.Services;

using System;
using System.Collections.Concurrent;
using System.Globalization;

internal static class ZoneHourAlignment
{
    private static readonly ConcurrentDictionary<string, (bool Constant, int MinutePart)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetMinutePart(string zoneId, out int m)
    {
        var (constant, minutePart) = Cache.GetOrAdd(zoneId, id =>
        {
            var zoneArms = SqliteZoneOffsetTable.Get(id);
            var baseMinutes = ParseMinutePart(zoneArms.BaseOffset);
            foreach (var arm in zoneArms.Arms)
            {
                if (ParseMinutePart(arm.Offset) != baseMinutes)
                {
                    return (false, 0);
                }
            }
            return (true, baseMinutes);
        });

        m = minutePart;
        return constant;
    }

    private static int ParseMinutePart(string offsetStr)
    {
        // offsetStr is e.g. "+02:00", "-05:00", "+05:30"
        if (TimeSpan.TryParse(offsetStr.TrimStart('+'), CultureInfo.InvariantCulture, out var ts))
        {
            var totalMin = (int)Math.Abs(ts.TotalMinutes);
            return totalMin % 60;
        }
        return 0;
    }
}

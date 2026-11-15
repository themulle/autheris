namespace Autheris.Application.DataSources.Services;

using System;
using System.Collections.Concurrent;

internal static class ZoneHourAlignment
{
    private static readonly ConcurrentDictionary<string, (bool Constant, int MinutePart)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryGetMinutePart(string zoneId, out int m)
    {
        var (constant, minutePart) = Cache.GetOrAdd(zoneId, id =>
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz))
            {
                throw new ArgumentException($"Time zone '{id}' is unknown.", nameof(id));
            }

            var baseMinutes = (int)Math.Abs(tz.BaseUtcOffset.TotalMinutes) % 60;
            foreach (var rule in tz.GetAdjustmentRules())
            {
                var stdMinutes = (int)Math.Abs((tz.BaseUtcOffset + rule.BaseUtcOffsetDelta).TotalMinutes) % 60;
                var dstMinutes = (int)Math.Abs((tz.BaseUtcOffset + rule.BaseUtcOffsetDelta + rule.DaylightDelta).TotalMinutes) % 60;
                if (stdMinutes != baseMinutes || dstMinutes != baseMinutes)
                {
                    return (false, 0);
                }
            }

            return (true, baseMinutes);
        });

        m = minutePart;
        return constant;
    }
}

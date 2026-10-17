namespace TrinoSqlEngine.Tests.TimeZones;

using Xunit;

/// <summary>
/// BC-130-6(b): compares every registry entry with the operating system time zone data through <see cref="TimeZoneInfo"/>: the Windows id and the
/// IANA id must both resolve, and their UTC offsets must be equal at the four 2026 transition instants of Europe/Berlin and America/New_York and
/// at mid-winter and mid-summer. The runtime self-test never does this (it is warning-level there); the build does.
/// </summary>
internal static class TzDataCrossCheck
{
    public static readonly DateTimeOffset[] Instants =
    [
        new(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
        new(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
        new(2026, 10, 25, 1, 0, 0, TimeSpan.Zero),
        new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero),
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero),
        new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)
    ];

    /// <summary>The mismatching entries as <c>iana (reason)</c>; empty when every entry agrees.</summary>
    public static List<string> Run(IEnumerable<(string Iana, string Windows)> entries)
    {
        var list = entries.ToList();
        var problems = new List<string>();
        foreach (var (iana, windows) in list)
        {
            if (!TryFind(iana, out var ianaZone)) { problems.Add($"{iana} (iana id not resolvable)"); continue; }
            if (!TryFindWindows(windows, list, out var windowsZone)) { problems.Add($"{iana} (windows id '{windows}' not resolvable)"); continue; }

            foreach (var instant in Instants)
            {
                if (ianaZone.GetUtcOffset(instant) != windowsZone.GetUtcOffset(instant))
                {
                    problems.Add($"{iana} (offset differs from '{windows}' at {instant:u})");
                    break;
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// The zone of a Windows id: the id itself, or the IANA id the runtime maps it to. When that name is a deprecated one that the machine's tzdata
    /// no longer has (Linux), the id is still known to the runtime, and its rules are taken from the territory-001 member of the registry
    /// (the first entry of that Windows id that resolves).
    /// </summary>
    private static bool TryFindWindows(string windows, List<(string Iana, string Windows)> entries, out TimeZoneInfo zone)
    {
        if (TryFind(windows, out zone)) return true;
        if (!TimeZoneInfo.TryConvertWindowsIdToIanaId(windows, out var mapped))
        {
            return false;
        }

        if (TryFind(mapped, out zone)) return true;
        foreach (var (iana, w) in entries)
        {
            if (string.Equals(w, windows, StringComparison.Ordinal) && TryFind(iana, out zone)) return true;
        }

        zone = TimeZoneInfo.Utc;
        return false;
    }

    private static bool TryFind(string id, out TimeZoneInfo zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
            return false;
        }
    }
}

/// <summary>
/// A fact that runs on CI (the <c>CI</c> or <c>GITHUB_ACTIONS</c> environment variable is set) or when <c>AUTHERIS_TZ_CROSSCHECK=1</c>; it is skipped
/// elsewhere because it needs the time zone data of the machine. It also carries the trait <c>Category=TzDataCrossCheck</c> for a filter.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class CiOnlyFactAttribute : FactAttribute
{
    public CiOnlyFactAttribute()
    {
        bool enabled = Environment.GetEnvironmentVariable("AUTHERIS_TZ_CROSSCHECK") == "1"
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        if (!enabled) Skip = "Time zone data cross-check runs on CI (CI or GITHUB_ACTIONS set) or with AUTHERIS_TZ_CROSSCHECK=1.";
    }
}

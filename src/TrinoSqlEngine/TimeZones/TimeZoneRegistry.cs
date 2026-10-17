namespace TrinoSqlEngine;

using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>Result of <see cref="TimeZoneRegistry.SelfTest"/>. <c>FailedCheck</c> is a constant check id, never data.</summary>
public sealed record TimeZoneSelfTestResult(bool Passed, string? FailedCheck, bool CrossCheckSkipped);

/// <summary>
/// REQ-SQL-130 (A-2): the closed set of time zones the gateway admits. It is built once from the embedded resource
/// <c>iana-windows-zones.txt</c> (lines <c>iana;windows</c>), loaded with <see cref="Assembly.GetManifestResourceStream(string)"/>; there is no
/// runtime download and no file path configuration (BC-130-6e). The value that reaches a database is always registry-owned text, never the
/// caller's string instance. Lookup is ordinal and case-sensitive, after a length cap of 64, and never trims or normalizes (BC-130-5e).
/// A failing load leaves an empty registry (every lookup fails) and a failed self-test: fail closed.
/// </summary>
public static partial class TimeZoneRegistry
{
    /// <summary>SHA-256 (hex, lower case) of the resource bytes. It changes only in the commit that bumps the CLDR version (BC-130-6d).</summary>
    internal const string ResourceSha256 = "f6a37bb0a8dec3e664ff8104981d8688dba027a1192c2ec562bf97ec8b08ee9e";

    /// <summary>Entry-count floor of the self-test (a truncated resource fails).</summary>
    internal const int MinimumEntries = 400;

    internal const int MaxLength = 64;

    internal const string ResourceName = "TrinoSqlEngine.TimeZones.iana-windows-zones.txt";

    // Hygiene checks only (every value is bound, never spliced): quotes, comment markers, control characters and non-ASCII cannot pass.
    [GeneratedRegex(@"\A[A-Za-z0-9_+\-/]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IanaPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9 .,()+\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPattern();

    /// <summary>The mapping every anchor must have (a swapped mapping cannot slip through the count and the pattern checks).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Anchors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UTC"] = "UTC",
        ["Etc/UTC"] = "UTC",
        ["Europe/Berlin"] = "W. Europe Standard Time",
        ["America/New_York"] = "Eastern Standard Time",
        ["Asia/Kolkata"] = "India Standard Time",
        ["Australia/Lord_Howe"] = "Lord Howe Standard Time",
        ["America/St_Johns"] = "Newfoundland Standard Time",
        ["Pacific/Kiritimati"] = "Line Islands Standard Time"
    };

    private static readonly Snapshot Current = LoadEmbedded();

    public static int Count => Current.Map.Count;

    /// <summary>Ordinal, case-sensitive membership; a value longer than 64 characters is never a member.</summary>
    public static bool Contains(string ianaId) => ianaId is { Length: > 0 and <= MaxLength } && Current.Map.ContainsKey(ianaId);

    /// <summary>SQL Server: the Windows zone id. PostgreSQL and DuckDB: the registry's IANA id. Any other dialect: false.</summary>
    public static bool TryResolve(TargetSqlDialect dialect, string ianaId, out string dialectValue)
    {
        dialectValue = string.Empty;
        if (ianaId is not { Length: > 0 and <= MaxLength } || !Current.Map.TryGetValue(ianaId, out var windows))
        {
            return false;
        }

        switch (dialect)
        {
            case TargetSqlDialect.SqlServer:
                dialectValue = windows;
                return true;
            case TargetSqlDialect.PostgreSql:
            case TargetSqlDialect.DuckDb:
                dialectValue = Current.Canonical[ianaId];   // the registry's own string instance, never the caller's
                return true;
            default:
                return false;
        }
    }

    /// <summary>Pure, no I/O besides the embedded resource. Evaluates the loaded registry.</summary>
    public static TimeZoneSelfTestResult SelfTest() => SelfTestOf(Current);

    /// <summary>Test seam: the self-test of a registry built from <paramref name="resourceText"/>.</summary>
    internal static TimeZoneSelfTestResult SelfTest(string resourceText) => SelfTestOf(Parse(resourceText, Sha256Of(resourceText)));

    /// <summary>Test seam: the self-test with an explicit expected hash.</summary>
    internal static TimeZoneSelfTestResult SelfTest(string resourceText, string expectedSha256) => SelfTestOf(Parse(resourceText, expectedSha256));

    internal static string Sha256Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string? LoadResourceText()
    {
        using var stream = typeof(TimeZoneRegistry).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }

    private static Snapshot LoadEmbedded()
    {
        try
        {
            string? text = LoadResourceText();
            return text is null ? Snapshot.Failed("resource-missing") : Parse(text, ResourceSha256);
        }
        catch (Exception)
        {
            // Any failure while loading means "no zone is known": the function stays unavailable, the gateway keeps running.
            return Snapshot.Failed("resource-load");
        }
    }

    private static TimeZoneSelfTestResult SelfTestOf(Snapshot snapshot)
    {
        if (snapshot.FailedCheck is { } failed) return new TimeZoneSelfTestResult(false, failed, true);
        if (snapshot.Map.Count < MinimumEntries) return new TimeZoneSelfTestResult(false, "entry-count", true);
        foreach (var (iana, windows) in Anchors)
        {
            if (!snapshot.Map.TryGetValue(iana, out var actual) || !string.Equals(actual, windows, StringComparison.Ordinal))
            {
                return new TimeZoneSelfTestResult(false, "anchor", true);
            }
        }

        return new TimeZoneSelfTestResult(true, null, true);
    }

    private static Snapshot Parse(string text, string expectedSha256)
    {
        if (!string.Equals(Sha256Of(text), expectedSha256, StringComparison.Ordinal)) return Snapshot.Failed("hash");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var canonical = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            if (rawLine.Length == 0 || rawLine[0] == '#') continue;
            int separator = rawLine.IndexOf(';', StringComparison.Ordinal);
            if (separator <= 0 || rawLine.IndexOf(';', separator + 1) >= 0) return Snapshot.Failed("format");

            string iana = rawLine[..separator];
            string windows = rawLine[(separator + 1)..];
            if (!IanaPattern().IsMatch(iana) || !WindowsPattern().IsMatch(windows)) return Snapshot.Failed("pattern");
            if (map.ContainsKey(iana)) return Snapshot.Failed("duplicate");
            map[iana] = windows;
            canonical[iana] = iana;
        }

        return new Snapshot(map.ToFrozenDictionary(StringComparer.Ordinal), canonical.ToFrozenDictionary(StringComparer.Ordinal), null);
    }

    private sealed record Snapshot(FrozenDictionary<string, string> Map, FrozenDictionary<string, string> Canonical, string? FailedCheck)
    {
        public static Snapshot Failed(string check) =>
            new(FrozenDictionary<string, string>.Empty, FrozenDictionary<string, string>.Empty, check);
    }
}

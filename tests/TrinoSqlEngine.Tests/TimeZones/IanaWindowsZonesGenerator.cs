namespace TrinoSqlEngine.Tests.TimeZones;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

/// <summary>
/// BC-130-6(a): the reproducible generator of <c>src/TrinoSqlEngine/TimeZones/iana-windows-zones.txt</c> (the "script"). It reads the two checked-in
/// CLDR files (<c>Cldr/windowsZones.xml</c>, <c>Cldr/timezone.xml</c>) and writes the resource text byte for byte. A CLDR version bump replaces
/// both files, the source hashes below, the version below, regenerates the resource (run the test class with the environment variable
/// <c>AUTHERIS_TZ_REGENERATE_TO=&lt;path&gt;</c>) and updates the resource hash constant in <c>TimeZoneRegistry</c>, all in one commit (BC-130-6d).
/// </summary>
internal static partial class IanaWindowsZonesGenerator
{
    public const string CldrVersion = "release-48";
    public const string WindowsZonesSha256 = "9cf3db6a31fb382fee21b70be6feba1e82766b0fcd06e6261fb7936a73e537ff";
    public const string TimezoneSha256 = "54ce7d9f1d385101e24356df7d48ae62cf2acc4421378da60a4007d2488c1aad";

    /// <summary>
    /// IANA ids that are not offered because their offsets differ from the Windows zone they share in CLDR (found by the tzdata cross-check,
    /// BC-130-6b): SQL Server would convert them with the Windows rules, PostgreSQL and DuckDB with the IANA rules. The value is the reason.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Divergent = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Found by the cross-check against tzdata 2025/2026 with CLDR release-48: the Windows zone follows other rules than the IANA id.
        ["America/Scoresbysund"] = "Windows 'Azores Standard Time' differs (Greenland zone rules)",
        ["Antarctica/Casey"] = "Windows 'Central Pacific Standard Time' differs (Casey station rules)",
        ["Asia/Bishkek"] = "Windows 'Central Asia Standard Time' follows Kazakhstan (UTC+05 since 2024), Kyrgyzstan stays UTC+06",
        ["Asia/Urumqi"] = "Windows 'Central Asia Standard Time' follows Kazakhstan (UTC+05 since 2024), Urumqi stays UTC+06",
        ["Etc/GMT-6"] = "Fixed UTC+06, Windows 'Central Asia Standard Time' follows Kazakhstan (UTC+05 since 2024)",
        ["Indian/Chagos"] = "Windows 'Central Asia Standard Time' follows Kazakhstan (UTC+05 since 2024), Chagos stays UTC+06"
    };

    [GeneratedRegex(@"\A[A-Za-z0-9_+\-/]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IanaPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9 .,()+\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPattern();

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Generate(string windowsZonesXml, string timezoneXml)
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var zone in Load(windowsZonesXml).Descendants("mapZone"))
        {
            string windows = (string?)zone.Attribute("other") ?? throw new InvalidDataException("mapZone without other");
            if (!WindowsPattern().IsMatch(windows)) throw new InvalidDataException("illegal windows id in source");
            foreach (var id in ((string?)zone.Attribute("type") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IanaPattern().IsMatch(id)) throw new InvalidDataException("illegal iana id in source");
                if (map.TryGetValue(id, out var existing) && existing != windows) throw new InvalidDataException("conflicting mapping in source");
                map[id] = windows;
            }
        }

        // CLDR spells some zones with the older tzdata name (Asia/Calcutta). Where the bcp47 data names the current IANA id, the old spelling
        // is replaced by it (a modern tzdata no longer knows the old one). Other aliases are never added, except the plain id UTC.
        foreach (var type in Load(timezoneXml).Descendants("type"))
        {
            string? aliasText = (string?)type.Attribute("alias");
            if (aliasText is null) continue;
            var ids = aliasText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            string? iana = (string?)type.Attribute("iana");

            var windows = ids.Where(map.ContainsKey).Select(i => map[i]).Distinct(StringComparer.Ordinal).ToList();
            if (windows.Count == 0) continue;
            if (windows.Count > 1) throw new InvalidDataException("alias group maps to several windows ids");

            if (iana is not null)
            {
                if (!IanaPattern().IsMatch(iana)) throw new InvalidDataException("illegal iana id in source");
                foreach (var id in ids) map.Remove(id);
                map[iana] = windows[0];
            }
            else if (ids.Contains("UTC", StringComparer.Ordinal))
            {
                map["UTC"] = windows[0];
            }
        }

        // Entries whose rules differ from the Windows zone they map to on current operating system data (see Divergent): not offered.
        foreach (var id in Divergent.Keys) map.Remove(id);

        var text = new StringBuilder();
        text.Append("# iana;windows time zone mapping. Generated, do not edit: see IanaWindowsZonesGenerator in TrinoSqlEngine.Tests.\n");
        text.Append(CultureInfo.InvariantCulture, $"# Source: Unicode CLDR {CldrVersion}, common/supplemental/windowsZones.xml sha256={WindowsZonesSha256}\n");
        text.Append(CultureInfo.InvariantCulture, $"# Source: Unicode CLDR {CldrVersion}, common/bcp47/timezone.xml sha256={TimezoneSha256} (current names of the zones above)\n");
        text.Append("# Copyright (c) Unicode, Inc. Distributed under the Unicode License v3 (see THIRD_PARTY_NOTICES.md).\n");
        foreach (var (id, windows) in map)
        {
            text.Append(id).Append(';').Append(windows).Append('\n');
        }

        return text.ToString();
    }

    private static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }
}

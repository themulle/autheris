using System.Text.RegularExpressions;

namespace Autheris.Domain.Options;

/// <summary>The kinds of key a <c>TeamToGroupSidMap</c> entry may address (NL-SC-09).</summary>
public enum TeamMapKeyKind
{
    Id = 1,
    Fqn = 2,
    Name = 3
}

/// <summary>
/// NL-SC-09 (O-5f): the single definition of a valid OpenMetadata <c>TeamToGroupSidMap</c>. Keys need an <c>id:</c> (GUID), <c>fqn:</c>
/// or <c>name:</c> prefix, values are group SIDs, and two keys resolving to the same team are rejected. The failure messages name the
/// option path and the entry position only, never a key or SID value.
/// </summary>
public static partial class TeamToGroupSidMapRules
{
    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9A-Za-z]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();

    /// <summary>Parses a prefixed key. <paramref name="normalized"/> is the canonical form used for duplicate detection.</summary>
    public static bool TryParseKey(string? rawKey, out TeamMapKeyKind kind, out string value, out string normalized)
    {
        kind = default;
        value = string.Empty;
        normalized = string.Empty;
        var key = rawKey?.Trim();
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (TryStrip(key, "id:", out var rest))
        {
            if (!Guid.TryParse(rest, out var id) || id == Guid.Empty)
            {
                return false;
            }

            kind = TeamMapKeyKind.Id;
            value = rest;
            normalized = "id:" + id.ToString("N");
            return true;
        }

        if (TryStrip(key, "fqn:", out rest) && rest.Length > 0)
        {
            kind = TeamMapKeyKind.Fqn;
            value = rest;
            normalized = "fqn:" + rest;
            return true;
        }

        if (TryStrip(key, "name:", out rest) && rest.Length > 0)
        {
            kind = TeamMapKeyKind.Name;
            value = rest;
            normalized = "name:" + rest;
            return true;
        }

        return false;
    }

    public static bool IsValidSid(string? value) => !string.IsNullOrWhiteSpace(value) && SidPattern().IsMatch(value.Trim());

    /// <summary>Returns one failure per invalid entry; empty when the map is valid.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string>? map, string optionPath)
    {
        var failures = new List<string>();
        if (map is null)
        {
            return failures;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var (rawKey, sidValue) in map)
        {
            position++;
            if (!TryParseKey(rawKey, out _, out _, out var normalized))
            {
                failures.Add($"{optionPath} entry #{position}: the key needs an 'id:' (valid GUID), 'fqn:' or 'name:' prefix followed by a value.");
                continue;
            }

            if (!IsValidSid(sidValue))
            {
                failures.Add($"{optionPath} entry #{position}: the value must be a valid group SID (S-1-...).");
            }

            if (!seen.Add(normalized))
            {
                failures.Add($"{optionPath} entry #{position}: the key resolves to the same team as an earlier entry.");
            }
        }

        return failures;
    }

    private static bool TryStrip(string key, string prefix, out string rest)
    {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = key[prefix.Length..].Trim();
            return true;
        }

        rest = string.Empty;
        return false;
    }
}

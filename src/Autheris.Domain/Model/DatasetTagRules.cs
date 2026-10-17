namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>Issue #111 (A-3): pure rules for dataset catalog tags and the stored/parsed visibility values. Fail-closed on every unknown input.</summary>
public static class DatasetTagRules
{
    public const int MaxTagLength = 64;
    public const int HardMaxTagsPerDataset = 256;
    public const string InvalidTag = "INVALID_TAG";
    public const string TagLimitExceeded = "TAG_LIMIT_EXCEEDED";

    /// <summary>Trim, lower-case (invariant), 1-64 chars of <c>[A-Za-z0-9._:-]</c>; anything else is rejected.</summary>
    public static bool TryNormalize(string? raw, out string tag)
    {
        tag = string.Empty;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxTagLength)
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-';
            if (!ok)
            {
                return false;
            }
        }

        tag = trimmed.ToLowerInvariant();
        return true;
    }

    /// <summary>Normalizes and de-duplicates a set; <see cref="InvalidTag"/> wins over <see cref="TagLimitExceeded"/>.</summary>
    public static bool TryNormalizeSet(IEnumerable<string?> raw, int max, out IReadOnlyList<string> tags, out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(raw);
        tags = [];
        errorCode = string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var item in raw)
        {
            if (!TryNormalize(item, out var tag))
            {
                errorCode = InvalidTag;
                return false;
            }

            if (seen.Add(tag))
            {
                list.Add(tag);
            }
        }

        if (list.Count > max)
        {
            errorCode = TagLimitExceeded;
            return false;
        }

        tags = list;
        return true;
    }

    /// <summary>Maps a stored integer: 0, 1, 2 only; everything else (and null) is <see cref="DatasetVisibility.Hidden"/>.</summary>
    public static DatasetVisibility ReadStored(int? stored) => stored switch
    {
        1 => DatasetVisibility.Summary,
        2 => DatasetVisibility.Full,
        _ => DatasetVisibility.Hidden
    };

    /// <summary>Parses the names Hidden, Summary, Full (any case). Numbers, numeric strings, lists and padded values are rejected.</summary>
    public static bool TryParseVisibility(string? raw, out DatasetVisibility visibility)
    {
        visibility = DatasetVisibility.Hidden;
        if (raw is null)
        {
            return false;
        }

        foreach (var name in Enum.GetNames<DatasetVisibility>())
        {
            if (string.Equals(raw, name, StringComparison.OrdinalIgnoreCase))
            {
                visibility = Enum.Parse<DatasetVisibility>(name);
                return true;
            }
        }

        return false;
    }
}

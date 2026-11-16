namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Text;
using Autheris.Application.Docs.Services;

/// <summary>
/// Sanitises untrusted tenant-provided catalog metadata using the shared rune filtering rules,
/// Unicode NFC normalisation, and rune-boundary length capping.
/// </summary>
public static class DiscoveryTextSanitizer
{
    public const int MaxDisplayNameRunes = 200;
    public const int MaxDescriptionRunes = 2000;
    public const int MaxTagRunes = 64;
    public const int MaxTagCount = 20;
    public const int MaxMcpPayloadBytes = 64 * 1024;

    public static string? SanitizeText(string? input, int maxRunes = MaxDescriptionRunes, bool allowNewlinesAndTabs = false)
    {
        if (input is null)
        {
            return null;
        }

        var normalized = input.IsNormalized(NormalizationForm.FormC) ? input : input.Normalize(NormalizationForm.FormC);
        var filtered = UntrustedTextRunes.FilterRunes(normalized, allowNewlinesAndTabs).Trim();
        return TruncateOnRuneBoundary(filtered, maxRunes);
    }

    public static string SanitizeRequiredText(string? input, string fallback, int maxRunes = MaxDisplayNameRunes)
    {
        var clean = SanitizeText(input, maxRunes, allowNewlinesAndTabs: false);
        return string.IsNullOrEmpty(clean) ? fallback : clean;
    }

    public static IReadOnlyList<string> SanitizeTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            if (result.Count >= MaxTagCount)
            {
                break;
            }

            var clean = SanitizeText(raw, MaxTagRunes, allowNewlinesAndTabs: false);
            if (!string.IsNullOrEmpty(clean) && seen.Add(clean))
            {
                result.Add(clean);
            }
        }

        return result;
    }

    public static string TruncateUtf8ToMaxBytes(string json, int maxBytes = MaxMcpPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) <= maxBytes)
        {
            return json;
        }

        var sb = new StringBuilder(json.Length);
        var bytes = 0;
        foreach (var rune in json.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maxBytes)
            {
                break;
            }

            sb.Append(rune);
            bytes += runeBytes;
        }

        return sb.ToString();
    }

    internal static string TruncateOnRuneBoundary(string text, int maxRunes)
    {
        if (maxRunes <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(Math.Min(text.Length, maxRunes * 2));
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count >= maxRunes)
            {
                break;
            }

            sb.Append(rune);
            count++;
        }

        return sb.ToString();
    }
}

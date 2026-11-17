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

    /// <summary>Marker appended to a summary that was cut.</summary>
    public const string SummaryCutMarker = "…";

    /// <summary>Default maximum length (in runes) of the description summary in list and search results.</summary>
    public const int MaxSummaryRunes = 240;

    /// <summary>
    /// Sanitises <paramref name="input"/> like <see cref="SanitizeText"/>, then shortens it for list and search results:
    /// the first sentence when it fits in <paramref name="maxRunes"/>, otherwise a cut on a rune boundary followed by
    /// <see cref="SummaryCutMarker"/>. Text that already fits is returned unchanged.
    /// </summary>
    public static string? Summarize(string? input, int maxRunes = MaxSummaryRunes)
    {
        var clean = SanitizeText(input, MaxDescriptionRunes);
        if (string.IsNullOrEmpty(clean))
        {
            return clean;
        }

        var sentenceEnd = -1;
        for (var i = 0; i < clean.Length - 1; i++)
        {
            if (clean[i] is '.' or '!' or '?' && char.IsWhiteSpace(clean[i + 1]))
            {
                sentenceEnd = i + 1;
                break;
            }
        }

        var candidate = sentenceEnd > 0 ? clean[..sentenceEnd] : clean;
        var runeCount = 0;
        foreach (var _ in candidate.EnumerateRunes())
        {
            runeCount++;
        }

        if (runeCount <= maxRunes)
        {
            return candidate;
        }

        return TruncateOnRuneBoundary(candidate, Math.Max(1, maxRunes - 1)).TrimEnd() + SummaryCutMarker;
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

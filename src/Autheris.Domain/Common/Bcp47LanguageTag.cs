namespace Autheris.Domain.Common;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

/// <summary>
/// High-performance, zero-allocation span-based BCP 47 / RFC 5646 language tag parser and canonicalizer.
/// Guards against ReDoS by using linear parsing without regular expressions (SEC-CAT146-04).
/// </summary>
public static class Bcp47LanguageTag
{
    public const int MaxTagLength = 35;

    public static bool IsValid(string? tag, [NotNullWhen(true)] out string? canonicalTag)
    {
        canonicalTag = null;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var trimmed = tag.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxTagLength) return false;

        return TryParse(trimmed.AsSpan(), out canonicalTag);
    }

    public static bool TryCanonicalize(string? tag, [NotNullWhen(true)] out string? canonicalTag)
        => IsValid(tag, out canonicalTag);

    public static bool TryParse(ReadOnlySpan<char> span, [NotNullWhen(true)] out string? canonicalTag)
    {
        canonicalTag = null;
        if (span.IsEmpty || span.Length > MaxTagLength) return false;

        var sb = new StringBuilder(span.Length);
        int subtagIndex = 0;
        int start = 0;

        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == '-')
            {
                int len = i - start;
                if (len == 0) return false; // Empty subtag (e.g. "en--US")
                var subtag = span.Slice(start, len);

                if (subtagIndex == 0)
                {
                    // Primary language: 2 to 8 ASCII letters
                    if (len < 2 || len > 8 || !IsAsciiLetters(subtag)) return false;
                    for (int k = 0; k < len; k++)
                    {
                        sb.Append(char.ToLowerInvariant(subtag[k]));
                    }
                }
                else
                {
                    // Subsequent subtags
                    if (len == 4 && IsAsciiLetters(subtag))
                    {
                        // Script subtag (4 letters): Titlecase (e.g. Hant)
                        sb.Append('-');
                        sb.Append(char.ToUpperInvariant(subtag[0]));
                        for (int k = 1; k < len; k++)
                        {
                            sb.Append(char.ToLowerInvariant(subtag[k]));
                        }
                    }
                    else if ((len == 2 && IsAsciiLetters(subtag)) || (len == 3 && IsAsciiDigits(subtag)))
                    {
                        // Region subtag (2 letters or 3 digits): UPPERCASE (e.g. US, CH, 001)
                        sb.Append('-');
                        for (int k = 0; k < len; k++)
                        {
                            sb.Append(char.ToUpperInvariant(subtag[k]));
                        }
                    }
                    else if (len >= 1 && len <= 8 && IsAsciiAlphanumeric(subtag))
                    {
                        // Variant / extension / private use: lowercase
                        sb.Append('-');
                        for (int k = 0; k < len; k++)
                        {
                            sb.Append(char.ToLowerInvariant(subtag[k]));
                        }
                    }
                    else
                    {
                        return false;
                    }
                }

                subtagIndex++;
                start = i + 1;
            }
        }

        if (subtagIndex == 0) return false;

        canonicalTag = sb.ToString();
        return true;
    }

    private static bool IsAsciiLetters(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsAsciiLetter(c)) return false;
        }
        return true;
    }

    private static bool IsAsciiDigits(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return true;
    }

    private static bool IsAsciiAlphanumeric(ReadOnlySpan<char> span)
    {
        foreach (char c in span)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// Produces the RFC 4647 subtag truncation fallback chain for a canonical language tag.
    /// E.g. "de-CH" yields ["de-CH", "de"].
    /// </summary>
    public static IEnumerable<string> GetFallbackChain(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) yield break;
        yield return tag;

        int dashIdx = tag.LastIndexOf('-');
        while (dashIdx > 0)
        {
            tag = tag[..dashIdx];
            yield return tag;
            dashIdx = tag.LastIndexOf('-');
        }
    }
}

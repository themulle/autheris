namespace Autheris.Application.Mcp.Services;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Read-side sanitiser for externally authored catalog text that MCP clients (and the LLMs behind them) consume (PLAN-CAT-146-A,
/// SEC-CAT146A-02..04). Pure and logger-free: the text never reaches a log, exception or metric (SEC-CAT146A-06).
/// </summary>
internal static class McpCatalogText
{
    /// <summary>Maximum length of a display name in UTF-16 code units, counted after sanitising.</summary>
    internal const int MaxDisplayNameLength = 256;

    /// <summary>Longest run of consecutive combining marks kept (limits "zalgo" stacks).</summary>
    private const int MaxConsecutiveMarks = 4;

    /// <summary>
    /// Returns the display name safe for MCP output: whitespace and control characters become one space (before collapsing, so words never
    /// fuse), invisible and format characters (category Cf, variation selectors, the Tags block, fillers, private-use and unassigned code
    /// points) and invalid UTF-16 are removed, runs of combining marks are capped, whitespace runs are collapsed, the result is trimmed and
    /// cut at <see cref="MaxDisplayNameLength"/> on a rune boundary. A result without a letter or digit yields <paramref name="tableName"/>.
    /// Never throws.
    /// </summary>
    internal static string DisplayName(string? stored, string tableName)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return tableName;
        }

        if (stored.Length <= MaxDisplayNameLength && IsClean(stored))
        {
            return stored;
        }

        var builder = new StringBuilder(Math.Min(stored.Length, MaxDisplayNameLength));
        var pendingSpace = false;
        var hasLetterOrDigit = false;
        var marks = 0;
        foreach (var rune in stored.EnumerateRunes())
        {
            if (IsWordBreak(rune))
            {
                pendingSpace = builder.Length > 0;
                marks = 0;
                continue;
            }

            if (IsInvisible(rune))
            {
                continue;
            }

            if (IsMark(rune))
            {
                if (++marks > MaxConsecutiveMarks)
                {
                    continue;
                }
            }
            else
            {
                marks = 0;
            }

            var width = rune.Utf16SequenceLength + (pendingSpace ? 1 : 0);
            if (builder.Length + width > MaxDisplayNameLength)
            {
                break;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune);
            hasLetterOrDigit |= Rune.IsLetterOrDigit(rune);
        }

        return hasLetterOrDigit ? builder.ToString() : tableName;
    }

    /// <summary>True when the text needs no change at all, so the allocation-free fast path may return it as is.</summary>
    private static bool IsClean(string text)
    {
        var previousSpace = true; // a leading space is not clean
        var hasLetterOrDigit = false;
        var marks = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == ' ')
            {
                if (previousSpace)
                {
                    return false;
                }

                previousSpace = true;
                marks = 0;
                continue;
            }

            if (IsWordBreak(rune) || IsInvisible(rune))
            {
                return false;
            }

            if (IsMark(rune))
            {
                if (++marks > MaxConsecutiveMarks)
                {
                    return false;
                }
            }
            else
            {
                marks = 0;
            }

            previousSpace = false;
            hasLetterOrDigit |= Rune.IsLetterOrDigit(rune);
        }

        return !previousSpace && hasLetterOrDigit;
    }

    private static bool IsMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    private static bool IsWordBreak(Rune rune) =>
        Rune.IsWhiteSpace(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control;

    private static bool IsInvisible(Rune rune)
    {
        // EnumerateRunes maps invalid UTF-16 (lone surrogates) to U+FFFD; it is dropped like any other non-content.
        if (rune == Rune.ReplacementChar)
        {
            return true;
        }

        var value = rune.Value;
        if (value is >= 0xFE00 and <= 0xFE0F or >= 0xE0000 and <= 0xE007F or >= 0x2060 and <= 0x2064
            or 0x180E or 0x00AD or 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
    }
}

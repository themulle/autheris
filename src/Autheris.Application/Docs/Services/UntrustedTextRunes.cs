namespace Autheris.Application.Docs.Services;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Shared rune-level filtering rules for untrusted text: strips control, bidi, zero-width, invisible, and tag characters,
/// and caps combining-mark runs.
/// </summary>
internal static class UntrustedTextRunes
{
    internal const int MaxConsecutiveMarks = 4;

    internal static bool IsMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    internal static bool IsControl(Rune rune) =>
        rune.Value <= 0x1F || (rune.Value >= 0x7F && rune.Value <= 0x9F) ||
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control;

    internal static bool IsWordBreak(Rune rune) =>
        Rune.IsWhiteSpace(rune) || IsControl(rune);

    internal static bool IsInvisible(Rune rune)
    {
        if (rune == Rune.ReplacementChar)
        {
            return true;
        }

        var value = rune.Value;
        if (value is >= 0xFE00 and <= 0xFE0F
            or >= 0xE0000 and <= 0xE007F
            or >= 0x2060 and <= 0x2064
            or >= 0x202A and <= 0x202E
            or >= 0x2066 and <= 0x2069
            or >= 0x200B and <= 0x200F
            or 0xFEFF
            or 0x180E or 0x00AD or 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x2800 or 0x3164 or 0xFFA0)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
    }

    internal static string FilterRunes(string text, bool allowNewlinesAndTabs = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var marks = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (allowNewlinesAndTabs && rune.Value is '\n' or '\t')
            {
                builder.Append(rune);
                marks = 0;
                continue;
            }

            if (IsControl(rune) || IsInvisible(rune))
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

            builder.Append(rune);
        }

        return builder.ToString();
    }
}

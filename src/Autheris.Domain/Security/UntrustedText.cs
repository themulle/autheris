using System.Globalization;
using System.Text;

namespace Autheris.Domain.Security;

/// <summary>Why <see cref="UntrustedText.Normalize"/> refused a value. Carries no part of the value.</summary>
public enum TextCheckFailure
{
    None = 0,
    TooLong = 1,
    InvalidCharacters = 2
}

/// <summary>Result of <see cref="UntrustedText.Normalize"/>: <see cref="Value"/> is the normalised text, or <c>null</c> for empty input.</summary>
public readonly record struct TextCheck(bool IsValid, string? Value, TextCheckFailure Failure)
{
    public static TextCheck Ok(string? value) => new(true, value, TextCheckFailure.None);

    public static TextCheck Fail(TextCheckFailure failure) => new(false, null, failure);
}

/// <summary>
/// PLAN-GOV-158-INC1 decision 2 (SEC-GOV158I1-01/02): normaliser for free text that is authored by a person and later shown to other
/// users or LLM clients. NFC, trim (U+0020 only), empty becomes <c>null</c>; rejects by Unicode category (controls, format characters
/// such as bidi and zero-width marks, surrogates, private use, unassigned, line/paragraph separators, every space except U+0020, tag
/// characters and variation selectors) instead of a code point list. Never echoes the value.
/// </summary>
public static class UntrustedText
{
    /// <summary>Raw input longer than this multiple of the cap is refused before normalisation (DoS bound).</summary>
    private const int RawLengthFactor = 4;

    public static TextCheck Normalize(string? raw, int maxLength, bool allowLineFeed)
    {
        if (raw is null)
        {
            return TextCheck.Ok(null);
        }

        if (raw.Length > (long)RawLengthFactor * maxLength)
        {
            return TextCheck.Fail(TextCheckFailure.TooLong);
        }

        string normalized;
        try
        {
            normalized = raw.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return TextCheck.Fail(TextCheckFailure.InvalidCharacters);
        }

        if (!AllCharactersAcceptable(normalized, allowLineFeed))
        {
            return TextCheck.Fail(TextCheckFailure.InvalidCharacters);
        }

        var trimmed = allowLineFeed ? normalized.Trim(' ', '\n') : normalized.Trim(' ');
        if (trimmed.Length == 0)
        {
            return TextCheck.Ok(null);
        }

        // Second pass on the trimmed value (SEC-GOV158I1-01) and the length cap in UTF-16 units (SEC-GOV158I1-02).
        if (!AllCharactersAcceptable(trimmed, allowLineFeed))
        {
            return TextCheck.Fail(TextCheckFailure.InvalidCharacters);
        }

        return trimmed.Length > maxLength
            ? TextCheck.Fail(TextCheckFailure.TooLong)
            : TextCheck.Ok(trimmed);
    }

    private static bool AllCharactersAcceptable(string text, bool allowLineFeed)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            Rune rune;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                rune = new Rune(c, text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                return false; // lone surrogate
            }
            else
            {
                rune = new Rune(c);
            }

            if (!IsAcceptable(rune, allowLineFeed))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAcceptable(Rune rune, bool allowLineFeed)
    {
        var value = rune.Value;
        if (value == ' ')
        {
            return true;
        }

        if (value == '\n')
        {
            return allowLineFeed;
        }

        // Explicit minimum list (variation selectors, joiners and marks that some Unicode versions classify as Mn/Cf).
        if (value is 0x034F or 0x061C or 0x00AD or 0x180E or 0x200E or 0x200F
            || (value >= 0xFE00 && value <= 0xFE0F)
            || (value >= 0xFFF9 && value <= 0xFFFB)
            || (value >= 0xE0000 && value <= 0xE007F)
            || (value >= 0xE0100 && value <= 0xE01EF))
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control => false,
            UnicodeCategory.Format => false,
            UnicodeCategory.Surrogate => false,
            UnicodeCategory.PrivateUse => false,
            UnicodeCategory.OtherNotAssigned => false,
            UnicodeCategory.LineSeparator => false,
            UnicodeCategory.ParagraphSeparator => false,
            UnicodeCategory.SpaceSeparator => false,
            _ => true
        };
    }
}

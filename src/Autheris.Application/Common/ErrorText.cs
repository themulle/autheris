namespace Autheris.Application.Common;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Guard for operator-declared text that may reach an error body (NL-SC-30): control and format characters are stripped, the
/// text is trimmed and length-capped, and an empty result yields the caller's fixed fallback.
/// </summary>
public static class ErrorText
{
    public static string Sanitize(string? text, string fallback, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var sb = new StringBuilder(Math.Min(text.Length, maxLength));
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                continue;
            }

            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            sb.Append(c);
            if (sb.Length >= maxLength)
            {
                break;
            }
        }

        var result = sb.ToString().Trim();
        return result.Length == 0 ? fallback : result;
    }
}

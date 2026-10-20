namespace Autheris.Application.Catalog.Search;

using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Fast diacritic folding and normalization with zero-allocation ASCII fast-path.
/// Emits German umlaut expansions and NFD accent-stripped forms (SEC-CAT146-07).
/// </summary>
public static class DiacriticNormalizer
{
    public static string NormalizeAndFold(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        ReadOnlySpan<char> span = text.AsSpan();
        if (Ascii.IsValid(span))
        {
            bool hasUpper = false;
            foreach (char ch in span)
            {
                if (char.IsAsciiLetterUpper(ch))
                {
                    hasUpper = true;
                    break;
                }
            }

            return hasUpper ? text.ToLowerInvariant() : text;
        }

        var expanded = ExpandGermanUmlauts(text);
        return StripDiacritics(expanded).ToLowerInvariant();
    }

    public static string ExpandGermanUmlauts(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool prevUpper = i > 0 && char.IsUpper(text[i - 1]);
            bool nextUpper = i + 1 < text.Length && char.IsUpper(text[i + 1]);
            bool isAllUpperContext = prevUpper || nextUpper;

            switch (c)
            {
                case 'ä':
                    sb.Append("ae");
                    break;
                case 'Ä':
                    sb.Append(nextUpper ? "AE" : "Ae");
                    break;
                case 'ö':
                    sb.Append("oe");
                    break;
                case 'Ö':
                    sb.Append(nextUpper ? "OE" : "Oe");
                    break;
                case 'ü':
                    sb.Append("ue");
                    break;
                case 'Ü':
                    sb.Append(nextUpper ? "UE" : "Ue");
                    break;
                case 'ß':
                    sb.Append(isAllUpperContext ? "SS" : "ss");
                    break;
                case 'ẞ':
                    sb.Append(isAllUpperContext ? "SS" : "Ss");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    public static string FoldGermanUmlauts(string text) => ExpandGermanUmlauts(text);

    public static string StripDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        if (Ascii.IsValid(text.AsSpan()))
        {
            return text;
        }

        var normalizedD = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalizedD.Length);
        foreach (char ch in normalizedD)
        {
            if (ch == 'Ł')
            {
                sb.Append('L');
            }
            else if (ch == 'ł')
            {
                sb.Append('l');
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string StripAccents(string text) => StripDiacritics(text);
}

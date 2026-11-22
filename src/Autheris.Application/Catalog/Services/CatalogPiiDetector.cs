namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

public static partial class CatalogPiiDetector
{
    private static readonly string[] PiiKeywords = ["name", "email", "phone", "iban", "birth", "address", "lat", "lon"];

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b[A-Za-z]{2}\d{2}(?:[\s\-]?[A-Za-z0-9]){11,30}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"\b(?:\d[ \-]?){12,18}\d\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CardCandidatePattern();

    [GeneratedRegex(@"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b|\b\d{3}\.\d{3}\.\d{3}-\d{2}\b|\b[A-CEGHJ-PR-TW-Z]{2}\d{6}[A-D]\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex NationalIdPattern();

    public static bool IsPii(string columnName, bool classified = false)
    {
        if (classified) return true;
        if (string.IsNullOrWhiteSpace(columnName)) return false;
        var lower = columnName.ToLowerInvariant();
        return PiiKeywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    public static bool HasSuspectedPiiValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (EmailPattern().IsMatch(value))
        {
            return true;
        }

        foreach (Match match in IbanPattern().Matches(value))
        {
            if (IsValidIban(match.Value))
            {
                return true;
            }
        }

        foreach (Match match in CardCandidatePattern().Matches(value))
        {
            if (IsValidLuhnCard(match.Value))
            {
                return true;
            }
        }

        return NationalIdPattern().IsMatch(value);
    }

    public static (bool Suspected, IReadOnlyList<string> Columns) HasSuspectedPii(IEnumerable<IReadOnlyDictionary<string, object?>>? rows)
    {
        if (rows is null)
        {
            return (false, []);
        }

        HashSet<string>? cols = null;
        foreach (var row in rows)
        {
            if (row is null) continue;
            foreach (var (key, val) in row)
            {
                if (val is string s && HasSuspectedPiiValue(s))
                {
                    (cols ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(key);
                }
            }
        }

        return cols is { Count: > 0 }
            ? (true, cols.OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList())
            : (false, []);
    }

    public static bool JsonHasSuspectedPii(string? samplesJson)
    {
        if (string.IsNullOrWhiteSpace(samplesJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(samplesJson, new JsonDocumentOptions { MaxDepth = 32 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var row in doc.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object) continue;
                foreach (var prop in row.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        HasSuspectedPiiValue(prop.Value.GetString()))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool IsValidIban(string candidate)
    {
        Span<char> normalized = stackalloc char[34];
        var len = 0;
        foreach (var ch in candidate)
        {
            if (ch is ' ' or '-') continue;
            if (!char.IsLetterOrDigit(ch) || len >= 34) return false;
            normalized[len++] = char.ToUpperInvariant(ch);
        }

        if (len < 15 || !char.IsLetter(normalized[0]) || !char.IsLetter(normalized[1]) || !char.IsDigit(normalized[2]) || !char.IsDigit(normalized[3]))
        {
            return false;
        }

        int remainder = 0;
        for (int offset = 0; offset < len; offset++)
        {
            var ch = normalized[(offset + 4) % len];
            if (char.IsDigit(ch))
            {
                remainder = (remainder * 10 + (ch - '0')) % 97;
            }
            else
            {
                int val = ch - 'A' + 10;
                remainder = (remainder * 10 + (val / 10)) % 97;
                remainder = (remainder * 10 + (val % 10)) % 97;
            }
        }

        return remainder == 1;
    }

    private static bool IsValidLuhnCard(string candidate)
    {
        Span<int> digits = stackalloc int[19];
        var len = 0;
        bool hasSeparator = false;
        foreach (var ch in candidate)
        {
            if (ch is ' ' or '-')
            {
                hasSeparator = true;
                continue;
            }

            if (!char.IsDigit(ch) || len >= 19) return false;
            digits[len++] = ch - '0';
        }

        if (len < 13 || len > 19)
        {
            return false;
        }

        if (!hasSeparator && digits[0] is not (3 or 4 or 5 or 6))
        {
            return false;
        }

        int sum = 0;
        bool alt = false;
        for (int i = len - 1; i >= 0; i--)
        {
            int n = digits[i];
            if (alt)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }

            sum += n;
            alt = !alt;
        }

        return sum % 10 == 0;
    }
}

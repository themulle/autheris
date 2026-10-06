namespace Autheris.Application.Procedures.Interfaces;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Canonical text form of a (composite) row key, used to compare keys read from a procedure result with keys read back
/// from the table. Comparison is ordinal (fail-closed: a key that differs only by case or padding is not matched).
/// </summary>
public static class RowScopeKeys
{
    /// <summary>Returns null when any part is NULL (a NULL key never matches, as in SQL).</summary>
    public static string? Normalize(IReadOnlyList<object?> parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is null or DBNull)
            {
                return null;
            }

            string text = part switch
            {
                string s => s,
                bool b => b ? "1" : "0",
                DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset dto => dto.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                Guid g => g.ToString("D"),
                byte[] bytes => Convert.ToHexString(bytes),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => part.ToString() ?? string.Empty
            };

            // Length prefix keeps ("a|b","c") and ("a","b|c") apart.
            sb.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('|');
        }

        return sb.ToString();
    }
}

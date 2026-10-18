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

    /// <summary>
    /// Returns the display name safe for MCP output: whitespace and control characters become one space (before collapsing, so words never
    /// fuse), invisible and format characters (category Cf, variation selectors, the Tags block, private-use and unassigned code points) and
    /// invalid UTF-16 are removed, runs are collapsed, the result is trimmed and cut at <see cref="MaxDisplayNameLength"/> on a rune
    /// boundary. An empty result yields <paramref name="tableName"/>. Never throws.
    /// </summary>
    internal static string DisplayName(string? stored, string tableName)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return tableName;
        }

        var builder = new StringBuilder(Math.Min(stored.Length, MaxDisplayNameLength));
        var pendingSpace = false;
        foreach (var rune in stored.EnumerateRunes())
        {
            if (IsWordBreak(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (IsInvisible(rune))
            {
                continue;
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

            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? tableName : builder.ToString();
    }

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
        if (value is >= 0xFE00 and <= 0xFE0F or >= 0xE0000 and <= 0xE007F or 0x180E or 0x00AD or (>= 0x2060 and <= 0x2064))
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate;
    }
}

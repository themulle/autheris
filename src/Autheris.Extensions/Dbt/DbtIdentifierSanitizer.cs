namespace Autheris.Extensions.Dbt;

/// <summary>
/// Identifier rules for dbt relation import (issue #151, SEC-REL151-02): identifiers that reach logs, warnings or exceptions are
/// truncated and stripped of control characters; identifiers that are empty, too long or contain control characters are rejected.
/// </summary>
public static class DbtIdentifierSanitizer
{
    public const int MaxIdentifierLength = 256;
    public const int MaxLogLength = 128;

    /// <summary>True when the identifier is non-empty, at most <see cref="MaxIdentifierLength"/> characters and free of control characters.</summary>
    public static bool IsValidIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentifierLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Truncates to <see cref="MaxLogLength"/> characters and replaces control characters, so the text is safe to log or warn.</summary>
    public static string ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var length = Math.Min(value.Length, MaxLogLength);
        return string.Create(length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IsControl(source[i]) ? '_' : source[i];
            }
        });
    }

    private static bool IsControl(char c) => char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029;
}

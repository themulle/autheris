namespace Autheris.Domain.Diagnostics;

using System;

/// <summary>
/// Strips CRLF characters from untrusted strings before logging to prevent log forging (CWE-117).
/// </summary>
public static class LogSanitizer
{
    public static string Sanitize(string? value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                   .Replace("\n", string.Empty, StringComparison.Ordinal);
}

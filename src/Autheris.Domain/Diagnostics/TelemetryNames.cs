namespace Autheris.Domain.Diagnostics;

using System.Text.RegularExpressions;

/// <summary>
/// Normalization helpers for telemetry attribute names and dimension values.
/// </summary>
public static partial class TelemetryNames
{
    private static readonly Regex ValidDataSourceRegex = new(@"^[A-Za-z0-9_.-]{1,64}\z", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a data source identifier to enforce bounded cardinality and safe charset.
    /// Allowed charset: [A-Za-z0-9_.-], maximum length: 64. Returns "invalid" on failure or null/whitespace.
    /// </summary>
    public static string DataSource(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "invalid";
        }

        var trimmed = raw.Trim();
        return ValidDataSourceRegex.IsMatch(trimmed) ? trimmed : "invalid";
    }
}

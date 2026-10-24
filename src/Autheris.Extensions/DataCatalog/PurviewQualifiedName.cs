namespace Autheris.Extensions.DataCatalog;

using System;
using System.Diagnostics.CodeAnalysis;
using Autheris.Domain.Common;

/// <summary>
/// EXT-17: maps a Purview/Atlas qualifiedName to a <see cref="TableIdentifier"/> for both the sync and the webhook path.
/// The input is attacker-influenced: parsing is strict, fail-closed and never produces a partial or default identifier.
/// The scheme and host of a URI-style name are never used; the path segments are database/schema/table
/// (database becomes the domain, a missing schema becomes "dbo").
/// </summary>
internal static class PurviewQualifiedName
{
    internal const int MaxQualifiedNameLength = 512;
    internal const int MaxSegmentLength = 128;
    private const string SchemeSeparator = "://";

    public static bool TryParse(string? qualifiedName, out TableIdentifier id)
    {
        id = default;

        if (string.IsNullOrWhiteSpace(qualifiedName) || qualifiedName.Length > MaxQualifiedNameLength)
        {
            return false;
        }

        foreach (var c in qualifiedName)
        {
            if (char.IsControl(c) || c == '\\' || c == '?' || c == '#')
            {
                return false;
            }
        }

        string path;
        var schemeIdx = qualifiedName.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            if (!IsValidScheme(qualifiedName.AsSpan(0, schemeIdx)))
            {
                return false;
            }

            var rest = qualifiedName.AsSpan(schemeIdx + SchemeSeparator.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                // No host or no path.
                return false;
            }

            path = rest[(slash + 1)..].ToString();
        }
        else
        {
            if (qualifiedName.Contains(':'))
            {
                // Scheme-like junk without "://".
                return false;
            }

            path = qualifiedName;
        }

        var rawSegments = path.Split('/');
        if (rawSegments.Length is < 2 or > 3)
        {
            return false;
        }

        var segments = new string[rawSegments.Length];
        for (var i = 0; i < rawSegments.Length; i++)
        {
            if (!TryUnescapeSegment(rawSegments[i], out segments[i]))
            {
                return false;
            }
        }

        try
        {
            id = segments.Length == 3
                ? new TableIdentifier(segments[0], segments[1], segments[2])
                : new TableIdentifier(segments[0], "dbo", segments[1]);
            return true;
        }
        catch (ArgumentException)
        {
            id = default;
            return false;
        }
    }

    private static bool TryUnescapeSegment(string raw, out string segment)
    {
        segment = string.Empty;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxSegmentLength * 3)
        {
            return false;
        }

        string value;
        try
        {
            value = Uri.UnescapeDataString(raw);
        }
        catch (UriFormatException)
        {
            return false;
        }

        // Validate after unescape: encoded separators, control characters and traversal segments are rejected.
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSegmentLength || value is "." or "..")
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || c is '/' or '\\' or '%' or '?' or '#')
            {
                return false;
            }
        }

        segment = value;
        return true;
    }

    private static bool IsValidScheme(ReadOnlySpan<char> scheme)
    {
        if (scheme.IsEmpty || !char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}

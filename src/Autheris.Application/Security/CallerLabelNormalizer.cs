namespace Autheris.Application.Security;

using System;
using System.Buffers;
using System.Collections.Generic;

/// <summary>
/// Architecture Phase 1 / Issue #114: Normalizes untrusted caller telemetry headers
/// (X-Caller-Application, X-Caller-Version, X-Caller-Query-Name).
/// Enforces length limits, safe character allowlists, and CSV/spreadsheet formula injection guards.
/// </summary>
public static class CallerLabelNormalizer
{
    public const int MaxApplicationLength = 64;
    public const int MaxVersionLength = 32;
    public const int MaxQueryNameLength = 64;
    public const int MaxClientIdLength = 64;

    public const string UnknownSentinel = "unknown";
    public const string InvalidSentinel = "invalid";
    public const string OtherSentinel = "other";

    private static readonly SearchValues<char> SafeChars = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._ -");

    private static readonly SearchValues<char> SafeClientIdChars = SearchValues.Create(
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:-");

    private static readonly HashSet<string> ReservedSentinels = new(StringComparer.OrdinalIgnoreCase)
    {
        UnknownSentinel,
        InvalidSentinel,
        OtherSentinel
    };

    public static string NormalizeApplication(string? raw) =>
        NormalizeLabel(raw, MaxApplicationLength, SafeChars);

    public static string NormalizeVersion(string? raw) =>
        NormalizeLabel(raw, MaxVersionLength, SafeChars);

    public static string NormalizeQueryName(string? raw) =>
        NormalizeLabel(raw, MaxQueryNameLength, SafeChars);

    public static string NormalizeClientId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownSentinel;

        char rawFirst = raw[0];
        if (rawFirst is '=' or '+' or '-' or '@' or '\t' or '\r') return InvalidSentinel;

        var trimmed = raw.Trim(' ');
        if (trimmed.Length == 0) return UnknownSentinel;
        if (trimmed.Length > MaxClientIdLength) trimmed = trimmed[..MaxClientIdLength];

        char first = trimmed[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r') return InvalidSentinel;

        if (trimmed.AsSpan().ContainsAnyExcept(SafeClientIdChars)) return InvalidSentinel;
        if (ReservedSentinels.Contains(trimmed)) return InvalidSentinel;

        return trimmed.ToLowerInvariant();
    }

    private static string NormalizeLabel(string? raw, int maxLength, SearchValues<char> allowed)
    {
        if (string.IsNullOrWhiteSpace(raw)) return UnknownSentinel;

        char rawFirst = raw[0];
        if (rawFirst is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            return InvalidSentinel;
        }

        var trimmed = raw.Trim(' ');
        if (trimmed.Length == 0) return UnknownSentinel;

        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed[..maxLength];
        }

        // CSV / Spreadsheet formula injection guard
        char first = trimmed[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            return InvalidSentinel;
        }

        // Character validation against allowlist
        if (trimmed.AsSpan().ContainsAnyExcept(allowed))
        {
            return InvalidSentinel;
        }

        // Clients cannot supply reserved sentinels
        if (ReservedSentinels.Contains(trimmed))
        {
            return InvalidSentinel;
        }

        return trimmed.ToLowerInvariant();
    }

    public static string StripFormulaPrefix(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input.TrimStart('=', '+', '-', '@', '\t', '\r');
    }

    public static bool IsSafeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var trimmed = label.Trim();
        if (trimmed.Length > MaxApplicationLength) return false;
        char first = trimmed[0];
        if (first is '=' or '+' or '-' or '@' or '\t' or '\r') return false;
        return !trimmed.AsSpan().ContainsAnyExcept(SafeChars) && !ReservedSentinels.Contains(trimmed);
    }
}

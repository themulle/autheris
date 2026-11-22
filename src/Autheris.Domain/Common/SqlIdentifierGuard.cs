namespace Autheris.Domain.Common;

using System;
using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Canonical SQL identifier validation, normalization, and dialect-aware quoting helper.
/// </summary>
public static class SqlIdentifierGuard
{
    public const int DefaultMaxLength = 128;

    /// <summary>
    /// Validates a single-segment SQL identifier against ASCII <c>[A-Za-z_][A-Za-z0-9_]*</c>
    /// (or <c>[A-Za-z0-9_]+</c> when <paramref name="allowLeadingDigit"/> is true).
    /// </summary>
    public static bool IsValidIdentifier(
        [NotNullWhen(true)] string? identifier,
        int? maxLength = DefaultMaxLength,
        bool allowLeadingDigit = false) =>
        identifier is not null && IsValidIdentifier(identifier.AsSpan(), maxLength, allowLeadingDigit);

    /// <summary>
    /// Span-based single-segment SQL identifier validation. Rejects empty values, leading digits (unless enabled),
    /// whitespace, control characters, quotes, newlines, dots, and non-ASCII characters.
    /// </summary>
    public static bool IsValidIdentifier(
        ReadOnlySpan<char> identifier,
        int? maxLength = DefaultMaxLength,
        bool allowLeadingDigit = false)
    {
        if (maxLength is int limit)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1, nameof(maxLength));
            if (identifier.Length > limit)
            {
                return false;
            }
        }

        if (identifier.IsEmpty)
        {
            return false;
        }

        char first = identifier[0];
        if (!IsAsciiLetterOrUnderscore(first) && !(allowLeadingDigit && IsAsciiDigit(first)))
        {
            return false;
        }

        for (int i = 1; i < identifier.Length; i++)
        {
            char c = identifier[i];
            if (!IsAsciiLetterOrUnderscore(c) && !IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates a single-segment SQL identifier or throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string ValidateIdentifier(
        string? identifier,
        string paramName = "identifier",
        int? maxLength = DefaultMaxLength,
        bool allowLeadingDigit = false,
        string? message = null)
    {
        if (!IsValidIdentifier(identifier, maxLength, allowLeadingDigit))
        {
            throw new ArgumentException(
                message ?? $"Invalid SQL identifier '{identifier}'. Only ASCII letters, digits, and underscores are allowed.",
                paramName);
        }

        return identifier!;
    }

    /// <summary>
    /// Validates a dot-qualified SQL identifier where every segment satisfies <see cref="IsValidIdentifier(ReadOnlySpan{char}, int?, bool)"/>.
    /// Empty segments (leading, trailing, or consecutive dots) are always rejected.
    /// </summary>
    public static bool IsValidQualifiedIdentifier(
        [NotNullWhen(true)] string? identifier,
        int minSegments = 1,
        int? maxSegments = null,
        int? maxSegmentLength = DefaultMaxLength,
        bool allowLeadingDigit = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minSegments, 1);
        if (maxSegments is int maxSeg)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maxSeg, minSegments, nameof(maxSegments));
        }

        if (string.IsNullOrEmpty(identifier))
        {
            return false;
        }

        ReadOnlySpan<char> remaining = identifier.AsSpan();
        int segmentCount = 0;
        while (true)
        {
            int dotIndex = remaining.IndexOf('.');
            ReadOnlySpan<char> segment = dotIndex < 0 ? remaining : remaining[..dotIndex];
            if (!IsValidIdentifier(segment, maxSegmentLength, allowLeadingDigit))
            {
                return false;
            }

            segmentCount++;
            if (maxSegments is int limit && segmentCount > limit)
            {
                return false;
            }

            if (dotIndex < 0)
            {
                break;
            }

            remaining = remaining[(dotIndex + 1)..];
        }

        return segmentCount >= minSegments;
    }

    /// <summary>
    /// Validates a dot-qualified SQL identifier or throws <see cref="ArgumentException"/>.
    /// </summary>
    public static string ValidateQualifiedIdentifier(
        string? identifier,
        string paramName = "identifier",
        int minSegments = 1,
        int? maxSegments = null,
        int? maxSegmentLength = DefaultMaxLength,
        bool allowLeadingDigit = false,
        string? message = null)
    {
        if (!IsValidQualifiedIdentifier(identifier, minSegments, maxSegments, maxSegmentLength, allowLeadingDigit))
        {
            throw new ArgumentException(
                message ?? $"Invalid qualified SQL identifier '{identifier}'.",
                paramName);
        }

        return identifier!;
    }

    /// <summary>
    /// Validates a single-segment SQL identifier strictly and quotes it for <paramref name="dialect"/>
    /// via <see cref="DatabaseDialectExtensions.QuoteIdentifier"/>.
    /// </summary>
    public static string QuoteSafeIdentifier(
        string? identifier,
        DatabaseDialect dialect,
        string paramName = "identifier",
        int? maxLength = DefaultMaxLength)
    {
        var validated = ValidateIdentifier(identifier, paramName, maxLength, allowLeadingDigit: false);
        return dialect.QuoteIdentifier(validated);
    }

    /// <summary>
    /// Validates a dot-qualified SQL identifier strictly and quotes each segment for <paramref name="dialect"/>
    /// via <see cref="DatabaseDialectExtensions.QuoteQualifiedColumn"/>.
    /// </summary>
    public static string QuoteSafeQualifiedIdentifier(
        string? identifier,
        DatabaseDialect dialect,
        string paramName = "identifier",
        int minSegments = 1,
        int? maxSegments = null,
        int? maxSegmentLength = DefaultMaxLength)
    {
        var validated = ValidateQualifiedIdentifier(
            identifier,
            paramName,
            minSegments,
            maxSegments,
            maxSegmentLength,
            allowLeadingDigit: false);

        return dialect.QuoteQualifiedColumn(validated);
    }

    /// <summary>
    /// Normalizes an untrusted label into an ASCII <c>[A-Za-z_][A-Za-z0-9_]*</c> identifier for local staging tables
    /// and views by replacing disallowed characters with <c>_</c> and prefixing <c>_</c> if the first character is a digit.
    /// </summary>
    public static string SanitizeIdentifier(string? identifier, string fallback = "col")
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return fallback;
        }

        bool needsLeadingUnderscore = IsAsciiDigit(identifier[0]);
        int targetLength = identifier.Length + (needsLeadingUnderscore ? 1 : 0);
        return string.Create(targetLength, (identifier, needsLeadingUnderscore), static (span, state) =>
        {
            int offset = 0;
            if (state.needsLeadingUnderscore)
            {
                span[0] = '_';
                offset = 1;
            }

            for (int i = 0; i < state.identifier.Length; i++)
            {
                char c = state.identifier[i];
                span[offset + i] = IsAsciiLetterOrUnderscore(c) || IsAsciiDigit(c) ? c : '_';
            }
        });
    }

    private static bool IsAsciiLetterOrUnderscore(char c) =>
        (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z') || c == '_';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}

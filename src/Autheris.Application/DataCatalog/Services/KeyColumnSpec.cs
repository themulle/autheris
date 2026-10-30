namespace Autheris.Application.DataCatalog.Services;

using System;
using Autheris.Domain.Model;

/// <summary>
/// Issue #390 (I390-SC-05): the one strict grammar for a key column token of an untrusted source (dbt constraint <c>columns</c>):
/// <c>column</c> or <c>column ASC</c> / <c>column DESC</c>. The token is trimmed and bounded before parsing. At most one trailing
/// whitespace-separated word is accepted and it must be exactly <c>ASC</c> or <c>DESC</c> (ordinal, ignoring case). With a direction the
/// remaining column text may only hold letters, digits, <c>_</c> and <c>$</c>, so quotes, brackets, dots, commas, parentheses,
/// <c>NULLS FIRST/LAST</c>, <c>COLLATE</c> and a second direction are refused. Anything else is not a key column (the caller drops the
/// whole definition; there is never a partial key and never an ascending fallback for an unparsable token).
/// </summary>
public static class KeyColumnSpec
{
    /// <summary>Upper bound of a token; checked before any parsing.</summary>
    public const int MaxTokenLength = 256;

    public static bool TryParse(string? token, out string column, out KeyColumnDirection direction)
    {
        column = string.Empty;
        direction = KeyColumnDirection.Ascending;
        if (token is null || token.Length > MaxTokenLength)
        {
            return false;
        }

        var trimmed = token.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        var split = trimmed.LastIndexOfAny(" \t\r\n");
        if (split < 0)
        {
            column = trimmed.ToString();
            return true;
        }

        var suffix = trimmed[(split + 1)..];
        var head = trimmed[..split].TrimEnd();
        if (head.IsEmpty || !KeyDirections.TryParse(suffix.ToString(), out var parsed) || !IsPlainIdentifier(head))
        {
            return false;
        }

        column = head.ToString();
        direction = parsed;
        return true;
    }

    private static bool IsPlainIdentifier(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }
}

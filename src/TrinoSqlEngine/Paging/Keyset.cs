namespace TrinoSqlEngine;

using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// The declared primary key of the one table a paged statement reads, supplied by the caller (never derived from client text).
/// The compiler decides whether the statement is eligible for keyset continuation and reports the outcome on the compiled result.
/// </summary>
public sealed record KeysetCandidate(ImmutableArray<string> KeyColumns);

/// <summary>
/// The compiler-resolved keyset shape of an eligible statement: key columns in ORDER BY order with their directions, the
/// output ordinal of each key in the result row, and a hash of the shape (names, directions and catalog types).
/// </summary>
public sealed record KeysetKeyInfo(ImmutableArray<string> Columns, ImmutableArray<bool> Descending, ImmutableArray<int> OutputOrdinals, string ShapeHash);

/// <summary>A server-issued continuation: the shape hash the values were issued for and the last row's key values.</summary>
public sealed record KeysetSeek(string ShapeHash, ImmutableArray<object> Values);

/// <summary>The seek continuation does not fit the statement. Fail closed; the message carries no value and no column name.</summary>
public sealed class KeysetSeekRejectedException : SecurityException
{
    public KeysetSeekRejectedException()
        : base("The keyset continuation does not match the statement.")
    {
    }
}

/// <summary>Why a statement is not eligible for keyset continuation (the offset rules apply).</summary>
public static class KeysetUnavailableReasons
{
    public const string Shape = "shape";
    public const string NotPrimaryKey = "not_primary_key";
    public const string AliasShadow = "alias_shadow";
    public const string Window = "window";
    public const string KeyNotProjected = "key_not_projected";
    public const string KeyMasked = "key_masked";
    public const string KeyTooWide = "key_too_wide";
}

public static class KeysetLimits
{
    public const int MaxKeyColumns = 8;
}

internal static class KeysetShapeHash
{
    /// <summary>Lowercase hex of the first 8 bytes of SHA-256 over <c>column:a|d:catalogtype</c> joined with <c>|</c>.</summary>
    public static string Compute(ImmutableArray<string> columns, ImmutableArray<bool> descending, ImmutableArray<string> catalogTypes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < columns.Length; i++)
        {
            if (i > 0) sb.Append('|');
            sb.Append(columns[i].ToLowerInvariant()).Append(':').Append(descending[i] ? 'd' : 'a').Append(':')
              .Append(catalogTypes[i].Trim().ToLowerInvariant());
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLower(CultureInfo.InvariantCulture);
    }
}

namespace Autheris.Application.Sql.Services;

using System;

/// <summary>
/// A SQL <c>LIKE</c> pattern (<c>%</c>, <c>_</c> and an optional single escape character) matched in memory by a small
/// hand-written matcher. The pattern is never translated to a regular expression or to SQL.
/// </summary>
internal sealed class TrinoLikePattern
{
    internal const int MaxPatternLength = 256;

    // Token kind per position: 0 = literal, 1 = any single character, 2 = any sequence.
    private readonly byte[] _kinds;
    private readonly char[] _literals;

    private TrinoLikePattern(byte[] kinds, char[] literals)
    {
        _kinds = kinds;
        _literals = literals;
    }

    /// <exception cref="NotSupportedException">The pattern is too long or uses the escape character incorrectly.</exception>
    public static TrinoLikePattern Compile(string pattern, char? escape)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > MaxPatternLength)
        {
            throw TrinoJdbcMetadataShapes.NotSupported();
        }

        var kinds = new byte[pattern.Length];
        var literals = new char[pattern.Length];
        var count = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (escape is { } esc && c == esc)
            {
                if (i + 1 >= pattern.Length)
                {
                    throw TrinoJdbcMetadataShapes.NotSupported();
                }

                var next = pattern[++i];
                if (next != '%' && next != '_' && next != esc)
                {
                    throw TrinoJdbcMetadataShapes.NotSupported();
                }

                kinds[count] = 0;
                literals[count++] = next;
            }
            else if (c == '%')
            {
                kinds[count++] = 2;
            }
            else if (c == '_')
            {
                kinds[count++] = 1;
            }
            else
            {
                kinds[count] = 0;
                literals[count++] = c;
            }
        }

        return new TrinoLikePattern(kinds.AsSpan(0, count).ToArray(), literals.AsSpan(0, count).ToArray());
    }

    /// <summary>Case-insensitive match of the whole value (same case rule as the equality filters of the metadata views).</summary>
    public bool IsMatch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var v = 0;
        var p = 0;
        var star = -1;
        var mark = 0;
        while (v < value.Length)
        {
            if (p < _kinds.Length && (_kinds[p] == 1 || (_kinds[p] == 0 && SameChar(_literals[p], value[v]))))
            {
                v++;
                p++;
            }
            else if (p < _kinds.Length && _kinds[p] == 2)
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < _kinds.Length && _kinds[p] == 2)
        {
            p++;
        }

        return p == _kinds.Length;
    }

    private static bool SameChar(char a, char b) =>
        a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
}

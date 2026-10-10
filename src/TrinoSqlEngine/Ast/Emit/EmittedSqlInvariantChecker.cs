namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;

public enum EmittedRangeKind
{
    /// <summary>Inline structural number (row count, ordinal, frame offset, type parameter) or a numeric constant.</summary>
    Numeric,

    /// <summary>Constant text of a reviewed generator template (for example the HASHBYTES algorithm literal). Fully skipped.</summary>
    ConstantFragment
}

/// <summary>A region of the emitted text the emitter registered as structural (see plan 3.4).</summary>
public readonly record struct EmittedRange(int Start, int Length, EmittedRangeKind Kind);

/// <summary>The emitted SQL violates an output invariant (INV-4, INV-6, INV-13). Fail closed; the text is never executed.</summary>
public sealed class EmittedSqlInvariantViolationException : SecurityException
{
    public string Rule { get; }

    public EmittedSqlInvariantViolationException(string rule, int position)
        : base($"Emitted SQL violates invariant '{rule}' at position {position}.")
    {
        Rule = rule;
    }
}

/// <summary>
/// Scans emitted SQL with a dialect-aware lexer (production, on every compile). It rejects quotes, comments, semicolons,
/// dollar quotes and unregistered numeric tokens outside delimited identifiers, and proves that markers and bound
/// parameters correspond one to one.
/// </summary>
public static class EmittedSqlInvariantChecker
{
    public const int MaxEmittedSqlLength = 1 << 20;

    public static void Check(
        string sql,
        TargetSqlDialect dialect,
        ImmutableArray<BoundParameter> parameters,
        IReadOnlyList<EmittedRange> ranges,
        bool structuralOnly = false)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(ranges);

        if (sql.Length > MaxEmittedSqlLength)
        {
            throw new SqlLimitExceededException(SqlLimitKind.EmittedSqlLength, dialect, sql.Length, MaxEmittedSqlLength);
        }

        if (dialect != TargetSqlDialect.SqlServer)
        {
            throw new ArgumentOutOfRangeException(nameof(dialect), dialect, "No emitted-text lexer exists for this dialect.");
        }

        var sorted = new EmittedRange[ranges.Count];
        for (int r = 0; r < sorted.Length; r++) sorted[r] = ranges[r];
        Array.Sort(sorted, static (a, b) => a.Start.CompareTo(b.Start));

        var markersInText = new HashSet<string>(StringComparer.Ordinal);
        int i = 0;
        while (i < sql.Length)
        {
            if (TryFindRange(sorted, i, out var range) && range.Kind == EmittedRangeKind.ConstantFragment && range.Start == i)
            {
                i = range.Start + range.Length;
                continue;
            }

            char c = sql[i];
            if (c == '[')
            {
                i = SkipBracketIdentifier(sql, i);
                continue;
            }

            if (IsTokenChar(c))
            {
                int start = i;
                while (i < sql.Length && IsTokenChar(sql[i])) i++;
                var token = sql.AsSpan(start, i - start);
                if (IsMarker(token))
                {
                    markersInText.Add(token.ToString());
                }
                else if (!structuralOnly && char.IsAsciiDigit(token[0]) && !IsNumericAllowed(sorted, start, i - start))
                {
                    throw new EmittedSqlInvariantViolationException("unregistered-numeric-token", start);
                }

                continue;
            }

            if (!structuralOnly)
            {
                switch (c)
                {
                    case '\'':
                    case '"':
                    case ';':
                    case '$':
                    case '`':
                        throw new EmittedSqlInvariantViolationException("forbidden-character", i);
                    case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                    case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                        throw new EmittedSqlInvariantViolationException("comment", i);
                }

                if (c < ' ' && c != '\n' && c != '\r' && c != '\t')
                {
                    throw new EmittedSqlInvariantViolationException("control-character", i);
                }
            }

            i++;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parameters)
        {
            if (!declared.Add(p.Marker))
            {
                throw new EmittedSqlInvariantViolationException("duplicate-parameter-marker", 0);
            }

            if (!markersInText.Contains(p.Marker))
            {
                throw new EmittedSqlInvariantViolationException("parameter-without-marker", 0);
            }
        }

        foreach (var marker in markersInText)
        {
            if (!declared.Contains(marker))
            {
                throw new EmittedSqlInvariantViolationException("marker-without-parameter", 0);
            }
        }
    }

    // Identifier, keyword, marker (@p1, @@DATEFIRST), temp-table and number characters form one token, so digits inside
    // them are never mistaken for standalone numeric literals.
    private static bool IsTokenChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '#' || c > 127;

    private static bool IsMarker(ReadOnlySpan<char> token)
    {
        if (token.Length < 3 || token[0] != '@' || token[1] != 'p') return false;
        for (int k = 2; k < token.Length; k++)
        {
            if (!char.IsAsciiDigit(token[k])) return false;
        }

        return true;
    }

    private static int SkipBracketIdentifier(string sql, int start)
    {
        int i = start + 1;
        while (i < sql.Length)
        {
            if (sql[i] == ']')
            {
                if (i + 1 < sql.Length && sql[i + 1] == ']')
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        throw new EmittedSqlInvariantViolationException("unterminated-delimited-identifier", start);
    }

    // Ranges are sorted by start and never overlap; find the last range that starts at or before position.
    private static bool TryFindRange(EmittedRange[] sorted, int position, out EmittedRange range)
    {
        int lo = 0, hi = sorted.Length - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (sorted[mid].Start <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found >= 0 && position < sorted[found].Start + sorted[found].Length)
        {
            range = sorted[found];
            return true;
        }

        range = default;
        return false;
    }

    private static bool IsNumericAllowed(EmittedRange[] sorted, int start, int length)
    {
        if (!TryFindRange(sorted, start, out var range)) return false;
        return range.Kind == EmittedRangeKind.Numeric && start + length <= range.Start + range.Length;
    }
}

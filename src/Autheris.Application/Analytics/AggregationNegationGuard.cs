namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;

/// <summary>
/// MISC-09: <c>Analytics:Privacy:RestrictNegatedFilters</c>. With a minimum group size above 1, a WHERE predicate that negates (or isolates a
/// single row of) a column that is not a group-by column lets a caller difference two aggregations to expose one row
/// (<c>employee_id &lt;&gt; 7</c> against the unfiltered total). The guard rejects such predicates by tokenising the clause with the SQL lexer
/// (never by pattern matching on the text); an unparseable clause is rejected.
/// Note: k-anonymity without noise does not fully resist differencing; this guard narrows the most direct oracle.
/// </summary>
internal static partial class AggregationNegationGuard
{
    /// <summary>Raised as <see cref="AggregationFilterRejectedException"/> when the clause is rejected; returns normally otherwise.</summary>
    public static void Enforce(
        TableFilterClause? where,
        IReadOnlyList<string> groupByColumns,
        TableMetadata metadata,
        int effectiveMinGroupSize,
        bool restrictNegatedFilters)
    {
        if (where == null || !restrictNegatedFilters || effectiveMinGroupSize <= 1 || string.IsNullOrWhiteSpace(where.SqlPredicate))
        {
            return;
        }

        var tokens = Tokenize(where.SqlPredicate);
        if (tokens == null)
        {
            throw new AggregationFilterRejectedException();
        }

        var groupBy = new HashSet<string>(groupByColumns, StringComparer.OrdinalIgnoreCase);
        var isolating = IsolatingColumns(metadata);

        for (var i = 0; i < tokens.Count; i++)
        {
            var type = tokens[i].Type;
            var negates = type == SqlBaseLexer.NEQ
                || (type == SqlBaseLexer.NOT && !IsIsNotNull(tokens, i));
            var equals = type == SqlBaseLexer.EQ;
            if (!negates && !equals)
            {
                continue;
            }

            var (start, end) = type == SqlBaseLexer.NOT && i + 1 < tokens.Count && tokens[i + 1].Text == "("
                ? ParenScope(tokens, i + 1)
                : AtomScope(tokens, i);
            if (start < 0)
            {
                throw new AggregationFilterRejectedException();
            }

            foreach (var column in ColumnsIn(tokens, start, end, metadata))
            {
                if (groupBy.Contains(column))
                {
                    continue;
                }

                if (negates || isolating.Contains(column))
                {
                    throw new AggregationFilterRejectedException();
                }
            }
        }
    }

    /// <summary>Primary-key columns and single-column unique-index columns: an equality on them can isolate one row.</summary>
    private static HashSet<string> IsolatingColumns(TableMetadata metadata)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (metadata.PrimaryKeyColumns.Count == 1)
        {
            set.Add(metadata.PrimaryKeyColumns[0]);
        }

        foreach (var index in metadata.Indexes)
        {
            if (index.IsUnique && index.Columns.Count == 1)
            {
                set.Add(index.Columns[0]);
            }
        }

        return set;
    }

    private static bool IsIsNotNull(List<IToken> tokens, int notIndex) =>
        notIndex > 0 && tokens[notIndex - 1].Type == SqlBaseLexer.IS
        && notIndex + 1 < tokens.Count && tokens[notIndex + 1].Type == SqlBaseLexer.NULL;

    /// <summary>Default-channel tokens of the predicate, or null when the text cannot be tokenised cleanly (fail closed).</summary>
    private static List<IToken>? Tokenize(string sql)
    {
        // Bound-parameter placeholders (@gq1) are not SQL tokens; they become '?' before lexing.
        var normalized = PlaceholderRegex().Replace(sql, "?");
        try
        {
            var lexer = new SqlBaseLexer(new ZeroCopyCaseInsensitiveStream(normalized.AsMemory()));
            lexer.RemoveErrorListeners();
            var result = new List<IToken>();
            var depth = 0;
            foreach (var token in lexer.GetAllTokens())
            {
                if (token.Channel != TokenConstants.DefaultChannel)
                {
                    continue;
                }

                if (token.Type == SqlBaseLexer.UNRECOGNIZED)
                {
                    return null;
                }

                if (token.Text == "(")
                {
                    depth++;
                }
                else if (token.Text == ")" && --depth < 0)
                {
                    return null;
                }

                result.Add(token);
            }

            return depth == 0 ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    /// <summary>The tokens between the parenthesis at <paramref name="open"/> and its match (exclusive); (-1, -1) when unbalanced.</summary>
    private static (int Start, int End) ParenScope(List<IToken> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(")
            {
                depth++;
            }
            else if (tokens[i].Text == ")" && --depth == 0)
            {
                return (open + 1, i);
            }
        }

        return (-1, -1);
    }

    /// <summary>
    /// The predicate atom around <paramref name="index"/>: bounded by AND/OR at the same parenthesis depth or by an enclosing parenthesis.
    /// The AND of a <c>BETWEEN x AND y</c> belongs to its atom.
    /// </summary>
    private static (int Start, int End) AtomScope(List<IToken> tokens, int index)
    {
        var start = index;
        var depth = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            var text = tokens[i].Text;
            if (text == ")")
            {
                depth++;
            }
            else if (text == "(")
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && tokens[i].Type is SqlBaseLexer.AND or SqlBaseLexer.OR && !IsBetweenAnd(tokens, i))
            {
                break;
            }

            start = i;
        }

        var end = index + 1;
        depth = 0;
        var betweenPending = tokens[index].Type == SqlBaseLexer.BETWEEN
            || (tokens[index].Type == SqlBaseLexer.NOT && index + 1 < tokens.Count && tokens[index + 1].Type == SqlBaseLexer.BETWEEN);
        for (var i = index + 1; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (text == "(")
            {
                depth++;
            }
            else if (text == ")")
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (depth == 0 && tokens[i].Type == SqlBaseLexer.BETWEEN)
            {
                betweenPending = true;
            }
            else if (depth == 0 && tokens[i].Type is SqlBaseLexer.AND or SqlBaseLexer.OR)
            {
                if (betweenPending && tokens[i].Type == SqlBaseLexer.AND)
                {
                    betweenPending = false;
                }
                else
                {
                    break;
                }
            }

            end = i + 1;
        }

        return (start, end);
    }

    private static bool IsBetweenAnd(List<IToken> tokens, int andIndex)
    {
        if (tokens[andIndex].Type != SqlBaseLexer.AND)
        {
            return false;
        }

        var depth = 0;
        for (var i = andIndex - 1; i >= 0; i--)
        {
            var text = tokens[i].Text;
            if (text == ")")
            {
                depth++;
            }
            else if (text == "(")
            {
                if (depth == 0)
                {
                    return false;
                }

                depth--;
            }
            else if (depth == 0)
            {
                if (tokens[i].Type == SqlBaseLexer.BETWEEN)
                {
                    return true;
                }

                if (tokens[i].Type is SqlBaseLexer.AND or SqlBaseLexer.OR)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> ColumnsIn(List<IToken> tokens, int start, int end, TableMetadata metadata)
    {
        for (var i = start; i < end; i++)
        {
            var token = tokens[i];
            string? name = token.Type switch
            {
                SqlBaseLexer.IDENTIFIER or SqlBaseLexer.DIGIT_IDENTIFIER => token.Text,
                SqlBaseLexer.QUOTED_IDENTIFIER => token.Text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
                SqlBaseLexer.BACKQUOTED_IDENTIFIER => token.Text[1..^1].Replace("``", "`", StringComparison.Ordinal),
                // A column whose name the grammar knows as a (non-reserved) keyword is still a column.
                _ => metadata.GetColumn(token.Text) != null && IsWordToken(token) && !IsStructural(token.Type) ? token.Text : null
            };
            if (name == null)
            {
                continue;
            }

            // A function name (identifier followed by '(') is not a column.
            if (i + 1 < tokens.Count && tokens[i + 1].Text == "(")
            {
                continue;
            }

            yield return metadata.GetColumn(name)?.ColumnName ?? name;
        }
    }

    private static bool IsWordToken(IToken token) => token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] == '_');

    private static bool IsStructural(int type) =>
        type is SqlBaseLexer.AND or SqlBaseLexer.OR or SqlBaseLexer.NOT or SqlBaseLexer.IN or SqlBaseLexer.LIKE or SqlBaseLexer.BETWEEN
            or SqlBaseLexer.IS or SqlBaseLexer.NULL or SqlBaseLexer.TRUE or SqlBaseLexer.FALSE or SqlBaseLexer.DISTINCT or SqlBaseLexer.FROM
            or SqlBaseLexer.ESCAPE;
}

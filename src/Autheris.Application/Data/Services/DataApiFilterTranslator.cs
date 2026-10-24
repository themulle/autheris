namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Safe translator for REST Data API filter and order-by expressions with Zero-Trust column tracking.
/// Prevents SQL injection and extracts referenced columns for Oracle Inference guardrails.
/// </summary>
public static partial class DataApiFilterTranslator
{
    private static readonly HashSet<string> ReservedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "ge", "lt", "le", "and", "or", "not", "null", "true", "false", "is", "in", "like"
    };

    [GeneratedRegex(@"'([^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex StringLiteralRegex();

    [GeneratedRegex(@"[a-zA-Z_][a-zA-Z0-9_]*", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?<![a-zA-Z0-9_.""])(startswith|endswith|contains)\s*\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StringFunctionHeadRegex();

    [GeneratedRegex(@"\beq\s+null\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex EqNullRegex();

    [GeneratedRegex(@"\bne\s+null\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex NeNullRegex();

    [GeneratedRegex(@"^\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex StringFunctionTailRegex();

    public static IReadOnlyList<string> ExtractReferencedColumns(string expression, TableMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Array.Empty<string>();
        }

        // #323: the string functions are looked at in their LIKE form, so the function names are never taken for columns.
        var withoutStrings = StringLiteralRegex().Replace(RewriteStringFunctionsInText(expression), " '' ");

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = IdentifierRegex().Matches(withoutStrings);

        foreach (Match match in matches)
        {
            var word = match.Value;
            if (ReservedKeywords.Contains(word))
            {
                continue;
            }

            // The ESCAPE keyword the rewrite emits right after a pattern literal (only there; never a column).
            if (string.Equals(word, "ESCAPE", StringComparison.Ordinal) &&
                withoutStrings.AsSpan(0, match.Index).TrimEnd().EndsWith("''", StringComparison.Ordinal))
            {
                continue;
            }

            var col = metadata.GetColumn(word);
            if (col != null)
            {
                referenced.Add(col.ColumnName);
            }
            else
            {
                // In case column was passed directly matching case-insensitively
                referenced.Add(word);
            }
        }

        return referenced.ToList();
    }

    public static IReadOnlyList<string> ExtractOrderByColumns(string orderBy, TableMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(orderBy))
        {
            return Array.Empty<string>();
        }

        var columns = new List<string>();
        var parts = orderBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length > 0)
            {
                var candidate = tokens[0];
                var col = metadata.GetColumn(candidate);
                columns.Add(col != null ? col.ColumnName : candidate);
            }
        }

        return columns;
    }

    public static string TranslateFilterToSql(string filterExpression)
    {
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return string.Empty;
        }

        // Tokenize and replace operators while preserving string literals
        var pieces = SegmentLiterals(filterExpression);
        RewriteStringFunctions(pieces);

        var sb = new StringBuilder();
        foreach (var (text, isLiteral) in pieces)
        {
            sb.Append(isLiteral ? text : ReplaceOperators(text));
        }

        return sb.ToString().Trim();
    }

    private static List<(string Text, bool IsLiteral)> SegmentLiterals(string expression)
    {
        var pieces = new List<(string Text, bool IsLiteral)>();
        int lastIndex = 0;
        foreach (Match match in StringLiteralRegex().Matches(expression))
        {
            pieces.Add((expression[lastIndex..match.Index], false));
            pieces.Add((match.Value, true)); // preserve exact string literal
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < expression.Length)
        {
            pieces.Add((expression[lastIndex..], false));
        }

        return pieces;
    }

    private static string RewriteStringFunctionsInText(string expression)
    {
        var pieces = SegmentLiterals(expression);
        RewriteStringFunctions(pieces);
        var sb = new StringBuilder(expression.Length + 16);
        foreach (var (text, _) in pieces)
        {
            sb.Append(text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// #323 (SEC-RG-14): <c>startswith|endswith|contains(identifier, 'literal')</c> becomes <c>identifier LIKE 'pattern' ESCAPE '!'</c>. The
    /// literal is escaped (<c>!</c>, <c>%</c>, <c>_</c> get an <c>!</c> prefix; <c>''</c> stays one quote) so a caller cannot widen the pattern. The escape
    /// character is not a backslash: the SQL token policy rejects every backslash in a string literal. Only that exact shape
    /// is rewritten and only on a real string literal (never inside one); any other argument shape stays untouched and fails as before.
    /// </summary>
    private static void RewriteStringFunctions(List<(string Text, bool IsLiteral)> pieces)
    {
        for (int i = 1; i + 1 < pieces.Count; i++)
        {
            if (!pieces[i].IsLiteral)
            {
                continue;
            }

            var head = StringFunctionHeadRegex().Match(pieces[i - 1].Text);
            var tail = StringFunctionTailRegex().Match(pieces[i + 1].Text);
            if (!head.Success || !tail.Success)
            {
                continue;
            }

            string function = head.Groups[1].Value.ToLowerInvariant();
            string inner = EscapeLikeLiteral(pieces[i].Text[1..^1]);
            string pattern = function switch
            {
                "startswith" => $"'{inner}%'",
                "endswith" => $"'%{inner}'",
                _ => $"'%{inner}%'"
            };

            pieces[i - 1] = (pieces[i - 1].Text[..head.Index] + head.Groups[2].Value + " LIKE ", false);
            pieces[i] = (pattern, true);
            pieces.Insert(i + 1, (" ESCAPE ", false));
            pieces.Insert(i + 2, ("'!'", true));
            pieces[i + 3] = (pieces[i + 3].Text[tail.Length..], false);
            i += 2;
        }
    }

    private static string EscapeLikeLiteral(string literalContent)
    {
        var sb = new StringBuilder(literalContent.Length + 4);
        foreach (char c in literalContent)
        {
            if (c is '!' or '%' or '_')
            {
                sb.Append('!');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>Quotes one identifier as a Trino delimited identifier (embedded quotes doubled).</summary>
    public static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    /// <summary>
    /// Like <see cref="TranslateFilterToSql"/>, but every word that names a catalog column is emitted as a quoted
    /// identifier of the catalog spelling (WP-X0n). String literals and operator words are left untouched.
    /// </summary>
    public static string TranslateFilterToTrino(string filterExpression, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var translated = TranslateFilterToSql(filterExpression);
        if (translated.Length == 0)
        {
            return translated;
        }

        var sb = new StringBuilder(translated.Length + 16);
        int lastIndex = 0;
        foreach (Match literal in StringLiteralRegex().Matches(translated))
        {
            sb.Append(QuoteColumnWords(translated[lastIndex..literal.Index], metadata, afterLiteral: lastIndex > 0));
            sb.Append(literal.Value);
            lastIndex = literal.Index + literal.Length;
        }

        sb.Append(QuoteColumnWords(translated[lastIndex..], metadata, afterLiteral: lastIndex > 0));
        return sb.ToString();
    }

    /// <summary>
    /// Builds the ORDER BY item list from the request text. Each item is <c>column [ASC|DESC]</c> over a catalog column;
    /// anything else is rejected, so no request text reaches the SQL unquoted.
    /// </summary>
    public static string BuildOrderByClause(string orderBy, TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var items = new List<string>();
        foreach (var part in orderBy.Split(',', StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var column = tokens.Length is 1 or 2 ? metadata.GetColumn(tokens[0]) : null;
            string? direction = null;
            var directionValid = tokens.Length != 2
                || (direction = tokens[1].ToUpperInvariant()) is "ASC" or "DESC";
            if (column == null || !directionValid)
            {
                throw new ArgumentException($"Invalid orderBy item '{part}'. Use '<column> [asc|desc]' over the columns of the dataset.", nameof(orderBy));
            }

            items.Add(direction == null ? QuoteIdentifier(column.ColumnName) : QuoteIdentifier(column.ColumnName) + " " + direction);
        }

        return string.Join(", ", items);
    }

    /// <summary>
    /// Parses the request text <c>column [ASC|DESC][, ...]</c> into neutral order items for the table-read planner. The column
    /// is kept as written (the planner resolves and authorizes it against the catalog); a malformed item is rejected.
    /// </summary>
    public static IReadOnlyList<TableOrderBy> ParseOrderBy(string orderBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderBy);
        var items = new List<TableOrderBy>();
        foreach (var part in orderBy.Split(',', StringSplitOptions.TrimEntries))
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? direction = null;
            var valid = tokens.Length is 1 or 2
                && (tokens.Length != 2 || (direction = tokens[1].ToUpperInvariant()) is "ASC" or "DESC");
            if (!valid)
            {
                throw new ArgumentException($"Invalid orderBy item '{part}'. Use '<column> [asc|desc]' over the columns of the dataset.", nameof(orderBy));
            }

            items.Add(new TableOrderBy(tokens[0], direction == "DESC"));
        }

        return items;
    }

    private static string QuoteColumnWords(string text, TableMetadata metadata, bool afterLiteral) =>
        IdentifierRegex().Replace(text, m =>
        {
            // #323: the ESCAPE keyword the translator emits right after a LIKE pattern literal (only there; never a column).
            if (afterLiteral && string.Equals(m.Value, "ESCAPE", StringComparison.Ordinal) && text.AsSpan(0, m.Index).IsWhiteSpace())
            {
                return m.Value;
            }

            // Not a word inside a number (1e5) or a qualified name.
            if (m.Index > 0 && (char.IsLetterOrDigit(text[m.Index - 1]) || text[m.Index - 1] is '_' or '.' or '"'))
            {
                return m.Value;
            }

            if (ReservedKeywords.Contains(m.Value))
            {
                return m.Value;
            }

            var column = metadata.GetColumn(m.Value);
            return column == null ? m.Value : QuoteIdentifier(column.ColumnName);
        });

    private static string ReplaceOperators(string text)
    {
        // #324 (SEC-RG-10): "= NULL" never matches (3VL); a comparison with the null keyword is an IS [NOT] NULL test. Non-literal segments only.
        var result = EqNullRegex().Replace(text, "IS NULL");
        result = NeNullRegex().Replace(result, "IS NOT NULL");
        // Safe word-boundary operator replacements
        result = Regex.Replace(result, @"\beq\b", "=", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bne\b", "<>", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bge\b", ">=", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\ble\b", "<=", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bgt\b", ">", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\blt\b", "<", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\band\b", "AND", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bor\b", "OR", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\bnot\b", "NOT", RegexOptions.IgnoreCase);
        return result;
    }
}

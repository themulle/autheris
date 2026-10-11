namespace Autheris.Application.Data.Services;

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
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

    public static IReadOnlyList<string> ExtractReferencedColumns(string expression, TableMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return Array.Empty<string>();
        }

        // Replace string literals with placeholders to avoid matching identifiers inside strings
        var withoutStrings = StringLiteralRegex().Replace(expression, " '' ");

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = IdentifierRegex().Matches(withoutStrings);

        foreach (Match match in matches)
        {
            var word = match.Value;
            if (ReservedKeywords.Contains(word))
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
        var sb = new StringBuilder();
        var matches = StringLiteralRegex().Matches(filterExpression);
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            var nonLiteral = filterExpression[lastIndex..match.Index];
            sb.Append(ReplaceOperators(nonLiteral));
            sb.Append(match.Value); // preserve exact string literal
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < filterExpression.Length)
        {
            sb.Append(ReplaceOperators(filterExpression[lastIndex..]));
        }

        return sb.ToString().Trim();
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
            sb.Append(QuoteColumnWords(translated[lastIndex..literal.Index], metadata));
            sb.Append(literal.Value);
            lastIndex = literal.Index + literal.Length;
        }

        sb.Append(QuoteColumnWords(translated[lastIndex..], metadata));
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

    private static string QuoteColumnWords(string text, TableMetadata metadata) =>
        IdentifierRegex().Replace(text, m =>
        {
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
        // Safe word-boundary operator replacements
        var result = Regex.Replace(text, @"\beq\b", "=", RegexOptions.IgnoreCase);
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

namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

/// <summary>
/// Evaluates and applies row-level security filters and mandatory virtual filters in-memory for non-SQL or mock data sources.
/// Enforces type-strict comparisons and fail-closed security invariants.
/// </summary>
public static partial class InMemoryRowFilterEvaluator
{
    [GeneratedRegex(@"(?:\[[a-zA-Z0-9_]+\]|[a-zA-Z_][a-zA-Z0-9_]*)\.(\[?[a-zA-Z_][a-zA-Z0-9_]*\]?)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TablePrefixRegex();

    [GeneratedRegex(@"""([a-zA-Z0-9_]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DoubleQuotedIdentifierRegex();

    [GeneratedRegex(@"\(\s*([a-zA-Z0-9_, \[\]]+)\s*\)\s+IN\s*\(\s*(\(.*?\))\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TupleInRegex();

    [GeneratedRegex(@"(?<col>\[[^\]]+\]|""[^""]+""|\b[A-Za-z_][A-Za-z0-9_]*\b)\s*(?:NOT\s+)?(?<op>=|<>|!=|<=|>=|<|>|\bIN\b|\bLIKE\b)\s*(?<rhs>\((?:[^()']|'(?:[^']|'')*')*\)|'(?:[^']|'')*'|-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ColumnVsLiteralRegex();

    [GeneratedRegex(@"'(?:[^']|'')*'|-?\d+(?:\.\d+)?", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LiteralTokenRegex();

    /// <summary>
    /// Virtual filters: an in-memory row filter cannot evaluate their subqueries (it would return nothing and look like
    /// "no data"). Sources that filter in memory refuse the request instead (403 with reason).
    /// </summary>
    public static void EnsureInMemoryFilterIsEnforceable(TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.MandatoryRowPredicateSql != null)
        {
            throw new GatewayForbiddenException(
                $"Virtual filters ({string.Join(", ", decision.AppliedVirtualFilters ?? [])}) cannot be enforced on this data source; it does not filter in the database.");
        }
    }

    public static List<IReadOnlyDictionary<string, object?>> FilterRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        string rowFilterSql,
        TableMetadata metadata)
    {
        if (rows.Count == 0 || string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return rows;
        }

        var normalizedSql = NormalizeRowFilterForInMemoryEvaluation(rowFilterSql);
        if (string.IsNullOrWhiteSpace(normalizedSql))
        {
            // Zero Trust: When a row filter is defined but cannot be safely evaluated in-memory, fail closed
            return new List<IReadOnlyDictionary<string, object?>>();
        }

        try
        {
            // Review E-6: SQL compares ordinally on the governed sources; DataTable defaults to case-insensitive.
            using var dt = new DataTable { CaseSensitive = true };

            // Review E-6: a row that lacks a column the filter refers to must not count as NULL (IS NULL would match).
            var referencedColumns = metadata.Columns
                .Select(c => c.ColumnName)
                .Where(n => Regex.IsMatch(
                    normalizedSql, @"(?<![A-Za-z0-9_])\[?" + Regex.Escape(n) + @"\]?(?![A-Za-z0-9_])",
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)))
                .ToList();

            foreach (var col in metadata.Columns)
            {
                Type colType = col.DataType.ToLowerInvariant() switch
                {
                    var d when d.Contains("bigint") || d.Contains("long") => typeof(long),
                    var d when d.Contains("int") => typeof(int),
                    var d when d.Contains("decimal") || d.Contains("numeric") || d.Contains("money") => typeof(decimal),
                    var d when d.Contains("float") || d.Contains("double") || d.Contains("real") => typeof(double),
                    var d when d.Contains("bool") => typeof(bool),
                    var d when d.Contains("date") || d.Contains("time") => typeof(DateTime),
                    _ => typeof(string)
                };
                dt.Columns.Add(col.ColumnName, colType);
            }

            // Review E-5/E-6: DataTable.Select coerces ('007' = 7 on an int column, 7 = '7' on a string column) and knows
            // LIKE wildcards SQL does not. Filters that rely on either are refused (fail-closed).
            if (!IsTypeStrictRowFilter(normalizedSql, dt))
            {
                return new List<IReadOnlyDictionary<string, object?>>();
            }

            var rowMap = new Dictionary<DataRow, IReadOnlyDictionary<string, object?>>();
            foreach (var r in rows)
            {
                if (referencedColumns.Any(n => !r.ContainsKey(n)))
                {
                    continue; // fail-closed: the filter cannot be evaluated for this row
                }

                var dr = dt.NewRow();
                foreach (var col in metadata.Columns)
                {
                    if (r.TryGetValue(col.ColumnName, out var v) && v != null)
                    {
                        if (v is DateTimeOffset dto)
                        {
                            dr[col.ColumnName] = dto.UtcDateTime;
                        }
                        else
                        {
                            dr[col.ColumnName] = v;
                        }
                    }
                    else
                    {
                        dr[col.ColumnName] = DBNull.Value;
                    }
                }
                dt.Rows.Add(dr);
                rowMap[dr] = r;
            }

            var matchedDataRows = dt.Select(normalizedSql);
            return matchedDataRows.Select(dr => rowMap[dr]).ToList();
        }
        catch
        {
            // Strict Fail-Closed if expression cannot be evaluated
            return new List<IReadOnlyDictionary<string, object?>>();
        }
    }

    /// <summary>
    /// Review E-5/E-6: true when every comparison of a column with a literal uses a literal of the column's own type
    /// (quoted for text columns, unquoted number for numeric columns) and no LIKE pattern uses DataTable-only wildcards.
    /// </summary>
    internal static bool IsTypeStrictRowFilter(string normalizedSql, DataTable table)
    {
        foreach (Match m in ColumnVsLiteralRegex().Matches(normalizedSql))
        {
            var colName = m.Groups["col"].Value.Trim('[', ']', '"');
            if (!table.Columns.Contains(colName))
            {
                continue;
            }

            var colType = table.Columns[colName]!.DataType;
            bool isLike = m.Groups["op"].Value.Equals("LIKE", StringComparison.OrdinalIgnoreCase);
            bool numericColumn = colType == typeof(long) || colType == typeof(int) || colType == typeof(decimal) || colType == typeof(double);
            bool textColumn = colType == typeof(string);

            foreach (Match lit in LiteralTokenRegex().Matches(m.Groups["rhs"].Value))
            {
                bool quoted = lit.Value[0] == '\'';
                if (isLike)
                {
                    // DataTable treats '*' and '[' as wildcards/escapes; in SQL they are plain characters.
                    if (!textColumn || !quoted || lit.Value.Contains('*') || lit.Value.Contains('['))
                    {
                        return false;
                    }

                    continue;
                }

                if ((numericColumn && quoted) || (textColumn && !quoted))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? NormalizeRowFilterForInMemoryEvaluation(string rowFilterSql)
    {
        if (string.IsNullOrWhiteSpace(rowFilterSql))
        {
            return null;
        }

        var trimmed = rowFilterSql.Trim();
        var trimmedUpper = trimmed.Trim('(', ')', ' ');

        // 1. Subqueries like EXISTS (...) cannot be evaluated against mock in-memory DataTables
        if (trimmedUpper.StartsWith("EXISTS", StringComparison.OrdinalIgnoreCase) ||
            trimmedUpper.StartsWith("NOT EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Protect string literals from regex mangling (e.g. 'john.doe@x.de' matching table.column regex)
        var literals = new List<string>();
        var sb = new StringBuilder();
        bool inStr = false;
        var curLit = new StringBuilder();

        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c == '\'')
            {
                if (inStr)
                {
                    // Check for escaped quote ''
                    if (i + 1 < trimmed.Length && trimmed[i + 1] == '\'')
                    {
                        curLit.Append("''");
                        i++;
                        continue;
                    }
                    inStr = false;
                    curLit.Append('\'');
                    var placeholder = $"__STR_LIT_{literals.Count}__";
                    literals.Add(curLit.ToString());
                    curLit.Clear();
                    sb.Append(placeholder);
                }
                else
                {
                    inStr = true;
                    curLit.Append('\'');
                }
            }
            else if (inStr)
            {
                curLit.Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        if (inStr)
        {
            // Unterminated string literal -> fail closed
            return null;
        }

        var normalized = sb.ToString();

        // 2. Strip table prefixes: [alias].[col] -> [col] or alias.col -> col
        normalized = TablePrefixRegex().Replace(normalized, "$1");

        // 3. Normalize double-quoted identifiers: "col" -> [col]
        normalized = DoubleQuotedIdentifierRegex().Replace(normalized, "[$1]");

        // 4. Expand Tuple-IN clauses: (col1, col2) IN ((v1, v2), (v3, v4)) -> ((([col1] = v1) AND ([col2] = v2)) OR ...)
        var tupleInRegex = TupleInRegex();

        while (true)
        {
            var match = tupleInRegex.Match(normalized);
            if (!match.Success)
            {
                break;
            }

            var colNames = match.Groups[1].Value
                .Split(',')
                .Select(c => c.Trim().Trim('[', ']'))
                .ToArray();

            var tuples = ParseTuples(match.Groups[2].Value);
            string replacement;

            if (tuples.Count == 0)
            {
                replacement = "(1 = 0)";
            }
            else
            {
                var orClauses = new List<string>();
                foreach (var tuple in tuples)
                {
                    var andClauses = new List<string>();
                    for (int i = 0; i < colNames.Length && i < tuple.Count; i++)
                    {
                        andClauses.Add($"([{colNames[i]}] = {tuple[i]})");
                    }
                    orClauses.Add($"({string.Join(" AND ", andClauses)})");
                }
                replacement = $"({string.Join(" OR ", orClauses)})";
            }

            normalized = normalized.Remove(match.Index, match.Length).Insert(match.Index, replacement);
        }

        // Restore string literals
        for (int i = 0; i < literals.Count; i++)
        {
            normalized = normalized.Replace($"__STR_LIT_{i}__", literals[i]);
        }

        return normalized;
    }

    private static List<List<string>> ParseTuples(string tupleString)
    {
        var tuples = new List<List<string>>();
        var currentTuple = new List<string>();
        var currentVal = new StringBuilder();
        bool inQuote = false;
        bool inTuple = false;

        for (int i = 0; i < tupleString.Length; i++)
        {
            char c = tupleString[i];

            if (c == '\'' && (i == 0 || tupleString[i - 1] != '\\'))
            {
                inQuote = !inQuote;
                currentVal.Append(c);
            }
            else if (!inQuote && c == '(')
            {
                inTuple = true;
                currentTuple = new List<string>();
                currentVal.Clear();
            }
            else if (!inQuote && c == ')')
            {
                if (inTuple)
                {
                    var trimmed = currentVal.ToString().Trim();
                    if (trimmed.Length > 0)
                    {
                        currentTuple.Add(trimmed);
                    }
                    tuples.Add(currentTuple);
                    inTuple = false;
                    currentVal.Clear();
                }
            }
            else if (!inQuote && c == ',' && inTuple)
            {
                var trimmed = currentVal.ToString().Trim();
                currentTuple.Add(trimmed);
                currentVal.Clear();
            }
            else if (inTuple)
            {
                currentVal.Append(c);
            }
        }

        return tuples;
    }
}

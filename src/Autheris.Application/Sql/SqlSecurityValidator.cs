namespace Autheris.Application.Sql;

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;

/// <summary>
/// Centralized security validation for SQL fragments, filters, predicates, and RLS expressions.
/// Enforces Zero-Trust principles using AST parsing to prevent SQL injection, query stacking, comment breakouts,
/// and RLS bypasses in compiled AST and runtime data source queries.
/// </summary>
public static partial class SqlSecurityValidator
{
    private static readonly FastSqlEngine Engine = new() { MaxQueryLength = 4096 };

    // RR-L5-03: Fragments are validated with hardened token options instead of the permissive engine defaults.
    // Backslashes and non-ASCII identifiers stay allowed (Databricks literal escaping, localized column names).
    private static readonly SqlTokenSecurityOptions FragmentTokenOptions = new()
    {
        RejectComments = true,
        RejectEscapedStringLiterals = true,
        RejectDollarQuoting = true,
        RejectTimeTravelQueries = true
    };

    private static readonly ConcurrentDictionary<string, bool> ValidatedPredicatesCache = new(StringComparer.Ordinal);
    private const int MaxValidationCacheSize = 2000;

    [GeneratedRegex(@"@([a-zA-Z0-9_]+)")]
    private static partial Regex ParameterTokenRegex();

    /// <summary>
    /// Validates a SQL predicate or filter expression to ensure it is free from SQL injection,
    /// statement terminators, comment breakouts, and unbalanced syntax using grammar-level AST parsing.
    /// </summary>
    /// <summary>Maximum length of a row filter predicate.</summary>
    public const int MaxPredicateLength = 2000;

    /// <summary>
    /// Maximum length when the row filter contains gateway-generated virtual filter predicates (decision 9): several
    /// bindings on one object easily exceed <see cref="MaxPredicateLength"/>.
    /// </summary>
    public const int MaxPredicateLengthWithVirtualFilters = 8000;

    public static void ValidatePredicateSql(string? predicate, string fieldName)
        => ValidatePredicateSql(predicate, fieldName, allowSubqueries: true);

    /// <summary>The length limit that applies to the row filter of <paramref name="decision"/>.</summary>
    public static int MaxLengthFor(TableAccessDecision? decision) =>
        decision?.MandatoryRowPredicateSql != null ? MaxPredicateLengthWithVirtualFilters : MaxPredicateLength;

    /// <summary>Validates the combined row filter of <paramref name="decision"/> with the length limit that applies to it.</summary>
    public static void ValidateRowFilter(TableAccessDecision decision, string fieldName = "CombinedRowFilterSql")
    {
        ArgumentNullException.ThrowIfNull(decision);
        ValidatePredicateSql(decision.CombinedRowFilterSql, fieldName, allowSubqueries: true, MaxLengthFor(decision));
    }

    /// <summary>
    /// RR-L5-03: Same as <see cref="ValidatePredicateSql(string?, string)"/>; with <paramref name="allowSubqueries"/> = false
    /// any nested query (EXISTS / IN (SELECT ...) / scalar subquery) is rejected, so a filter can never probe other tables.
    /// </summary>
    public static void ValidatePredicateSql(string? predicate, string fieldName, bool allowSubqueries)
        => ValidatePredicateSql(predicate, fieldName, allowSubqueries, MaxPredicateLength);

    public static void ValidatePredicateSql(string? predicate, string fieldName, bool allowSubqueries, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return;
        }

        var cacheKey = (allowSubqueries ? "S:" : "N:") + maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + predicate;

        // Fast-path: Return immediately if already verified safe in hot-cache
        if (ValidatedPredicatesCache.ContainsKey(cacheKey))
        {
            return;
        }

        if (predicate.Length > maxLength)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' exceeds maximum allowed length of {maxLength} characters.", fieldName);
        }

        if (predicate.Contains('\0'))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited null byte.", fieldName);
        }

        if (predicate.Contains(';'))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited statement terminator ';'.", fieldName);
        }

        if (predicate.Contains("--") || predicate.Contains("/*") || predicate.Contains("*/"))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited comment sequence.", fieldName);
        }

        if (predicate.Contains("@@"))
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains prohibited SQL token '@@'.", fieldName);
        }

        // Fast normalization of query parameters (@p0, @tenant_id) to valid identifiers for AST verification
        string normalized = predicate.Contains('@')
            ? ParameterTokenRegex().Replace(predicate, "__param_$1")
            : predicate;

        // SQL Server bracket identifiers ([dbo].[customers], generated for correlated row filters) are not part of the
        // Trino grammar. They name the same identifier as a double-quoted one, so they are validated as such.
        if (normalized.Contains('['))
        {
            normalized = NormalizeBracketIdentifiers(normalized);
        }

        try
        {
            var (tree, _) = Engine.ParseExpression(normalized.AsMemory(), FragmentTokenOptions);
            if (tree == null || tree.expression() == null)
            {
                throw new ArgumentException($"SQL predicate in '{fieldName}' has invalid expression syntax.", fieldName);
            }

            if (!allowSubqueries && Antlr4.Runtime.Tree.Trees.FindAllRuleNodes(tree, SqlBaseParser.RULE_query).Count > 0)
            {
                throw new ArgumentException($"SQL predicate in '{fieldName}' must not contain subqueries.", fieldName);
            }

            // RR-L5-03: bounded cache without full clears (a flood of distinct filters must not cause cache thrashing).
            if (ValidatedPredicatesCache.Count < MaxValidationCacheSize)
            {
                ValidatedPredicatesCache[cacheKey] = true;
            }
        }
        catch (ParseCanceledException ex)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' contains invalid or unsafe SQL syntax: {ex.Message}", fieldName, ex);
        }
        catch (Exception ex) when (ex is not ArgumentException)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' could not be parsed: {ex.Message}", fieldName, ex);
        }
    }

    /// <summary>
    /// Rewrites strict bracket identifiers ([name], name = letters, digits, underscore) outside string literals into
    /// double-quoted identifiers. A bracket directly attached to an expression (identifier character, ')', ']' or '"'
    /// right before it) is a subscript and is left alone, as is any other bracket form; the grammar check then rejects
    /// or accepts it as before.
    /// </summary>
    internal static string NormalizeBracketIdentifiers(string predicate)
    {
        var sb = new StringBuilder(predicate.Length);
        bool inString = false;
        for (int i = 0; i < predicate.Length; i++)
        {
            char c = predicate[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\'')
                {
                    if (i + 1 < predicate.Length && predicate[i + 1] == '\'')
                    {
                        sb.Append(predicate[++i]);
                    }
                    else
                    {
                        inString = false;
                    }
                }
                continue;
            }

            if (c == '\'')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            char before = i > 0 ? predicate[i - 1] : ' ';
            if (c == '[' && !(char.IsAsciiLetterOrDigit(before) || before is '_' or ')' or ']' or '"'))
            {
                int end = i + 1;
                if (end < predicate.Length && (char.IsAsciiLetter(predicate[end]) || predicate[end] == '_'))
                {
                    while (end < predicate.Length && (char.IsAsciiLetterOrDigit(predicate[end]) || predicate[end] == '_'))
                    {
                        end++;
                    }

                    if (end < predicate.Length && predicate[end] == ']')
                    {
                        sb.Append('"').Append(predicate, i + 1, end - i - 1).Append('"');
                        i = end;
                        continue;
                    }
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

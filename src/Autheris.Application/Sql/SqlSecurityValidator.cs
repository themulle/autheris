namespace Autheris.Application.Sql;

using System;
using System.Collections.Concurrent;
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
    public static void ValidatePredicateSql(string? predicate, string fieldName)
        => ValidatePredicateSql(predicate, fieldName, allowSubqueries: true);

    /// <summary>
    /// RR-L5-03: Same as <see cref="ValidatePredicateSql(string?, string)"/>; with <paramref name="allowSubqueries"/> = false
    /// any nested query (EXISTS / IN (SELECT ...) / scalar subquery) is rejected, so a filter can never probe other tables.
    /// </summary>
    public static void ValidatePredicateSql(string? predicate, string fieldName, bool allowSubqueries)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return;
        }

        var cacheKey = (allowSubqueries ? "S:" : "N:") + predicate;

        // Fast-path: Return immediately if already verified safe in hot-cache
        if (ValidatedPredicatesCache.ContainsKey(cacheKey))
        {
            return;
        }

        if (predicate.Length > 2000)
        {
            throw new ArgumentException($"SQL predicate in '{fieldName}' exceeds maximum allowed length of 2000 characters.", fieldName);
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
}

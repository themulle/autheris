namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using Autheris.Application.Policy;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Resolved row-filter predicate for a <see cref="TableAccessDecision"/>, carrying the verified typed
/// <see cref="PolicyPredicate"/> (when present) and its AST <see cref="Expression"/>.
/// </summary>
public readonly record struct ResolvedDecisionRowFilter(
    PolicyPredicate? TypedPredicate,
    Expression Expression)
{
    public bool IsTyped => TypedPredicate is not null;
}

/// <summary>
/// Unified entry point for resolving and validating a <see cref="TableAccessDecision"/>'s row filter
/// without duplicating text normalization, AST parsing, or typed-vs-text consistency checks.
/// </summary>
public static class DecisionRowFilter
{
    internal static readonly SqlTokenSecurityOptions StrictRowFilterTokenOptions = SqlTokenSecurityOptions.Strict;

    /// <summary>
    /// Resolves the verified typed <see cref="PolicyPredicate"/> when <paramref name="decision"/> carries a
    /// <see cref="PolicyPredicateRowPolicy"/>. Fails closed on projection drift or unknown <see cref="IRowPolicy"/>
    /// implementations with <see cref="GatewaySecurityException"/> (<see cref="GovernedSqlErrorCodes.PolicyDenied"/>).
    /// </summary>
    public static bool TryResolveTypedPredicate(
        TableAccessDecision decision,
        [NotNullWhen(true)] out PolicyPredicate? predicate)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.HasRowFilter)
        {
            predicate = null;
            return false;
        }

        if (decision.RowPolicy is PolicyPredicateRowPolicy typed)
        {
            var projectedSql = typed.Project().Sql;
            if (string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                string.IsNullOrWhiteSpace(projectedSql) ||
                !string.Equals(decision.CombinedRowFilterSql, projectedSql, StringComparison.Ordinal))
            {
                throw new GatewaySecurityException(
                    "Row filter policy projection does not match the decision's row filter SQL.",
                    GovernedSqlErrorCodes.PolicyDenied);
            }

            predicate = typed.Predicate;
            return true;
        }

        if (decision.RowPolicy is not null)
        {
            throw new GatewaySecurityException(
                "Unsupported row policy implementation.",
                GovernedSqlErrorCodes.PolicyDenied);
        }

        predicate = null;
        return false;
    }

    /// <summary>
    /// Resolves a decision's row filter either directly from its verified typed <see cref="PolicyPredicateRowPolicy"/>
    /// (zero text re-parsing) or by normalizing and parsing its standalone SQL expression once.
    /// </summary>
    public static bool TryResolve(
        TableAccessDecision decision,
        IReadOnlySet<string>? allowedFunctions,
        out ResolvedDecisionRowFilter resolved,
        ISqlEngine? engine = null,
        bool translateTrinoDateFunctions = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        if (!decision.HasRowFilter)
        {
            resolved = default;
            return false;
        }

        if (TryResolveTypedPredicate(decision, out var typed))
        {
            resolved = new ResolvedDecisionRowFilter(typed, typed.Expression);
            return true;
        }

        var normalized = NormalizeRowFilterForAst(decision.CombinedRowFilterSql!);
        var (exprTree, _) = (engine ?? FastSqlEngine.Default).ParseExpression(
            normalized.AsMemory(),
            StrictRowFilterTokenOptions,
            ct);
        var expression = new SqlAstBuilder(new AstBuilderOptions
        {
            EnforceReadOnlyQueries = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = allowedFunctions,
            TranslateTrinoDateFunctions = translateTrinoDateFunctions,
            RejectTimeTravelQueries = true
        }).BuildStandaloneExpression(exprTree);

        resolved = new ResolvedDecisionRowFilter(null, expression);
        return true;
    }

    /// <summary>
    /// Resolves only the AST <see cref="Expression"/> of a decision's row filter.
    /// </summary>
    public static bool TryResolveExpression(
        TableAccessDecision decision,
        IReadOnlySet<string>? allowedFunctions,
        [NotNullWhen(true)] out Expression? expression,
        ISqlEngine? engine = null,
        bool translateTrinoDateFunctions = false,
        CancellationToken ct = default)
    {
        if (!TryResolve(decision, allowedFunctions, out var resolved, engine, translateTrinoDateFunctions, ct))
        {
            expression = null;
            return false;
        }

        expression = resolved.Expression;
        return true;
    }

    /// <summary>
    /// True only when <paramref name="decision"/> carries a verified, subquery-free <see cref="PolicyPredicateRowPolicy"/>
    /// whose projected SQL matches <see cref="TableAccessDecision.CombinedRowFilterSql"/>. Fails closed (<c>false</c>)
    /// on drift, unknown <see cref="IRowPolicy"/>, text-only filters, or predicates containing subqueries.
    /// </summary>
    public static bool IsInlineEligiblePolicyPredicate(TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        try
        {
            return TryResolveTypedPredicate(decision, out var predicate) &&
                   !PolicyPredicateSubqueryDetector.ContainsSubquery(predicate.Expression);
        }
        catch (GatewaySecurityException)
        {
            return false;
        }
    }

    internal static string NormalizeRowFilterForAst(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var sb = new StringBuilder(sql.Length + 16);
        int i = 0;
        while (i < sql.Length)
        {
            if (TrySkipLiteralOrComment(sql, i, allowBrackets: false, out int next))
            {
                sb.Append(sql, i, next - i);
                i = next;
                continue;
            }

            char c = sql[i];
            if (c == '[' && (i == 0 || sql[i - 1] is '.' or '(' or ',' or '=' or '<' or '>' or '+' or '-' or '*' or '/' or '%' or '|' or '!' || char.IsWhiteSpace(sql[i - 1])))
            {
                int end = ScanDelimited(sql, i + 1, ']', out var ident);
                sb.Append('"').Append(ident.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                i = end;
                continue;
            }

            if (c == '@' && i + 1 < sql.Length && IsIdentStart(sql[i + 1]) && (i == 0 || !IsIdentContinue(sql[i - 1])))
            {
                int j = i + 2;
                while (j < sql.Length && IsIdentContinue(sql[j]))
                {
                    j++;
                }

                sb.Append("__param_").Append(sql, i + 1, j - (i + 1));
                i = j;
                continue;
            }

            if (sql.AsSpan(i).StartsWith("__param_", StringComparison.OrdinalIgnoreCase) && (i == 0 || !IsIdentContinue(sql[i - 1])))
            {
                throw new GatewaySecurityException("Row filter SQL contains a reserved parameter identifier prefix.", GovernedSqlErrorCodes.PolicyDenied);
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    internal static bool TrySkipLiteralOrComment(string sql, int i, bool allowBrackets, out int next)
    {
        char c = sql[i];
        if (c is '\'' or '"' or '`')
        {
            next = ScanDelimited(sql, i + 1, c, out _);
            return true;
        }

        if (c == '[' && allowBrackets)
        {
            next = ScanDelimited(sql, i + 1, ']', out _);
            return true;
        }

        if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
        {
            int lf = sql.AsSpan(i + 2).IndexOf('\n');
            next = lf >= 0 ? i + 2 + lf + 1 : sql.Length;
            return true;
        }

        if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
        {
            int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
            next = end >= 0 ? end + 2 : sql.Length;
            return true;
        }

        if (c == '$' && i + 1 < sql.Length && !char.IsAsciiDigit(sql[i + 1]))
        {
            int k = i + 1;
            while (k < sql.Length && IsIdentContinue(sql[k]))
            {
                k++;
            }

            if (k < sql.Length && sql[k] == '$')
            {
                int close = sql.IndexOf(sql[i..(k + 1)], k + 1, StringComparison.Ordinal);
                next = close >= 0 ? close + (k - i + 1) : sql.Length;
                return true;
            }
        }

        next = i;
        return false;
    }

    private static int ScanDelimited(string sql, int i, char close, out string content)
    {
        var sb = close == ']' ? new StringBuilder() : null;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    sb?.Append(close);
                    i += 2;
                    continue;
                }

                content = sb?.ToString() ?? string.Empty;
                return i + 1;
            }

            sb?.Append(sql[i]);
            i++;
        }

        if (close == ']')
        {
            throw new GatewaySecurityException("Unterminated bracket identifier in SQL.", GovernedSqlErrorCodes.PolicyDenied);
        }

        content = string.Empty;
        return sql.Length;
    }

    internal static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';
    internal static bool IsIdentContinue(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

namespace TrinoSqlEngine.Ast.Buffer;

using System;
using System.Collections.Generic;
using System.Text;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// 18 / SQLE-10: escapes a gateway value that is used as (part of) a LIKE pattern, so the value never acts as a pattern
/// (%, _, [ and the escape character). Shared by the emitter (cache miss) and the template rebind (cache hit).
/// </summary>
internal static class LikePatternEscaper
{
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length + 4);
        foreach (char c in text)
        {
            if (c is '\\' or '%' or '_' or '[') sb.Append('\\');
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>True when a LIKE pattern tree (literal, parameter, || chain, parentheses) contains a policy parameter.</summary>
    public static bool ContainsPolicyParameter(Expression pattern) => pattern switch
    {
        PolicyParameterExpression => true,
        BinaryExpression { Operator: BinaryOperator.Concat } b => ContainsPolicyParameter(b.Left) || ContainsPolicyParameter(b.Right),
        ParenthesizedExpression p => ContainsPolicyParameter(p.Expression),
        _ => false
    };

    /// <summary>
    /// True when the pattern is made of policy bind markers only (a literal-free || chain). The policy predicate renderer swaps every
    /// policy parameter for such a marker before emitting, so the pattern keeps its admin-authored wildcards and is never user text.
    /// </summary>
    private static bool IsRenderedPolicyMarker(Expression pattern) => pattern switch
    {
        NamedBindMarkerExpression => true,
        BinaryExpression { Operator: BinaryOperator.Concat } b => IsRenderedPolicyMarker(b.Left) && IsRenderedPolicyMarker(b.Right),
        ParenthesizedExpression p => IsRenderedPolicyMarker(p.Expression),
        _ => false
    };

    /// <summary>
    /// Validates user LIKE escape clauses and normalizes dialect-sensitive user LIKE patterns in a single idempotent pass.
    /// Preserves SQL pattern wildcards (<c>%</c> and <c>_</c>) while neutralizing dialect-specific metacharacters.
    /// </summary>
    public static LikeExpression NormalizeUserLike(
        LikeExpression like,
        TargetSqlDialect? dialect,
        int? maxQueryLength,
        IReadOnlySet<string>? gatewayParameters = null)
    {
        char? userEscape = ValidateUserEscape(like.Escape);
        if (like.IsPatternNormalized || ContainsPolicyParameter(like.Pattern) || IsRenderedPolicyMarker(like.Pattern))
        {
            return like;
        }

        if (dialect == TargetSqlDialect.SqlServer && like.Pattern is not LiteralExpression { Type: LiteralType.String })
        {
            // A gateway-owned parameter (tree filter value, escaped with the clause's explicit escape character, never pre-escaped for
            // '[') is admitted: the generator wraps it so '[' in the value is literal. Anything else stays rejected (fail closed).
            if (like.Pattern is ParameterReference { Name: var name } && gatewayParameters?.Contains(name) == true && userEscape is not (null or '['))
            {
                return like with { IsPatternNormalized = true, IsGatewayOwnedPattern = true };
            }

            throw new SqlCompileNotSupportedException(
                SqlCompileNotSupportedReason.Construct,
                "SQL Server LIKE requires a string literal pattern");
        }

        if (like.Pattern is not LiteralExpression { Type: LiteralType.String, Value: string patternText } patternLiteral)
        {
            return like with { IsPatternNormalized = true };
        }

        string? rewritten = RewriteUserLiteralPattern(patternText, dialect, userEscape);
        if (rewritten == null)
        {
            return like with { IsPatternNormalized = true };
        }

        if (maxQueryLength is int ceiling && rewritten.Length > ceiling)
        {
            throw new SqlLimitExceededException(
                SqlLimitKind.QueryLength,
                dialect ?? TargetSqlDialect.Ansi,
                rewritten.Length,
                ceiling);
        }

        return like with
        {
            Pattern = patternLiteral with { Value = rewritten },
            IsPatternNormalized = true
        };
    }

    private static char? ValidateUserEscape(Expression? escape)
    {
        if (escape == null)
        {
            return null;
        }

        if (escape is not LiteralExpression { Type: LiteralType.String, Value: string text } ||
            text.Length != 1 ||
            text[0] is < ' ' or > '~' ||
            text[0] is '%' or '_')
        {
            throw new SqlCompileNotSupportedException(
                SqlCompileNotSupportedReason.Construct,
                "LIKE ESCAPE requires a single printable ASCII character literal excluding % and _");
        }

        return text[0];
    }

    private static string? RewriteUserLiteralPattern(string pattern, TargetSqlDialect? dialect, char? userEscape)
    {
        if (dialect == TargetSqlDialect.SqlServer)
        {
            return userEscape is char esc
                ? EscapeSqlServerBracketsWithUserEscape(pattern, esc)
                : EscapeLiteralDefault(pattern, escapeBracket: true);
        }

        if (dialect is TargetSqlDialect.PostgreSql or TargetSqlDialect.Databricks && userEscape == null)
        {
            return EscapeLiteralDefault(pattern, escapeBracket: false);
        }

        return null;
    }

    private static string EscapeLiteralDefault(string pattern, bool escapeBracket)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        foreach (char c in pattern)
        {
            if (c == '\\' || (escapeBracket && c == '['))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string EscapeSqlServerBracketsWithUserEscape(string pattern, char esc)
    {
        var sb = new StringBuilder(pattern.Length + 4);
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == esc)
            {
                sb.Append(c);
                if (i + 1 < pattern.Length)
                {
                    sb.Append(pattern[++i]);
                }

                continue;
            }

            if (c == '[')
            {
                sb.Append(esc);
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

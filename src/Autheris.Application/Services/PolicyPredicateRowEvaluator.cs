namespace Autheris.Application.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// WP-ARC-B-04: evaluates the typed <see cref="PolicyPredicate"/> of a row policy against in-memory rows with SQL three-valued logic
/// (a row passes only when the predicate is TRUE). No SQL text is read or parsed. The evaluator is deliberately small and fail closed:
/// a node it does not implement, a comparison between values of different kinds (no implicit coercion) or a value that has no
/// comparable form makes the whole predicate unenforceable, and every row is denied. A row that lacks a column the predicate
/// reads is denied individually.
/// </summary>
internal static class PolicyPredicateRowEvaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Returns the rows for which <paramref name="predicate"/> is TRUE; an unenforceable predicate yields no rows.</summary>
    public static List<IReadOnlyDictionary<string, object?>> Filter(
        List<IReadOnlyDictionary<string, object?>> rows,
        PolicyPredicate predicate,
        TableMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(metadata);
        if (rows.Count == 0)
        {
            return rows;
        }

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in metadata.Columns)
        {
            columns[column.ColumnName] = column.ColumnName;
        }

        var context = new Context(predicate, columns);
        var kept = new List<IReadOnlyDictionary<string, object?>>();
        try
        {
            Validate(predicate.Expression, context);
            foreach (var row in rows)
            {
                context.Row = row;
                try
                {
                    if (Evaluate(predicate.Expression, context) is true)
                    {
                        kept.Add(row);
                    }
                }
                catch (MissingColumnException)
                {
                    // Fail closed for this row: the predicate cannot be evaluated, so the row must not count as NULL.
                }
            }
        }
        catch (UnsupportedPredicateException)
        {
            return [];
        }

        return kept;
    }

    private sealed class UnsupportedPredicateException : Exception
    {
    }

    private sealed class MissingColumnException : Exception
    {
    }

    private sealed class Context(PolicyPredicate predicate, Dictionary<string, string> columns)
    {
        public PolicyPredicate Predicate { get; } = predicate;

        public Dictionary<string, string> Columns { get; } = columns;

        public IReadOnlyDictionary<string, object?> Row { get; set; } = new Dictionary<string, object?>();

        public Dictionary<(string Pattern, char? Escape, bool Wildcards), Regex> Patterns { get; } = [];
    }

    // The whole tree is checked once before any row is evaluated, so short-circuiting can never hide an unsupported node.
    private static void Validate(Expression expression, Context context)
    {
        switch (expression)
        {
            case ColumnReference column:
                if (!context.Columns.ContainsKey(column.Name.SimpleName))
                {
                    throw new UnsupportedPredicateException();
                }

                break;
            case LiteralExpression:
                break;
            case PolicyParameterExpression parameter:
                if (!context.Predicate.Parameters.ContainsKey(parameter.Name))
                {
                    throw new UnsupportedPredicateException();
                }

                break;
            case ParenthesizedExpression paren:
                Validate(paren.Expression, context);
                break;
            case BinaryExpression binary when IsSupported(binary.Operator):
                Validate(binary.Left, context);
                Validate(binary.Right, context);
                break;
            case UnaryExpression unary when unary.Operator != UnaryOperator.Negate:
                Validate(unary.Operand, context);
                break;
            case LikeExpression like:
                Validate(like.Operand, context);
                Validate(like.Pattern, context);
                if (like.Escape is not null)
                {
                    if (like.Escape is not LiteralExpression { Value: string { Length: 1 } })
                    {
                        throw new UnsupportedPredicateException();
                    }
                }

                break;
            case InListExpression list:
                Validate(list.Operand, context);
                foreach (var item in list.Items)
                {
                    Validate(item, context);
                }

                break;
            case BetweenExpression between:
                Validate(between.Operand, context);
                Validate(between.Lower, context);
                Validate(between.Upper, context);
                break;
            case IsDistinctFromExpression distinct:
                Validate(distinct.Left, context);
                Validate(distinct.Right, context);
                break;
            default:
                throw new UnsupportedPredicateException();
        }
    }

    private static bool IsSupported(BinaryOperator op) =>
        op is BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual;

    // Result: true, false or null (SQL UNKNOWN) for predicates; the normalized value for operands.
    private static object? Evaluate(Expression expression, Context context)
    {
        switch (expression)
        {
            case ColumnReference column:
                return ReadColumn(column, context);
            case LiteralExpression literal:
                return literal.Type == LiteralType.Null ? null : Normalize(literal.Value);
            case PolicyParameterExpression parameter:
                return Normalize(context.Predicate.Parameters[parameter.Name].Value);
            case ParenthesizedExpression paren:
                return Evaluate(paren.Expression, context);
            case BinaryExpression binary:
                return EvaluateBinary(binary, context);
            case UnaryExpression unary:
                return EvaluateUnary(unary, context);
            case LikeExpression like:
                return EvaluateLike(like, context);
            case InListExpression list:
                return EvaluateIn(list, context);
            case BetweenExpression between:
                return EvaluateBetween(between, context);
            case IsDistinctFromExpression distinct:
            {
                var left = Evaluate(distinct.Left, context);
                var right = Evaluate(distinct.Right, context);
                bool isDistinct = left is null || right is null ? !(left is null && right is null) : Compare(left, right) != 0;
                return distinct.IsNotDistinctFrom ? !isDistinct : isDistinct;
            }

            default:
                throw new UnsupportedPredicateException();
        }
    }

    private static object? ReadColumn(ColumnReference column, Context context)
    {
        if (!context.Columns.TryGetValue(column.Name.SimpleName, out var canonical))
        {
            throw new UnsupportedPredicateException();
        }

        if (!context.Row.TryGetValue(canonical, out var value))
        {
            throw new MissingColumnException();
        }

        return Normalize(value);
    }

    private static object? EvaluateBinary(BinaryExpression binary, Context context)
    {
        if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            var left = AsBoolean(Evaluate(binary.Left, context));
            var right = AsBoolean(Evaluate(binary.Right, context));
            return binary.Operator == BinaryOperator.And
                ? (left is false || right is false ? false : left is null || right is null ? null : true)
                : (left is true || right is true ? true : left is null || right is null ? null : false);
        }

        var l = Evaluate(binary.Left, context);
        var r = Evaluate(binary.Right, context);
        if (l is null || r is null)
        {
            return null;
        }

        int cmp = Compare(l, r);
        return binary.Operator switch
        {
            BinaryOperator.Equal => cmp == 0,
            BinaryOperator.NotEqual => cmp != 0,
            BinaryOperator.LessThan => cmp < 0,
            BinaryOperator.LessThanOrEqual => cmp <= 0,
            BinaryOperator.GreaterThan => cmp > 0,
            BinaryOperator.GreaterThanOrEqual => cmp >= 0,
            _ => throw new UnsupportedPredicateException()
        };
    }

    private static object? EvaluateUnary(UnaryExpression unary, Context context)
    {
        var operand = Evaluate(unary.Operand, context);
        return unary.Operator switch
        {
            UnaryOperator.IsNull => operand is null,
            UnaryOperator.IsNotNull => operand is not null,
            UnaryOperator.Not => AsBoolean(operand) is { } b ? !b : null,
            _ => throw new UnsupportedPredicateException()
        };
    }

    private static object? EvaluateIn(InListExpression list, Context context)
    {
        var operand = Evaluate(list.Operand, context);
        if (operand is null)
        {
            return null;
        }

        bool sawNull = false;
        foreach (var item in list.Items)
        {
            var value = Evaluate(item, context);
            if (value is null)
            {
                sawNull = true;
            }
            else if (Compare(operand, value) == 0)
            {
                return !list.IsNotIn;
            }
        }

        return sawNull ? null : list.IsNotIn;
    }

    private static object? EvaluateBetween(BetweenExpression between, Context context)
    {
        var operand = Evaluate(between.Operand, context);
        var lower = Evaluate(between.Lower, context);
        var upper = Evaluate(between.Upper, context);
        bool? geLower = operand is null || lower is null ? null : Compare(operand, lower) >= 0;
        bool? leUpper = operand is null || upper is null ? null : Compare(operand, upper) <= 0;
        bool? result = geLower is false || leUpper is false ? false : geLower is null || leUpper is null ? null : true;
        return result is { } b && between.IsNotBetween ? !b : result;
    }

    private static object? EvaluateLike(LikeExpression like, Context context)
    {
        var operand = Evaluate(like.Operand, context);
        var pattern = Evaluate(like.Pattern, context);
        if (operand is null || pattern is null)
        {
            return null;
        }

        if (operand is not string text || pattern is not string patternText)
        {
            throw new UnsupportedPredicateException();
        }

        // SEC-ADG-18: only an admin-authored pattern (a literal, or a parameter flagged IsLikePattern) keeps its wildcards.
        bool wildcards = like.Pattern switch
        {
            PolicyParameterExpression p => p.IsLikePattern,
            LiteralExpression => true,
            _ => false
        };
        char? escape = like.Escape is LiteralExpression { Value: string { Length: 1 } e } ? e[0] : null;

        var key = (patternText, escape, wildcards);
        if (!context.Patterns.TryGetValue(key, out var regex))
        {
            regex = new Regex(ToRegex(patternText, escape, wildcards), RegexOptions.CultureInvariant | RegexOptions.Singleline, RegexTimeout);
            context.Patterns[key] = regex;
        }

        bool matched = regex.IsMatch(text);
        return like.IsNotLike ? !matched : matched;
    }

    private static string ToRegex(string pattern, char? escape, bool wildcards)
    {
        var sb = new StringBuilder("\\A");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (wildcards && escape is { } esc && c == esc && i + 1 < pattern.Length)
            {
                sb.Append(Regex.Escape(new string(pattern[++i], 1)));
            }
            else if (wildcards && c == '%')
            {
                sb.Append(".*");
            }
            else if (wildcards && c == '_')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(Regex.Escape(new string(c, 1)));
            }
        }

        return sb.Append("\\z").ToString();
    }

    private static bool? AsBoolean(object? value) => value switch
    {
        null => null,
        bool b => b,
        _ => throw new UnsupportedPredicateException()
    };

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal => value,
        double d when double.IsFinite(d) => ToDecimal(d),
        float f when float.IsFinite(f) => ToDecimal(f),
        double or float => throw new UnsupportedPredicateException(),
        _ => value
    };

    private static decimal ToDecimal(double value)
    {
        try
        {
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            throw new UnsupportedPredicateException();
        }
    }

    private static bool IsNumber(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal;

    // Type-strict: values of different kinds never compare (no '007' = 7, no 7 = '7').
    private static int Compare(object left, object right)
    {
        if (IsNumber(left) && IsNumber(right))
        {
            try
            {
                return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
            }
            catch (OverflowException)
            {
                throw new UnsupportedPredicateException();
            }
        }

        return (left, right) switch
        {
            (string a, string b) => string.CompareOrdinal(a, b),
            (bool a, bool b) => a.CompareTo(b),
            (DateTime a, DateTime b) => a.CompareTo(b),
            (Guid a, Guid b) => a.CompareTo(b),
            _ => throw new UnsupportedPredicateException()
        };
    }
}

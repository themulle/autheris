namespace Autheris.Application.Streaming.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;

/// <summary>
/// High-throughput compiled AST evaluator for SQL Row-Level Security (RLS) predicates on in-memory streaming CDC events.
/// Pre-compiles Antlr SQL AST into evaluation plans supporting comparisons, IN, LIKE, IS NULL, BETWEEN, and boolean logic.
/// SEC M-21: Plans use SQL three-valued logic (true / false / unknown = null); a row passes only if the predicate is TRUE
/// (WHERE semantics). Any expression that is not a plain column reference or a literal (functions, parameters, subqueries,
/// arithmetic, CASE, ...) is rejected at compile time, which makes the filter fail closed.
/// </summary>
public static partial class StreamingRowFilterAstEvaluator
{
    private static readonly FastSqlEngine Engine = new() { MaxQueryLength = 4096 };
    private static readonly ConcurrentDictionary<string, Func<IReadOnlyDictionary<string, object?>, bool?>?> PlanCache = new(StringComparer.Ordinal);
    private const int MaxCacheSize = 1000;

    // Column reference path: segments of plain, "double", `back` or [bracket] quoted identifiers separated by dots.
    [GeneratedRegex(@"^(?:[A-Za-z_][A-Za-z0-9_$]*|""[^"".]+""|`[^`.]+`|\[[^\].\[]+\])(?:\.(?:[A-Za-z_][A-Za-z0-9_$]*|""[^"".]+""|`[^`.]+`|\[[^\].\[]+\]))*$")]
    private static partial Regex ColumnPathRegex();

    private enum OperandKind
    {
        Column,
        Constant
    }

    private readonly record struct Operand(OperandKind Kind, string? Column, object? Value);

    /// <summary>
    /// Evaluates whether the given CDC payload matches the SQL row filter.
    /// Returns true only when the filter evaluates to SQL TRUE; FALSE, UNKNOWN (NULL), unsupported filters and
    /// evaluation errors all return false (fail-closed).
    /// </summary>
    public static bool Matches(IReadOnlyDictionary<string, object?> payload, string? filterSql)
    {
        if (string.IsNullOrWhiteSpace(filterSql))
        {
            return true;
        }

        if (payload == null || payload.Count == 0)
        {
            return false;
        }

        var plan = GetOrCompilePlan(filterSql);
        if (plan == null)
        {
            // Unparseable or unsupported filter -> fail-closed for zero-trust security
            return false;
        }

        try
        {
            return plan(payload) == true;
        }
        catch
        {
            // Any evaluation error -> fail-closed
            return false;
        }
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? GetOrCompilePlan(string filterSql)
    {
        if (PlanCache.TryGetValue(filterSql, out var cached))
        {
            return cached;
        }

        if (PlanCache.Count >= MaxCacheSize)
        {
            PlanCache.Clear();
        }

        Func<IReadOnlyDictionary<string, object?>, bool?>? plan = null;
        try
        {
            // SEC M-21: Named parameters (@p, ?) cannot be bound in the streaming path -> reject instead of
            // re-interpreting them as column names.
            if (!ContainsParameter(filterSql))
            {
                var (tree, _) = Engine.ParseExpression(filterSql.AsMemory());
                if (tree?.expression() != null)
                {
                    plan = CompileBoolean(tree.expression());
                }
            }
        }
        catch
        {
            plan = null;
        }

        PlanCache[filterSql] = plan;
        return plan;
    }

    private static bool ContainsParameter(string sql)
    {
        bool inSingleQuote = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'')
            {
                if (inSingleQuote && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inSingleQuote = !inSingleQuote;
            }
            else if (!inSingleQuote && (c == '@' || c == '?'))
            {
                return true;
            }
        }
        return false;
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileBoolean(RuleContext? ctx)
    {
        if (ctx == null) return null;

        return ctx switch
        {
            SqlBaseParser.ExpressionContext exprCtx =>
                CompileBoolean(exprCtx.booleanExpression()),

            SqlBaseParser.AndContext andCtx =>
                CompileAnd(andCtx),

            SqlBaseParser.OrContext orCtx =>
                CompileOr(orCtx),

            SqlBaseParser.LogicalNotContext notCtx =>
                CompileNot(notCtx),

            SqlBaseParser.ParenthesizedExpressionContext parenCtx =>
                CompileBoolean(parenCtx.expression()),

            SqlBaseParser.PredicatedContext predCtx =>
                CompilePredicated(predCtx),

            _ => null
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileAnd(SqlBaseParser.AndContext andCtx)
    {
        var left = CompileBoolean(andCtx.booleanExpression(0));
        var right = CompileBoolean(andCtx.booleanExpression(1));
        if (left == null || right == null) return null;
        return payload =>
        {
            var l = left(payload);
            if (l == false) return false;
            var r = right(payload);
            if (r == false) return false;
            return l == true && r == true ? true : null;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileOr(SqlBaseParser.OrContext orCtx)
    {
        var left = CompileBoolean(orCtx.booleanExpression(0));
        var right = CompileBoolean(orCtx.booleanExpression(1));
        if (left == null || right == null) return null;
        return payload =>
        {
            var l = left(payload);
            if (l == true) return true;
            var r = right(payload);
            if (r == true) return true;
            return l == false && r == false ? false : null;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileNot(SqlBaseParser.LogicalNotContext notCtx)
    {
        var inner = CompileBoolean(notCtx.booleanExpression());
        if (inner == null) return null;
        return payload =>
        {
            var v = inner(payload);
            return v.HasValue ? !v.Value : null;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompilePredicated(SqlBaseParser.PredicatedContext ctx)
    {
        var predicate = ctx.predicate();

        if (predicate == null)
        {
            if (ctx.valueExpression() is SqlBaseParser.ValueExpressionDefaultContext def &&
                def.primaryExpression() is SqlBaseParser.ParenthesizedExpressionContext paren)
            {
                return CompileBoolean(paren.expression());
            }

            var bare = InspectValueExpression(ctx.valueExpression());
            if (bare == null) return null;

            var operand = bare.Value;
            if (operand.Kind == OperandKind.Constant)
            {
                if (operand.Value is bool b) return _ => b;
                if (operand.Value == null) return _ => null;
                return null;
            }

            var colName = operand.Column!;
            return payload =>
            {
                if (!TryGetPayloadValue(payload, colName, out var v) || v == null || v is DBNull) return null;
                if (v is bool vb) return vb;
                if (v is string s && bool.TryParse(s, out var pb)) return pb;
                return null;
            };
        }

        var left = InspectValueExpression(ctx.valueExpression());
        if (left == null) return null;

        return predicate switch
        {
            SqlBaseParser.ComparisonContext comp =>
                CompileComparison(left.Value, comp),

            SqlBaseParser.InListContext inList =>
                CompileInList(left.Value, inList),

            SqlBaseParser.NullPredicateContext nullPred =>
                CompileNullPredicate(left.Value, nullPred),

            SqlBaseParser.LikeContext likeCtx =>
                CompileLike(left.Value, likeCtx),

            SqlBaseParser.BetweenContext betweenCtx =>
                CompileBetween(left.Value, betweenCtx),

            _ => null
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileComparison(Operand left, SqlBaseParser.ComparisonContext comp)
    {
        var op = comp.comparisonOperator()?.GetText()?.Trim();
        if (string.IsNullOrEmpty(op)) return null;
        if (op is not ("=" or "==" or "!=" or "<>" or "<" or "<=" or ">" or ">=")) return null;

        var right = InspectValueExpression(comp.right);
        if (right == null) return null;
        var rightOperand = right.Value;

        return payload => EvaluateComparison(Resolve(payload, left), op, Resolve(payload, rightOperand));
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileInList(Operand left, SqlBaseParser.InListContext inList)
    {
        if (left.Kind != OperandKind.Column) return null;
        var leftCol = left.Column!;

        bool isNot = inList.NOT() != null;
        var expressions = inList.expression();
        if (expressions == null || expressions.Length == 0) return null;

        var strSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var numSet = new HashSet<decimal>();
        bool listContainsNull = false;

        foreach (var expr in expressions)
        {
            var element = InspectExpressionOperand(expr);
            if (element == null || element.Value.Kind != OperandKind.Constant)
            {
                // Only literal lists are supported (fail-closed)
                return null;
            }

            var constant = element.Value.Value;
            if (constant == null)
            {
                listContainsNull = true;
                continue;
            }

            strSet.Add(constant.ToString() ?? string.Empty);
            if (TryConvertToDecimal(constant, out decimal d))
            {
                numSet.Add(d);
            }
        }

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null || val is DBNull)
            {
                return null;
            }

            bool match = (TryConvertToDecimal(val, out decimal d) && numSet.Contains(d))
                         || strSet.Contains(val.ToString() ?? string.Empty);

            if (match)
            {
                return !isNot;
            }

            // x IN (..., NULL) without a match is UNKNOWN, as is x NOT IN (..., NULL)
            if (listContainsNull)
            {
                return null;
            }

            return isNot;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileLike(Operand left, SqlBaseParser.LikeContext likeCtx)
    {
        if (left.Kind != OperandKind.Column) return null;
        if (likeCtx.escape != null) return null;
        var leftCol = left.Column!;

        bool isNot = likeCtx.NOT() != null;
        var pattern = InspectValueExpression(likeCtx.pattern);
        if (pattern == null || pattern.Value.Kind != OperandKind.Constant || pattern.Value.Value is not string patternVal)
        {
            return null;
        }

        var sb = new StringBuilder("^", patternVal.Length * 2 + 2);
        for (int i = 0; i < patternVal.Length; i++)
        {
            char c = patternVal[i];
            if (c == '%')
            {
                sb.Append(".*");
            }
            else if (c == '_')
            {
                sb.Append('.');
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');

        var compiledRegex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromMilliseconds(200));

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null || val is DBNull)
            {
                return null;
            }

            try
            {
                bool isMatch = compiledRegex.IsMatch(val.ToString() ?? string.Empty);
                return isNot ? !isMatch : isMatch;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileNullPredicate(Operand left, SqlBaseParser.NullPredicateContext nullPred)
    {
        if (left.Kind != OperandKind.Column) return null;
        var leftCol = left.Column!;
        bool isNot = nullPred.NOT() != null;

        return payload =>
        {
            bool hasVal = TryGetPayloadValue(payload, leftCol, out var val) && val != null && val is not DBNull;
            return isNot ? hasVal : !hasVal;
        };
    }

    private static Func<IReadOnlyDictionary<string, object?>, bool?>? CompileBetween(Operand left, SqlBaseParser.BetweenContext betweenCtx)
    {
        if (left.Kind != OperandKind.Column) return null;
        var leftCol = left.Column!;
        bool isNot = betweenCtx.NOT() != null;

        var low = InspectValueExpression(betweenCtx.lower);
        var up = InspectValueExpression(betweenCtx.upper);
        if (low == null || up == null ||
            low.Value.Kind != OperandKind.Constant || up.Value.Kind != OperandKind.Constant ||
            !TryConvertToDecimal(low.Value.Value, out decimal lowNum) ||
            !TryConvertToDecimal(up.Value.Value, out decimal upNum))
        {
            return null;
        }

        return payload =>
        {
            if (!TryGetPayloadValue(payload, leftCol, out var val) || val == null || val is DBNull)
            {
                return null;
            }

            if (TryConvertToDecimal(val, out decimal actual))
            {
                bool inRange = actual >= lowNum && actual <= upNum;
                return isNot ? !inRange : inRange;
            }

            return null;
        };
    }

    private static object? Resolve(IReadOnlyDictionary<string, object?> payload, Operand operand)
    {
        if (operand.Kind == OperandKind.Constant)
        {
            return operand.Value;
        }

        return TryGetPayloadValue(payload, operand.Column!, out var val) && val is not DBNull ? val : null;
    }

    private static Operand? InspectExpressionOperand(SqlBaseParser.ExpressionContext? expr)
    {
        if (expr?.booleanExpression() is SqlBaseParser.PredicatedContext pred && pred.predicate() == null)
        {
            return InspectValueExpression(pred.valueExpression());
        }

        return null;
    }

    /// <summary>
    /// Classifies a value expression as column reference or literal. Returns null for every other construct
    /// (function calls, parameters, subqueries, arithmetic, CASE, CAST, typed literals ...), so the caller rejects the filter.
    /// </summary>
    private static Operand? InspectValueExpression(SqlBaseParser.ValueExpressionContext? ctx)
    {
        if (ctx == null) return null;

        string text = ctx.GetText();

        if (ctx is SqlBaseParser.ArithmeticUnaryContext)
        {
            // Only signed numeric literals such as -5
            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal signed)
                ? new Operand(OperandKind.Constant, null, signed)
                : null;
        }

        if (ctx is not SqlBaseParser.ValueExpressionDefaultContext def)
        {
            return null;
        }

        var primary = def.primaryExpression();
        switch (primary)
        {
            case SqlBaseParser.LiteralsContext:
                if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
                {
                    return new Operand(OperandKind.Constant, null, text[1..^1].Replace("''", "'", StringComparison.Ordinal));
                }

                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal num))
                {
                    return new Operand(OperandKind.Constant, null, num);
                }

                if (bool.TryParse(text, out bool b))
                {
                    return new Operand(OperandKind.Constant, null, b);
                }

                if (string.Equals(text, "NULL", StringComparison.OrdinalIgnoreCase))
                {
                    return new Operand(OperandKind.Constant, null, null);
                }

                // Typed literals (DATE '...', INTERVAL, X'..') are not supported
                return null;

            case SqlBaseParser.ColumnReferenceContext:
            case SqlBaseParser.DereferenceContext:
            case SqlBaseParser.ArrayConstructorContext:
                // ArrayConstructor covers SQL Server style [column] identifiers produced by the row filter builder.
                if (!ColumnPathRegex().IsMatch(text))
                {
                    return null;
                }

                return new Operand(OperandKind.Column, NormalizeColumnPath(text), null);

            default:
                return null;
        }
    }

    private static string NormalizeColumnPath(string path)
    {
        var segments = path.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = SqlIdentifierHelper.NormalizeIdentifier(segments[i]);
        }

        return string.Join('.', segments);
    }

    private static bool TryGetPayloadValue(IReadOnlyDictionary<string, object?> payload, string colName, out object? val)
    {
        if (payload.TryGetValue(colName, out val))
        {
            return true;
        }

        int dotIdx = colName.LastIndexOf('.');
        if (dotIdx >= 0 && dotIdx + 1 < colName.Length)
        {
            string simple = colName[(dotIdx + 1)..];
            if (payload.TryGetValue(simple, out val))
            {
                return true;
            }
        }

        val = null;
        return false;
    }

    private static bool? EvaluateComparison(object? left, string opText, object? right)
    {
        // SEC M-21: SQL NULL semantics - any comparison with NULL (or a missing column) is UNKNOWN.
        if (left == null || right == null)
        {
            return null;
        }

        // Numeric comparison
        if (TryConvertToDecimal(left, out decimal leftNum) && TryConvertToDecimal(right, out decimal rightNum))
        {
            int cmp = leftNum.CompareTo(rightNum);
            return opText switch
            {
                "=" or "==" => cmp == 0,
                "!=" or "<>" => cmp != 0,
                "<" => cmp < 0,
                "<=" => cmp <= 0,
                ">" => cmp > 0,
                ">=" => cmp >= 0,
                _ => null
            };
        }

        // String comparison
        string sLeft = left.ToString() ?? string.Empty;
        string sRight = right.ToString() ?? string.Empty;
        int strCmp = string.Compare(sLeft, sRight, StringComparison.OrdinalIgnoreCase);

        return opText switch
        {
            "=" or "==" => strCmp == 0,
            "!=" or "<>" => strCmp != 0,
            "<" => strCmp < 0,
            "<=" => strCmp <= 0,
            ">" => strCmp > 0,
            ">=" => strCmp >= 0,
            _ => null
        };
    }

    private static bool TryConvertToDecimal(object? val, out decimal result)
    {
        if (val is null || val is bool)
        {
            result = 0;
            return false;
        }

        if (val is decimal d) { result = d; return true; }
        if (val is int i) { result = i; return true; }
        if (val is long l) { result = l; return true; }
        if (val is short s) { result = s; return true; }
        if (val is byte b) { result = b; return true; }
        if (val is float f) { result = (decimal)f; return true; }
        if (val is double db) { result = (decimal)db; return true; }

        return decimal.TryParse(val.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }
}

namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Security;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Fail-closed allowlist for user expressions on statements that involve a row policy or a tenant predicate: only operators and functions
/// that cannot raise a data-dependent error (or run for a data-dependent time) are accepted, every other expression kind is refused.
/// Runs on the user tree only (annotated with column types, without any injected predicate); details: private advisory.
/// </summary>
public static class RowPolicyErrorGuard
{
    public const string Message = "The statement uses an expression that is not permitted on a table with a row policy.";

    /// <summary>
    /// Refuses the statement or returns the family each client bind must conform to (empty when none).
    /// When <paramref name="evaluationFenced"/> is true, every policy-protected table scan in the read statement sits behind a
    /// proven optimizer evaluation fence, so column expressions evaluate only over visible rows while cross-family implicit coercions,
    /// <c>ClientBindFamily</c> checks, and constant-folding hazards remain strictly enforced.
    /// </summary>
    public static IReadOnlyDictionary<string, BindFamily> Enforce(
        SqlNode userTree,
        ITableCatalog catalog,
        bool evaluationFenced = false)
    {
        ArgumentNullException.ThrowIfNull(userTree);
        ArgumentNullException.ThrowIfNull(catalog);

        AstReflection.Walk(userTree, node =>
        {
            if (node is Expression expression && !IsAllowed(expression, evaluationFenced))
            {
                throw new RowPolicyExpressionRejectedException();
            }

            return true;
        });

        var coercion = new RowPolicyCoercionGuard(catalog, evaluationFenced);
        coercion.Visit(userTree);
        return coercion.Requirements;
    }

    /// <summary>
    /// Verifies structurally that <paramref name="secured"/> is a read <see cref="SelectStatement"/> and that every
    /// <see cref="QuerySpecification"/> carrying an injected <see cref="SecurityPredicateExpression"/> in its <c>WHERE</c>
    /// or <c>SELECT</c> list is the immediate body of a fenced <see cref="SubqueryTableSource"/>.
    /// </summary>
    internal static bool AreAllPolicyTablesFenced(SqlStatement secured)
    {
        if (secured is not SelectStatement)
        {
            return false;
        }

        return CheckNodeFenced(secured, enclosingSubqueryFenced: false);
    }

    private static bool CheckNodeFenced(SqlNode node, bool enclosingSubqueryFenced)
    {
        switch (node)
        {
            case SecurityPredicateExpression:
                // Gateway-injected policy predicate (including PolicySubquery); caller expressions cannot sit inside it.
                return true;
            case SubqueryTableSource sub:
                return CheckSelectInsideSubquery(sub.Subquery, sub.EvaluationFence);
            case QuerySpecification spec:
                if (HasDirectSecurityPredicate(spec) && !enclosingSubqueryFenced)
                {
                    return false;
                }

                foreach (var child in node.EnumerateChildren())
                {
                    if (!CheckNodeFenced(child, enclosingSubqueryFenced: false))
                    {
                        return false;
                    }
                }
                return true;
            default:
                foreach (var child in node.EnumerateChildren())
                {
                    if (!CheckNodeFenced(child, enclosingSubqueryFenced: false))
                    {
                        return false;
                    }
                }
                return true;
        }
    }

    private static bool CheckSelectInsideSubquery(SelectStatement select, bool isSubqueryFenced)
    {
        if (select.With is not null)
        {
            if (!CheckNodeFenced(select.With, enclosingSubqueryFenced: false)) return false;
        }

        if (select.Body is QuerySpecification spec)
        {
            if (!CheckNodeFenced(spec, enclosingSubqueryFenced: isSubqueryFenced)) return false;
        }
        else if (!CheckNodeFenced(select.Body, enclosingSubqueryFenced: false))
        {
            return false;
        }

        if (select.OrderBy is not null && !CheckNodeFenced(select.OrderBy, enclosingSubqueryFenced: false)) return false;
        if (select.Pagination is not null && !CheckNodeFenced(select.Pagination, enclosingSubqueryFenced: false)) return false;
        return true;
    }

    private static bool HasDirectSecurityPredicate(QuerySpecification spec)
    {
        if (spec.Where is not null && ContainsSecurityPredicateOutsideSubqueries(spec.Where))
        {
            return true;
        }

        foreach (var item in spec.Projections)
        {
            if (ContainsSecurityPredicateOutsideSubqueries(item))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSecurityPredicateOutsideSubqueries(SqlNode node)
    {
        if (node is SecurityPredicateExpression) return true;
        if (node is SelectStatement) return false;
        foreach (var child in node.EnumerateChildren())
        {
            if (ContainsSecurityPredicateOutsideSubqueries(child)) return true;
        }
        return false;
    }

    private static bool IsAllowed(Expression expression, bool evaluationFenced)
    {
        if (evaluationFenced)
        {
            if (expression is LiteralExpression lit)
            {
                return IsConsistentLiteral(lit);
            }

            if (HasColumn(expression))
            {
                return true;
            }

            // Column-free expressions must still be safe against plan-time constant evaluation hazards
            // (overflow, division by zero, negative sqrt, invalid constant casts).
            return IsAllowedConstantUnderFence(expression);
        }

        return IsAllowedStrict(expression);
    }

    private static bool IsAllowedConstantUnderFence(Expression expression) => expression switch
    {
        ScalarSubqueryExpression or ArrayConstructorExpression => true,
        BinaryExpression b when b.Operator is BinaryOperator.Add or BinaryOperator.Subtract && IsTemporalIntervalBinary(b) => true,
        FunctionCallExpression call when call.Name.Parts.Count == 1
            && call.Name.Parts[0].Value.ToLowerInvariant() is "row_number" or "rank" or "dense_rank" or "ntile" => true,
        _ => IsAllowedStrict(expression)
    };

    private static bool IsTemporalIntervalBinary(BinaryExpression binary) =>
        (binary.Right is IntervalLiteralExpression && IsConstantTemporal(binary.Left)) ||
        (binary.Left is IntervalLiteralExpression && binary.Operator == BinaryOperator.Add && IsConstantTemporal(binary.Right));

    private static bool IsConstantTemporal(Expression expr) => Unwrap(expr) switch
    {
        CurrentDateTimeExpression or TypedLiteralExpression or DateFunctionExpression => true,
        BinaryExpression b when b.Operator is BinaryOperator.Add or BinaryOperator.Subtract => IsTemporalIntervalBinary(b),
        _ => false
    };

    private static bool IsAllowedStrict(Expression expression) => expression switch
    {
        ColumnReference or ParameterReference or NamedBindMarkerExpression or PolicyParameterExpression or PageOffsetParameterExpression
            or ParenthesizedExpression or CurrentDateTimeExpression or TypedLiteralExpression or IntervalLiteralExpression
            or GroupingOperationExpression or PositionExpression or ExtractExpression or RowValueExpression or ArrayConstructorExpression
            or IsDistinctFromExpression or BetweenExpression or CaseExpression or InListExpression or InSubqueryExpression
            or ExistsExpression or QuantifiedComparisonExpression => true,
        ScalarSubqueryExpression scalar => ReturnsExactlyOneRow(scalar.Subquery),
        LiteralExpression literal => IsConsistentLiteral(literal),
        BinaryExpression binary => IsAllowedBinary(binary),
        UnaryExpression unary => unary.Operator != UnaryOperator.Negate || TryEvaluate(unary, out _),
        LikeExpression like => IsConstant(like.Pattern) && (like.Escape is null || IsConstant(like.Escape)),
        FunctionCallExpression call => IsAllowedFunction(call),
        SubstringExpression sub => IsNonNegativeIntegerLiteral(sub.Start, allowNegative: true) && (sub.Length is null || IsNonNegativeIntegerLiteral(sub.Length, allowNegative: false)),
        TrimExpression trim => trim.Characters is null || IsConstant(trim.Characters),
        CastExpression => true,   // judged by the coercion stage (target family against operand family)
        DateFunctionExpression date => date.Kind == DateFunctionKind.Trunc || !HasColumn(date.Source),
        // Everything else (subscript, unknown node kinds) can raise on row data: refused.
        _ => false
    };

    internal static bool IsSafeAggregateForm(FunctionCallExpression call)
    {
        if (call.Name.Parts.Count != 1 || call.Window is not null || call.Distinct || call.Filter is not null || call.Arguments.Count != 1)
        {
            return false;
        }

        string name = call.Name.Parts[0].Value;
        if (!name.Equals("sum", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("avg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Unwrap(call.Arguments[0]) switch
        {
            CastExpression { IsTryCast: true } tc => IsWideNumericTarget(tc.TargetType) && IsSafeAggregateOperand(tc.Operand),
            CastExpression { IsTryCast: false } wc => IsWideningExactOrFloatTarget(wc.TargetType) && Unwrap(wc.Operand) is ColumnReference,
            _ => false
        };
    }

    internal static bool IsWideningAggregateCast(CastExpression cast) =>
        !cast.IsTryCast && IsWideningExactOrFloatTarget(cast.TargetType) && Unwrap(cast.Operand) is ColumnReference;

    private static bool IsSafeAggregateOperand(Expression operand) => Unwrap(operand) switch
    {
        ColumnReference => true,
        LiteralExpression l => l.Type is LiteralType.Integer or LiteralType.Decimal or LiteralType.Null,
        FunctionCallExpression inner when inner.Name.Parts.Count == 1 && inner.Window is null && inner.Filter is null && !inner.Distinct
            && inner.Name.Parts[0].Value.ToLowerInvariant() is "nullif" or "coalesce"
            && inner.Arguments.Count >= 1
            && inner.Arguments.All(a => IsSafeAggregateOperand(a) || IsConstant(Unwrap(a))) => true,
        _ => false
    };

    private static bool IsWideNumericTarget(string targetType)
    {
        var trimmed = targetType.Trim().ToLowerInvariant();
        if (trimmed is "double" or "double precision" or "float")
        {
            return true;
        }

        return IsWideDecimalType(trimmed);
    }

    private static bool IsWideningExactOrFloatTarget(string targetType)
    {
        var trimmed = targetType.Trim().ToLowerInvariant();
        if (trimmed is "double" or "double precision")
        {
            return true;
        }

        return IsWideDecimalType(trimmed);
    }

    private static bool IsWideDecimalType(string trimmedLower)
    {
        int paren = trimmedLower.IndexOf('(', StringComparison.Ordinal);
        string baseName = (paren >= 0 ? trimmedLower[..paren] : trimmedLower).Trim();
        if (baseName is not ("decimal" or "numeric"))
        {
            return false;
        }

        if (paren < 0)
        {
            return true;
        }

        int close = trimmedLower.IndexOf(")", paren, StringComparison.Ordinal);
        if (close <= paren)
        {
            return false;
        }

        var parts = trimmedLower[(paren + 1)..close].Split(',', StringSplitOptions.TrimEntries);
        return parts.Length is 1 or 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int precision) && precision >= 38;
    }

    /// <summary>A scalar subquery raises on more than one row, except an ungrouped single count, min or max, which is exactly one row.</summary>
    private static bool ReturnsExactlyOneRow(SelectStatement subquery) =>
        subquery.Body is QuerySpecification { GroupBy: null, Projections: [ColumnSelectItem { Expression: FunctionCallExpression { Window: null } call }] }
        && call.Name.Parts.Count == 1 && call.Name.Parts[0].Value.ToLowerInvariant() is "count" or "min" or "max";

    private static bool IsAllowedBinary(BinaryExpression binary) => binary.Operator switch
    {
        BinaryOperator.And or BinaryOperator.Or
            or BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual => true,
        // A concatenation of row data can exceed a length limit (Oracle), so it is allowed only between constants.
        BinaryOperator.Concat => !HasColumn(binary.Left) && !HasColumn(binary.Right),
        // Arithmetic can overflow: only a literal expression that folds within the 64-bit range (a raising constant could sit behind a row-dependent branch).
        BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply => TryEvaluate(binary, out _),
        BinaryOperator.Divide or BinaryOperator.Modulo => IsSafeDivisor(binary.Right),
        _ => false
    };

    /// <summary>
    /// A text literal compared with a numeric (or unrecognised) catalog column is an implicit string-to-number conversion that fails on
    /// row data. The reverse (a number against a text column) is already rejected by the generator.
    /// </summary>
    private static bool IsConsistentLiteral(LiteralExpression literal)
    {
        if (literal.Type != LiteralType.String) return true;
        if (literal.ColumnTypeUnknown) return false;
        return literal.ColumnType is null || ColumnBindAdapter.Classify(literal.ColumnType) != ColumnTypeClass.Numeric;
    }

    private static bool IsAllowedFunction(FunctionCallExpression call)
    {
        if (call.Name.Parts.Count == 1 && call.Window is null && call.Name.Parts[0].Value.ToLowerInvariant() is "substring" or "substr")
        {
            return call.Arguments.Count is 2 or 3 && IsNonNegativeIntegerLiteral(call.Arguments[1], allowNegative: true)
                && (call.Arguments.Count == 2 || IsNonNegativeIntegerLiteral(call.Arguments[2], allowNegative: false));
        }

        // Zone conversion with literal zone names does not fail on valid values; timestamps at the extremes of the type range could
        // still fall outside it after the offset is applied. Accepted: the gateway's time bucketing relies on it.
        if (call.Name.Parts.Count == 1 && call.Window is null && call.Name.Parts[0].Value.Equals("convert_timezone", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < call.Arguments.Count - 1; i++)
            {
                if (!IsConstant(call.Arguments[i])) return false;
            }

            return call.Arguments.Count is 2 or 3;
        }

        if (call.Name.Parts.Count == 1 && call.Name.Parts[0].Value.Equals("concat", StringComparison.OrdinalIgnoreCase) && call.Arguments.Any(HasColumn))
        {
            return false;
        }

        if (call.Name.Parts.Count == 1 && call.Window is null && call.Filter is null && !call.Distinct
            && call.Name.Parts[0].Value.Equals("measure", StringComparison.OrdinalIgnoreCase))
        {
            return call.Arguments.Count == 1 && call.Arguments[0] is ColumnReference;
        }

        if (IsSafeAggregateForm(call))
        {
            return true;
        }

        if (call.Name.Parts.Count != 1 || !SqlFunctionPolicy.RowPolicySafeFunctions.Contains(call.Name.Parts[0].Value)) return false;
        if (call.Window is not null && call.Name.Parts[0].Value.ToLowerInvariant() is not ("count" or "min" or "max")) return false;
        return true;
    }

    /// <summary>Folds a literal-only +, -, * or negation with checked semantics; false when an operand is not a number literal or a result leaves the 64-bit range.</summary>
    private static bool TryEvaluate(Expression expression, out decimal value)
    {
        const decimal Limit = 9.2e18m;
        value = 0m;
        switch (Unwrap(expression))
        {
            case LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal } literal:
                return TryNumber(literal, out value) && Math.Abs(value) < Limit;
            case UnaryExpression { Operator: UnaryOperator.Negate } negate:
                if (!TryEvaluate(negate.Operand, out value)) return false;
                value = -value;
                return true;
            case BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply } binary
                when TryEvaluate(binary.Left, out var left) && TryEvaluate(binary.Right, out var right):
                try
                {
                    value = binary.Operator switch
                    {
                        BinaryOperator.Add => checked(left + right),
                        BinaryOperator.Subtract => checked(left - right),
                        _ => checked(left * right)
                    };
                }
                catch (OverflowException)
                {
                    return false;
                }

                return Math.Abs(value) < Limit;
            default:
                return false;
        }
    }

    private static bool HasColumn(Expression expression) => AstReflection.Collect<ColumnReference>(expression).Count > 0;

    private static bool IsConstant(Expression expression) => expression is LiteralExpression or ParameterReference or NamedBindMarkerExpression or PolicyParameterExpression;

    private static Expression Unwrap(Expression expression)
    {
        while (expression is ParenthesizedExpression p)
        {
            expression = p.Expression;
        }

        return expression;
    }

    private static bool TryNumber(Expression expression, out decimal value)
    {
        value = 0m;
        return Unwrap(expression) is LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal, Value: not null } literal
            && decimal.TryParse(Convert.ToString(literal.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A number literal with magnitude of at least one: not zero, and a fraction cannot turn a bounded value into an overflow.</summary>
    private static bool IsSafeDivisor(Expression expression) => TryNumber(expression, out var value) && Math.Abs(value) >= 1m;

    private static bool IsNonNegativeIntegerLiteral(Expression expression, bool allowNegative) =>
        TryNumber(expression, out var value) && (allowNegative || value >= 0m);
}

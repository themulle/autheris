namespace TrinoSqlEngine.Ast.Security;

using System;
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

    public static void Enforce(SqlNode userTree, ITableCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(userTree);
        ArgumentNullException.ThrowIfNull(catalog);

        AstReflection.Walk(userTree, node =>
        {
            if (node is Expression expression && !IsAllowed(expression))
            {
                throw new SecurityException(Message);
            }

            return true;
        });

        new RowPolicyCoercionGuard(catalog).Visit(userTree);
    }

    private static bool IsAllowed(Expression expression) => expression switch
    {
        ColumnReference or ParameterReference or NamedBindMarkerExpression or PolicyParameterExpression or PageOffsetParameterExpression
            or ParenthesizedExpression or CurrentDateTimeExpression or TypedLiteralExpression or IntervalLiteralExpression
            or GroupingOperationExpression or PositionExpression or ExtractExpression or RowValueExpression or ArrayConstructorExpression
            or IsDistinctFromExpression or BetweenExpression or CaseExpression or InListExpression or InSubqueryExpression
            or ExistsExpression or QuantifiedComparisonExpression => true,
        ScalarSubqueryExpression scalar => ReturnsExactlyOneRow(scalar.Subquery),
        LiteralExpression literal => IsConsistentLiteral(literal),
        BinaryExpression binary => IsAllowedBinary(binary),
        UnaryExpression unary => unary.Operator != UnaryOperator.Negate || !HasColumn(unary.Operand),
        LikeExpression like => IsConstant(like.Pattern) && (like.Escape is null || IsConstant(like.Escape)),
        FunctionCallExpression call => IsAllowedFunction(call),
        SubstringExpression sub => IsNonNegativeIntegerLiteral(sub.Start, allowNegative: true) && (sub.Length is null || IsNonNegativeIntegerLiteral(sub.Length, allowNegative: false)),
        TrimExpression trim => trim.Characters is null || IsConstant(trim.Characters),
        CastExpression cast => cast.IsTryCast || !HasColumn(cast.Operand),
        DateFunctionExpression date => date.Kind == DateFunctionKind.Trunc || !HasColumn(date.Source),
        // Everything else (subscript, unknown node kinds) can raise on row data: refused.
        _ => false
    };

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
        // Arithmetic can overflow: only without any column operand (a literal expression does not depend on row data).
        BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply => !HasColumn(binary.Left) && !HasColumn(binary.Right),
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

        if (call.Name.Parts.Count != 1 || !SqlFunctionPolicy.RowPolicySafeFunctions.Contains(call.Name.Parts[0].Value)) return false;
        if (call.Window is not null && call.Name.Parts[0].Value.ToLowerInvariant() is not ("count" or "min" or "max")) return false;
        return true;
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

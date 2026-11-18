namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Globalization;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Refuses user expressions whose outcome depends on row data (division, modulo, casts of columns) on statements that involve a row policy.
/// Runs on the user tree only; details: private advisory.
/// </summary>
public static class RowPolicyErrorGuard
{
    public const string Message = "The statement uses an expression that is not permitted on a table with a row policy.";

    public static void Enforce(SqlNode userTree)
    {
        ArgumentNullException.ThrowIfNull(userTree);

        foreach (var binary in AstReflection.Collect<BinaryExpression>(userTree))
        {
            if (binary.Operator is BinaryOperator.Divide or BinaryOperator.Modulo && !IsNonZeroNumber(binary.Right))
            {
                throw new SecurityException(Message);
            }
        }

        foreach (var cast in AstReflection.Collect<CastExpression>(userTree))
        {
            if (AstReflection.Collect<ColumnReference>(cast.Operand).Count > 0)
            {
                throw new SecurityException(Message);
            }
        }
    }

    private static bool IsNonZeroNumber(Expression expression)
    {
        while (expression is ParenthesizedExpression p)
        {
            expression = p.Expression;
        }

        return expression is LiteralExpression { Type: LiteralType.Integer or LiteralType.Decimal, Value: not null } literal
            && decimal.TryParse(Convert.ToString(literal.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value != 0m;
    }
}

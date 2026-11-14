namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Marks <see cref="BinaryExpression"/> division nodes whose left and right operands are both provably
/// integral (integer literals, casts to integral types, catalog columns declared with an integral SQL type,
/// and arithmetic compositions thereof). Unknown or non-integral types leave <c>IsIntegralDivision</c> false.
/// </summary>
internal sealed class IntegralOperandClassifier(ITableCatalog catalog) : ColumnScopeTracker(catalog)
{
    private static readonly Regex IntegralTypeRegex = new(
        @"\A(?:tinyint|smallint|int|integer|bigint|int2|int4|int8)(?:\s*\(\s*\d{1,5}\s*\))?\z|\A(?:number|decimal|numeric)\s*\(\s*\d{1,5}\s*(?:,\s*0+\s*)?\)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private readonly HashSet<OrderByClause> _selectOrderBys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Expression, bool> _integralNodes = new(ReferenceEqualityComparer.Instance);

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        if (node.OrderBy is not null && node.Body is QuerySpecification)
        {
            _selectOrderBys.Add(node.OrderBy);
        }

        return base.VisitSelectStatement(node);
    }

    public override SqlNode VisitOrderByClause(OrderByClause node)
    {
        if (!_selectOrderBys.Remove(node) || !PushLastBlock())
        {
            return base.VisitOrderByClause(node);
        }

        try
        {
            return base.VisitOrderByClause(node);
        }
        finally
        {
            PopBlock();
        }
    }

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        if (!SqlAstBuilder.CanUnrollLeftSpine(node.Operator)
            || node.Left is not BinaryExpression lb
            || !SqlAstBuilder.SameChainFamily(node.Operator, lb.Operator))
        {
            var left = (Expression)Visit(node.Left);
            var right = (Expression)Visit(node.Right);
            return ClassifyBinary(node, left, right);
        }

        var spine = new List<BinaryExpression>();
        Expression cur = node;
        while (cur is BinaryExpression cb && SqlAstBuilder.SameChainFamily(node.Operator, cb.Operator))
        {
            if (spine.Count > 0)
            {
                TickVisit();
            }

            spine.Add(cb);
            cur = cb.Left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = spine.Count - 1; i >= 0; i--)
        {
            var original = spine[i];
            var right = (Expression)Visit(original.Right);
            acc = ClassifyBinary(original, acc, right);
        }

        return acc;
    }

    private BinaryExpression ClassifyBinary(BinaryExpression original, Expression left, Expression right)
    {
        bool isIntegral = IsArithmeticIntegralOperator(original.Operator) && IsIntegral(left) && IsIntegral(right);
        bool isIntegralDivision = original.Operator == BinaryOperator.Divide && isIntegral;

        var result = left == original.Left && right == original.Right && isIntegralDivision == original.IsIntegralDivision
            ? original
            : original with { Left = left, Right = right, IsIntegralDivision = isIntegralDivision };

        if (isIntegral)
        {
            _integralNodes[result] = true;
        }

        return result;
    }

    private static bool IsArithmeticIntegralOperator(BinaryOperator op) =>
        op is BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo;

    private bool IsIntegral(Expression expr)
    {
        while (true)
        {
            if (_integralNodes.TryGetValue(expr, out bool known))
            {
                return known;
            }

            switch (expr)
            {
                case ParenthesizedExpression p:
                    expr = p.Expression;
                    continue;
                case UnaryExpression { Operator: UnaryOperator.Negate } u:
                    expr = u.Operand;
                    continue;
                case LiteralExpression { Type: LiteralType.Integer }:
                    return true;
                case CastExpression cast:
                    return IsIntegralTypeName(cast.TargetType);
                case ColumnReference:
                    return Resolve(expr) is { } resolved && IsIntegralTypeName(resolved.Column.DataType);
                case BinaryExpression b when b.Operator == BinaryOperator.Divide:
                    return b.IsIntegralDivision;
                default:
                    return false;
            }
        }
    }

    internal static bool IsIntegralTypeName(string? dataType) =>
        !string.IsNullOrWhiteSpace(dataType) && IntegralTypeRegex.IsMatch(dataType.Trim());
}

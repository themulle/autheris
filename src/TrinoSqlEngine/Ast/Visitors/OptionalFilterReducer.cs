namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Pass <c>"optional-filters"</c>. Reduces the optional filter shape <c>(@p IS NULL OR X)</c> (either operand order, parentheses
/// allowed) by the null-ness of the client parameter: a null parameter turns the OR into the constant <c>1 = 1</c>, any other
/// value keeps only <c>X</c>. It is a logical identity for the bound value. It runs on the secured tree, after every guard judged
/// the unreduced user tree, so the accept/refuse verdict never depends on parameter values. It matches only synthetic scalar
/// client parameters (<c>ParameterReference</c>); injected predicates carry policy parameters and are never matched.
/// </summary>
internal sealed class OptionalFilterReducer : SqlAstRewriter
{
    private readonly ClientNullShapes _nulls;

    public OptionalFilterReducer(ClientNullShapes nulls)
    {
        ArgumentNullException.ThrowIfNull(nulls);
        _nulls = nulls;
    }

    /// <summary>Number of optional filters reduced; used by tests.</summary>
    public int Reduced { get; private set; }

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        if (node.Operator != BinaryOperator.Or)
        {
            return base.VisitBinaryExpression(node);
        }

        // Iterative over the left spine of an OR chain (the AST builder unrolls long chains the same way).
        var spine = new List<BinaryExpression>();
        Expression cur = node;
        while (cur is BinaryExpression { Operator: BinaryOperator.Or } or)
        {
            if (spine.Count > 0)
            {
                TickVisit();
            }

            spine.Add(or);
            cur = or.Left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = spine.Count - 1; i >= 0; i--)
        {
            var original = spine[i];
            var right = (Expression)Visit(original.Right);
            acc = Reduce(original, acc, right);
        }

        return acc;
    }

    private Expression Reduce(BinaryExpression original, Expression left, Expression right)
    {
        if (TryIsNullParameter(left, out var leftName))
        {
            Reduced++;
            return _nulls.Contains(leftName) ? ClientListExpander.ConstantTrue : right;
        }

        if (TryIsNullParameter(right, out var rightName))
        {
            Reduced++;
            return _nulls.Contains(rightName) ? ClientListExpander.ConstantTrue : left;
        }

        return left == original.Left && right == original.Right ? original : original with { Left = left, Right = right };
    }

    private static bool TryIsNullParameter(Expression expression, out string name)
    {
        while (expression is ParenthesizedExpression paren)
        {
            expression = paren.Expression;
        }

        if (expression is UnaryExpression
            {
                Operator: UnaryOperator.IsNull,
                Operand: ParameterReference { IsSynthetic: true, ListIndex: null, ListCount: null, PositionalIndex: null } reference
            })
        {
            name = reference.Name;
            return true;
        }

        name = string.Empty;
        return false;
    }
}

namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Static AST validation visitor asserting structural and security invariants (I1 to I12).
/// </summary>
public sealed class AstValidationVisitor : SqlAstRewriter
{
    private int _depth = 0;
    private const int MaxAllowedDepth = 64;

    public void Validate(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _depth = 0;
        Visit(node);
    }

    public override SqlNode Visit(SqlNode node)
    {
        if (++_depth > MaxAllowedDepth)
        {
            throw new SecurityException($"AST depth exceeded invariant limit of {MaxAllowedDepth}.");
        }

        try
        {
            return base.Visit(node);
        }
        finally
        {
            _depth--;
        }
    }

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        if (!SqlAstBuilder.CanUnrollLeftSpine(node.Operator)
            || node.Left is not BinaryExpression lb
            || !SqlAstBuilder.SameChainFamily(node.Operator, lb.Operator))
        {
            return base.VisitBinaryExpression(node);
        }

        var spine = new List<BinaryExpression>();
        Expression cur = node;
        while (cur is BinaryExpression cb && SqlAstBuilder.SameChainFamily(node.Operator, cb.Operator))
        {
            if (spine.Count > 0)
            {
                TickVisit();
            }

            if (spine.Count >= SqlAstBuilder.MaxOperatorChainTerms)
            {
                throw new SecurityException(
                    $"Operator chain exceeded maximum length of {SqlAstBuilder.MaxOperatorChainTerms} terms.");
            }

            spine.Add(cb);
            cur = cb.Left;
        }

        var acc = (Expression)Visit(cur);
        for (int i = spine.Count - 1; i >= 0; i--)
        {
            var original = spine[i];
            var right = (Expression)Visit(original.Right);
            acc = acc == original.Left && right == original.Right
                ? original
                : original with { Left = acc, Right = right };
        }

        return acc;
    }
}

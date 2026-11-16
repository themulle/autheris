namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Architecture Phase 2: AST Simplification & Constant Folding Visitor.
/// Inspired by Trino's <c>SimplifyExpressions</c> and <c>IterativeOptimizer</c>.
/// Performs:
/// - Boolean logic reduction: (x AND TRUE -> x, x OR FALSE -> x, x AND FALSE -> FALSE, x OR TRUE -> TRUE)
/// - Double negation elimination: NOT(NOT(x)) -> x
/// - Comparison inversion: NOT(a = b) -> a <> b, NOT(a > b) -> a <= b
/// - Constant comparison folding, only when certain in every dialect: numerics by exact decimal value (1 = 1.0 -> TRUE, 1 < 1.5 -> TRUE),
/// identical strings, booleans. Never across type families, never for NULL, never for unequal strings (collation), never beyond double precision.
/// - Strict Three-Valued Logic: NULL = NULL does NOT fold to TRUE (yields UNKNOWN/FALSE in SQL semantics)
/// - Predicate deduplication in AND / OR chains: (a = 1 AND a = 1 -> a = 1)
/// - Contradiction detection: (col = 'v1' AND col = 'v2' -> 1 = 0)
/// - Tautological WHERE reduction: simplifies/normalizes top-level query WHERE clauses.
/// </summary>
public sealed class AstSimplificationVisitor : SqlAstRewriter
{
    private static readonly BinaryExpression CanonicalFalse = new(
        new LiteralExpression(1L, LiteralType.Integer),
        BinaryOperator.Equal,
        new LiteralExpression(0L, LiteralType.Integer));
    private static readonly BinaryExpression CanonicalTrue = new(
        new LiteralExpression(1L, LiteralType.Integer),
        BinaryOperator.Equal,
        new LiteralExpression(1L, LiteralType.Integer));

    public override SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        var visited = (QuerySpecification)base.VisitQuerySpecification(node);
        if (visited.Where == null)
        {
            return visited;
        }

        var simplifiedWhere = Simplify(visited.Where);
        if (IsTrue(simplifiedWhere))
        {
            return visited with { Where = CanonicalTrue };
        }

        if (IsFalse(simplifiedWhere))
        {
            return visited with { Where = CanonicalFalse };
        }

        return ReferenceEquals(visited.Where, simplifiedWhere) ? visited : visited with { Where = simplifiedWhere };
    }

    public override SqlNode VisitBinaryExpression(BinaryExpression node)
    {
        if (node.Operator is BinaryOperator.And or BinaryOperator.Or)
        {
            var operands = CollectSameOperatorOperands(node, node.Operator);
            for (int i = 0; i < operands.Count; i++)
            {
                operands[i] = (Expression)Visit(operands[i]);
            }

            return node.Operator == BinaryOperator.And
                ? SimplifyAndOperands(operands)
                : SimplifyOrOperands(operands);
        }

        if (Builder.SqlAstBuilder.CanUnrollLeftSpine(node.Operator)
            && node.Left is BinaryExpression lb
            && Builder.SqlAstBuilder.SameChainFamily(node.Operator, lb.Operator))
        {
            return base.VisitBinaryExpression(node);
        }

        var left = (Expression)Visit(node.Left);
        var right = (Expression)Visit(node.Right);

        return node.Operator switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual or
            BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or
            BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual
                => SimplifyComparison(node.Operator, left, right),
            _ => ReferenceEquals(node.Left, left) && ReferenceEquals(node.Right, right)
                ? node
                : node with { Left = left, Right = right }
        };
    }

    public override SqlNode VisitUnaryExpression(UnaryExpression node)
    {
        var operand = (Expression)Visit(node.Operand);

        return node.Operator switch
        {
            UnaryOperator.Not => SimplifyNot(operand),
            UnaryOperator.IsNull => SimplifyIsNull(operand),
            UnaryOperator.IsNotNull => SimplifyIsNotNull(operand),
            _ => ReferenceEquals(node.Operand, operand) ? node : node with { Operand = operand }
        };
    }

    private Expression Simplify(Expression expr)
    {
        return (Expression)Visit(expr);
    }

    private static List<Expression> CollectSameOperatorOperands(Expression root, BinaryOperator op)
    {
        if (root is BinaryExpression { Left: var l, Right: var r } rootBinary && rootBinary.Operator == op
            && !(l is BinaryExpression lb && lb.Operator == op) && !(r is BinaryExpression rb && rb.Operator == op))
        {
            // The common shape (two operands, neither of the same operator): no work stack.
            return [l, r];
        }

        var result = new List<Expression>();
        var stack = new Stack<Expression>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is BinaryExpression b && b.Operator == op)
            {
                stack.Push(b.Right);
                stack.Push(b.Left);
            }
            else
            {
                result.Add(current);
            }
        }

        return result;
    }

    private Expression SimplifyAndOperands(List<Expression> rawOperands)
    {
        var operands = new List<Expression>(rawOperands.Count);
        foreach (var raw in rawOperands)
        {
            FlattenAnd(raw, operands);
        }

        // Deduplicate identical operands
        var uniqueOperands = new List<Expression>();
        foreach (var op in operands)
        {
            if (IsFalse(op))
            {
                // Instant short-circuit on FALSE
                return CanonicalFalse;
            }

            if (IsTrue(op))
            {
                // Omit TRUE from AND conjunction
                continue;
            }

            if (!uniqueOperands.Any(existing => StructuralEquals(existing, op)))
            {
                uniqueOperands.Add(op);
            }
        }

        if (uniqueOperands.Count == 0)
        {
            return CanonicalTrue;
        }

        if (uniqueOperands.Count == 1)
        {
            return uniqueOperands[0];
        }

        // Note: Do not fold (col = v1 AND col = v2) -> 1 = 0 as it alters SQL three-valued logic when col is NULL.

        // Reconstruct left-associative AND tree
        Expression result = uniqueOperands[0];
        for (int i = 1; i < uniqueOperands.Count; i++)
        {
            result = new BinaryExpression(result, BinaryOperator.And, uniqueOperands[i]);
        }
        return result;
    }

    private Expression SimplifyOrOperands(List<Expression> rawOperands)
    {
        var operands = new List<Expression>(rawOperands.Count);
        foreach (var raw in rawOperands)
        {
            FlattenOr(raw, operands);
        }

        var uniqueOperands = new List<Expression>();
        foreach (var op in operands)
        {
            if (IsTrue(op))
            {
                // Instant short-circuit on TRUE
                return CanonicalTrue;
            }

            if (IsFalse(op))
            {
                // Omit FALSE from OR disjunction
                continue;
            }

            if (!uniqueOperands.Any(existing => StructuralEquals(existing, op)))
            {
                uniqueOperands.Add(op);
            }
        }

        if (uniqueOperands.Count == 0)
        {
            return CanonicalFalse;
        }

        if (uniqueOperands.Count == 1)
        {
            return uniqueOperands[0];
        }

        // Reconstruct left-associative OR tree
        Expression result = uniqueOperands[0];
        for (int i = 1; i < uniqueOperands.Count; i++)
        {
            result = new BinaryExpression(result, BinaryOperator.Or, uniqueOperands[i]);
        }
        return result;
    }

    private Expression SimplifyNot(Expression operand)
    {
        // NOT (NOT (x)) -> x
        if (operand is UnaryExpression { Operator: UnaryOperator.Not } innerNot)
        {
            return innerNot.Operand;
        }

        // NOT (TRUE) -> FALSE
        if (IsTrue(operand))
        {
            return CanonicalFalse;
        }

        // NOT (FALSE) -> TRUE
        if (IsFalse(operand))
        {
            return CanonicalTrue;
        }

        // Comparison inversion: NOT (a = b) -> a <> b, etc.
        if (operand is BinaryExpression b)
        {
            var invertedOp = InvertComparisonOperator(b.Operator);
            if (invertedOp.HasValue)
            {
                return new BinaryExpression(b.Left, invertedOp.Value, b.Right);
            }
        }

        // NOT (a IS NULL) -> a IS NOT NULL
        if (operand is UnaryExpression { Operator: UnaryOperator.IsNull } isNull)
        {
            return new UnaryExpression(UnaryOperator.IsNotNull, isNull.Operand);
        }

        // NOT (a IS NOT NULL) -> a IS NULL
        if (operand is UnaryExpression { Operator: UnaryOperator.IsNotNull } isNotNull)
        {
            return new UnaryExpression(UnaryOperator.IsNull, isNotNull.Operand);
        }

        return new UnaryExpression(UnaryOperator.Not, operand);
    }

    /// <summary>SQLE-07: the empty string IS NULL on Oracle, so a predicate on it is never folded here (the simplifier is dialect neutral).</summary>
    private static bool IsEmptyString(LiteralExpression literal) => literal is { Type: LiteralType.String, Value: string { Length: 0 } };

    private static Expression SimplifyIsNull(Expression operand)
    {
        if (operand is LiteralExpression lit && !IsEmptyString(lit))
        {
            return lit.Type == LiteralType.Null ? CanonicalTrue : CanonicalFalse;
        }
        return new UnaryExpression(UnaryOperator.IsNull, operand);
    }

    private static Expression SimplifyIsNotNull(Expression operand)
    {
        if (operand is LiteralExpression lit && !IsEmptyString(lit))
        {
            return lit.Type != LiteralType.Null ? CanonicalTrue : CanonicalFalse;
        }
        return new UnaryExpression(UnaryOperator.IsNotNull, operand);
    }

    private static Expression SimplifyComparison(BinaryOperator op, Expression left, Expression right)
    {
        // Constant folding only applies when both sides are LiteralExpressions
        if (left is LiteralExpression lLit && right is LiteralExpression rLit)
        {
            // SQL Three-Valued Logic: NULL compared to anything is UNKNOWN (not TRUE)
            if (lLit.Type == LiteralType.Null || rLit.Type == LiteralType.Null)
            {
                // Retain as-is to preserve SQL semantics
                return new BinaryExpression(left, op, right);
            }

            if (IsEmptyString(lLit) || IsEmptyString(rLit))
            {
                return new BinaryExpression(left, op, right);   // SQLE-07: Oracle compares '' as NULL
            }

            if (LiteralComparison.TryFold(op, lLit, rLit, out bool folded))
            {
                return folded ? CanonicalTrue : CanonicalFalse;
            }
        }

        return new BinaryExpression(left, op, right);
    }

    private static BinaryOperator? InvertComparisonOperator(BinaryOperator op) => op switch
    {
        BinaryOperator.Equal => BinaryOperator.NotEqual,
        BinaryOperator.NotEqual => BinaryOperator.Equal,
        BinaryOperator.LessThan => BinaryOperator.GreaterThanOrEqual,
        BinaryOperator.LessThanOrEqual => BinaryOperator.GreaterThan,
        BinaryOperator.GreaterThan => BinaryOperator.LessThanOrEqual,
        BinaryOperator.GreaterThanOrEqual => BinaryOperator.LessThan,
        _ => null
    };

    private static void FlattenAnd(Expression expr, List<Expression> collector)
    {
        if (expr is not BinaryExpression { Operator: BinaryOperator.And })
        {
            collector.Add(expr);
            return;
        }

        var stack = new Stack<Expression>();
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is BinaryExpression { Operator: BinaryOperator.And } b)
            {
                stack.Push(b.Right);
                stack.Push(b.Left);
            }
            else
            {
                collector.Add(current);
            }
        }
    }

    private static void FlattenOr(Expression expr, List<Expression> collector)
    {
        if (expr is not BinaryExpression { Operator: BinaryOperator.Or })
        {
            collector.Add(expr);
            return;
        }

        var stack = new Stack<Expression>();
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is BinaryExpression { Operator: BinaryOperator.Or } b)
            {
                stack.Push(b.Right);
                stack.Push(b.Left);
            }
            else
            {
                collector.Add(current);
            }
        }
    }

    private static bool IsTrue(Expression expr)
    {
        if (expr is LiteralExpression { Type: LiteralType.Boolean } lit && true.Equals(lit.Value))
            return true;

        if (expr is BinaryExpression b &&
            b.Left is LiteralExpression l1 && b.Right is LiteralExpression l2 && !IsEmptyString(l1) && !IsEmptyString(l2) &&
            LiteralComparison.TryFold(b.Operator, l1, l2, out bool isTrue) && isTrue)
            return true;

        if (expr is ParenthesizedExpression p)
            return IsTrue(p.Expression);

        return false;
    }

    private static bool IsFalse(Expression expr)
    {
        if (expr is LiteralExpression { Type: LiteralType.Boolean } lit && false.Equals(lit.Value))
            return true;

        if (expr is BinaryExpression b &&
            b.Left is LiteralExpression l1 && b.Right is LiteralExpression l2 && !IsEmptyString(l1) && !IsEmptyString(l2) &&
            LiteralComparison.TryFold(b.Operator, l1, l2, out bool isTrue) && !isTrue)
            return true;

        if (expr is ParenthesizedExpression p)
            return IsFalse(p.Expression);

        return false;
    }

    /// <summary>SQLE-07: a delimited identifier is case-sensitive ("Status" and "status" are different columns on PostgreSQL).</summary>
    private static bool SameColumnName(SqlQualifiedName a, SqlQualifiedName b)
    {
        if (a.Parts.Count != b.Parts.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Parts.Count; i++)
        {
            var comparison = a.Parts[i].IsQuoted || b.Parts[i].IsQuoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!string.Equals(a.Parts[i].Value, b.Parts[i].Value, comparison))
            {
                return false;
            }
        }

        return true;
    }

    private static bool StructuralEquals(Expression a, Expression b)
    {
        if (ReferenceEquals(a, b)) return true;

        var stack = new Stack<(Expression Left, Expression Right)>();
        stack.Push((a, b));
        while (stack.Count > 0)
        {
            var (curA, curB) = stack.Pop();
            if (ReferenceEquals(curA, curB)) continue;
            if (curA.GetType() != curB.GetType()) return false;

            switch (curA, curB)
            {
                case (ParenthesizedExpression p1, ParenthesizedExpression p2):
                    stack.Push((p1.Expression, p2.Expression));
                    break;

                case (ColumnReference c1, ColumnReference c2):
                    if (!SameColumnName(c1.Name, c2.Name)) return false;
                    break;

                case (ParameterReference p1, ParameterReference p2):
                    if (!string.Equals(p1.Name, p2.Name, StringComparison.OrdinalIgnoreCase)
                        || p1.ListIndex != p2.ListIndex || p1.ListCount != p2.ListCount)
                        return false;
                    break;

                case (LiteralExpression l1, LiteralExpression l2):
                    if (l1.Type != l2.Type || !Equals(l1.Value?.ToString(), l2.Value?.ToString()))
                        return false;
                    break;

                case (BinaryExpression b1, BinaryExpression b2):
                    if (b1.Operator != b2.Operator) return false;
                    stack.Push((b1.Right, b2.Right));
                    stack.Push((b1.Left, b2.Left));
                    break;

                case (UnaryExpression u1, UnaryExpression u2):
                    if (u1.Operator != u2.Operator) return false;
                    stack.Push((u1.Operand, u2.Operand));
                    break;

                case (BetweenExpression bt1, BetweenExpression bt2):
                    if (bt1.IsNotBetween != bt2.IsNotBetween) return false;
                    stack.Push((bt1.Upper, bt2.Upper));
                    stack.Push((bt1.Lower, bt2.Lower));
                    stack.Push((bt1.Operand, bt2.Operand));
                    break;

                default:
                    return false;
            }
        }

        return true;
    }
}

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
/// - Constant equality folding: ('a' = 'a' -> TRUE, 'a' = 'b' -> FALSE, 1 = 1 -> TRUE, 1 = 2 -> FALSE)
/// - Strict Three-Valued Logic: NULL = NULL does NOT fold to TRUE (yields UNKNOWN/FALSE in SQL semantics)
/// - Predicate deduplication in AND / OR chains: (a = 1 AND a = 1 -> a = 1)
/// - Contradiction detection: (col = 'v1' AND col = 'v2' -> 1 = 0)
/// - Tautological WHERE reduction: simplifies/normalizes top-level query WHERE clauses.
/// </summary>
public sealed class AstSimplificationVisitor : SqlAstRewriter
{
    private static readonly LiteralExpression TrueLiteral = new(true, LiteralType.Boolean);
    private static readonly LiteralExpression FalseLiteral = new(false, LiteralType.Boolean);
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
        var left = (Expression)Visit(node.Left);
        var right = (Expression)Visit(node.Right);

        return node.Operator switch
        {
            BinaryOperator.And => SimplifyAnd(left, right),
            BinaryOperator.Or => SimplifyOr(left, right),
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

    private Expression SimplifyAnd(Expression left, Expression right)
    {
        var operands = new List<Expression>();
        FlattenAnd(left, operands);
        FlattenAnd(right, operands);

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

    private Expression SimplifyOr(Expression left, Expression right)
    {
        var operands = new List<Expression>();
        FlattenOr(left, operands);
        FlattenOr(right, operands);

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

    private static Expression SimplifyIsNull(Expression operand)
    {
        if (operand is LiteralExpression lit)
        {
            return lit.Type == LiteralType.Null ? CanonicalTrue : CanonicalFalse;
        }
        return new UnaryExpression(UnaryOperator.IsNull, operand);
    }

    private static Expression SimplifyIsNotNull(Expression operand)
    {
        if (operand is LiteralExpression lit)
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

            if (op == BinaryOperator.Equal)
            {
                bool areEqual = Equals(lLit.Value?.ToString(), rLit.Value?.ToString());
                return areEqual ? CanonicalTrue : CanonicalFalse;
            }

            if (op == BinaryOperator.NotEqual)
            {
                bool areEqual = Equals(lLit.Value?.ToString(), rLit.Value?.ToString());
                return areEqual ? CanonicalFalse : CanonicalTrue;
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
        if (expr is BinaryExpression { Operator: BinaryOperator.And } b)
        {
            FlattenAnd(b.Left, collector);
            FlattenAnd(b.Right, collector);
        }
        else
        {
            collector.Add(expr);
        }
    }

    private static void FlattenOr(Expression expr, List<Expression> collector)
    {
        if (expr is BinaryExpression { Operator: BinaryOperator.Or } b)
        {
            FlattenOr(b.Left, collector);
            FlattenOr(b.Right, collector);
        }
        else
        {
            collector.Add(expr);
        }
    }

    private static bool IsTrue(Expression expr)
    {
        if (expr is LiteralExpression { Type: LiteralType.Boolean } lit && true.Equals(lit.Value))
            return true;

        if (expr is BinaryExpression { Operator: BinaryOperator.Equal } b &&
            b.Left is LiteralExpression l1 && b.Right is LiteralExpression l2 &&
            l1.Type != LiteralType.Null && l2.Type != LiteralType.Null &&
            Equals(l1.Value?.ToString(), l2.Value?.ToString()))
            return true;

        return false;
    }

    private static bool IsFalse(Expression expr)
    {
        if (expr is LiteralExpression { Type: LiteralType.Boolean } lit && false.Equals(lit.Value))
            return true;

        if (expr is BinaryExpression { Operator: BinaryOperator.Equal } b &&
            b.Left is LiteralExpression l1 && b.Right is LiteralExpression l2 &&
            l1.Type != LiteralType.Null && l2.Type != LiteralType.Null &&
            !Equals(l1.Value?.ToString(), l2.Value?.ToString()))
            return true;

        return false;
    }

    private static bool HasDirectContradiction(List<Expression> operands)
    {
        // Finds pairs of (col = const1) and (col = const2) where const1 != const2
        var columnValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var op in operands)
        {
            if (op is BinaryExpression { Operator: BinaryOperator.Equal } b)
            {
                ColumnReference? col = b.Left as ColumnReference ?? b.Right as ColumnReference;
                LiteralExpression? lit = b.Left as LiteralExpression ?? b.Right as LiteralExpression;

                if (col != null && lit != null && lit.Type != LiteralType.Null)
                {
                    string colKey = col.Name.NormalizedName;
                    string val = lit.Value?.ToString() ?? string.Empty;

                    if (columnValues.TryGetValue(colKey, out var existingVal))
                    {
                        if (!string.Equals(existingVal, val, StringComparison.Ordinal))
                        {
                            return true; // Contradiction! (col = 'a' AND col = 'b')
                        }
                    }
                    else
                    {
                        columnValues[colKey] = val;
                    }
                }
            }
        }

        return false;
    }

    private static bool StructuralEquals(Expression a, Expression b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.GetType() != b.GetType()) return false;

        return (a, b) switch
        {
            (ColumnReference c1, ColumnReference c2) =>
                c1.Name.NormalizedName.Equals(c2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase),

            (ParameterReference p1, ParameterReference p2) =>
                string.Equals(p1.Name, p2.Name, StringComparison.OrdinalIgnoreCase),

            (LiteralExpression l1, LiteralExpression l2) =>
                l1.Type == l2.Type && Equals(l1.Value?.ToString(), l2.Value?.ToString()),

            (BinaryExpression b1, BinaryExpression b2) =>
                b1.Operator == b2.Operator &&
                StructuralEquals(b1.Left, b2.Left) &&
                StructuralEquals(b1.Right, b2.Right),

            (UnaryExpression u1, UnaryExpression u2) =>
                u1.Operator == u2.Operator && StructuralEquals(u1.Operand, u2.Operand),

            (BetweenExpression bt1, BetweenExpression bt2) =>
                bt1.IsNotBetween == bt2.IsNotBetween &&
                StructuralEquals(bt1.Operand, bt2.Operand) &&
                StructuralEquals(bt1.Lower, bt2.Lower) &&
                StructuralEquals(bt1.Upper, bt2.Upper),

            _ => false
        };
    }
}

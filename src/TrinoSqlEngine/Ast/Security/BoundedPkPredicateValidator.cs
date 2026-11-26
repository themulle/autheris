namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Validates that the <c>WHERE</c> clause of a bounded single-table <c>UPDATE</c> or <c>DELETE</c> statically constrains every
/// declared primary key column via conjunctive (<c>AND</c>) equality or, for a single-column primary key, a bounded <c>IN (...)</c>
/// list, without subqueries or top-level disjunctions (<c>OR</c>). Returns the static upper bound on matched rows.
/// </summary>
public static class BoundedPkPredicateValidator
{
    public static long Validate(
        Expression? where,
        TableCatalogEntry entry,
        IReadOnlyList<string>? primaryKeyColumns,
        long maxRows)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (primaryKeyColumns is null || primaryKeyColumns.Count == 0)
        {
            throw new SecurityException("Bounded single-table UPDATE or DELETE requires declared primary key columns.");
        }

        if (maxRows <= 0)
        {
            throw new SecurityException("Bounded single-table UPDATE or DELETE requires a positive maximum row bound.");
        }

        if (where is null)
        {
            throw new SecurityException("Bounded single-table UPDATE or DELETE requires a WHERE clause constraining every primary key column.");
        }

        RejectSubqueriesAndDisjunctions(where);

        var pkSet = new HashSet<string>(primaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var boundColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long? singlePkInCount = null;

        var conjuncts = new List<Expression>();
        CollectConjuncts(where, conjuncts);

        foreach (var conjunct in conjuncts)
        {
            var expr = Unwrap(conjunct);
            if (expr is BinaryExpression { Operator: BinaryOperator.Equal } eq)
            {
                if (TryExtractTargetColumn(eq.Left, entry, out var leftCol) && IsScalarConstantOrParam(eq.Right) && pkSet.Contains(leftCol))
                {
                    boundColumns.Add(leftCol);
                }
                else if (TryExtractTargetColumn(eq.Right, entry, out var rightCol) && IsScalarConstantOrParam(eq.Left) && pkSet.Contains(rightCol))
                {
                    boundColumns.Add(rightCol);
                }
            }
            else if (pkSet.Count == 1 && expr is InListExpression { IsNotIn: false } inList && inList.Items.Count > 0)
            {
                if (TryExtractTargetColumn(inList.Operand, entry, out var col) &&
                    pkSet.Contains(col) &&
                    inList.Items.All(IsScalarConstantOrParam))
                {
                    boundColumns.Add(col);
                    singlePkInCount = singlePkInCount.HasValue
                        ? Math.Min(singlePkInCount.Value, inList.Items.Count)
                        : inList.Items.Count;
                }
            }
        }

        foreach (var pk in pkSet)
        {
            if (!boundColumns.Contains(pk))
            {
                throw new SecurityException("Bounded single-table UPDATE or DELETE must constrain every primary key column with an equality predicate.");
            }
        }

        long upperBound = singlePkInCount ?? 1L;
        if (upperBound > maxRows)
        {
            throw new SecurityException("Bounded single-table UPDATE or DELETE exceeds the configured maximum row bound.");
        }

        return upperBound;
    }

    private static void RejectSubqueriesAndDisjunctions(Expression where)
    {
        bool hasSubquery = false;
        AstReflection.Walk(where, node =>
        {
            if (node is SelectStatement)
            {
                hasSubquery = true;
                return false;
            }

            return true;
        });

        if (hasSubquery)
        {
            throw new SecurityException("Bounded single-table UPDATE or DELETE does not permit subqueries in WHERE.");
        }

        var stack = new Stack<Expression>();
        stack.Push(where);
        while (stack.Count > 0)
        {
            var current = Unwrap(stack.Pop());
            if (current is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                stack.Push(and.Left);
                stack.Push(and.Right);
            }
            else if (current is BinaryExpression { Operator: BinaryOperator.Or })
            {
                throw new SecurityException("Bounded single-table UPDATE or DELETE does not permit top-level OR predicates.");
            }
        }
    }

    private static void CollectConjuncts(Expression where, List<Expression> into)
    {
        var stack = new Stack<Expression>();
        stack.Push(where);
        while (stack.Count > 0)
        {
            var current = Unwrap(stack.Pop());
            if (current is BinaryExpression { Operator: BinaryOperator.And } and)
            {
                stack.Push(and.Right);
                stack.Push(and.Left);
            }
            else
            {
                into.Add(current);
            }
        }
    }

    private static Expression Unwrap(Expression expr)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        return expr;
    }

    private static bool TryExtractTargetColumn(Expression expr, TableCatalogEntry entry, out string columnName)
    {
        expr = Unwrap(expr);
        if (expr is ColumnReference col)
        {
            var parts = col.Name.Parts;
            if (parts.Count == 1 ||
                (parts.Count == 2 && string.Equals(parts[0].Value, entry.Identity.Table, StringComparison.OrdinalIgnoreCase)))
            {
                columnName = col.Name.SimpleName;
                return true;
            }
        }

        columnName = string.Empty;
        return false;
    }

    private static bool IsScalarConstantOrParam(Expression expr)
    {
        expr = Unwrap(expr);
        return expr switch
        {
            LiteralExpression { Type: not LiteralType.Null } => true,
            ParameterReference => true,
            PolicyParameterExpression => true,
            CastExpression cast => IsScalarConstantOrParam(cast.Operand),
            UnaryExpression { Operator: UnaryOperator.Negate } neg => IsScalarConstantOrParam(neg.Operand),
            _ => false
        };
    }
}

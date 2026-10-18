namespace Autheris.Application.Sql.Federation.Conjuncts;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;

public enum ConjunctOrigin
{
    Where,
    InnerJoinOn,
    LeftOuterJoinOn,
    RightOuterJoinOn
}

public sealed record ExtractedConjunct(
    Expression Expression,
    ConjunctOrigin Origin,
    IReadOnlySet<TableSource>? AllowedTableSources = null);

/// <summary>
/// WP-122-1: Splits WHERE and join ON clauses into boolean AND conjuncts using an AST visitor.
/// Preserves outer join semantics and Three-Valued Logic safety boundaries.
/// </summary>
public static class SqlConjunctSplitter
{
    public static IReadOnlyList<ExtractedConjunct> Split(SqlStatement? statement)
    {
        if (statement is not SelectStatement select)
        {
            return Array.Empty<ExtractedConjunct>();
        }

        if (select.Body is not QuerySpecification querySpec)
        {
            return Array.Empty<ExtractedConjunct>();
        }

        var result = new List<ExtractedConjunct>();

        if (querySpec.From != null)
        {
            // SEC-DATA122-03: Full outer joins invalidate all pushdown (fail closed).
            if (HasFullOuterJoin(querySpec.From))
            {
                return Array.Empty<ExtractedConjunct>();
            }

            var nullSupplyingLeafTables = new HashSet<TableSource>();
            CollectJoinConjuncts(querySpec.From, result, nullSupplyingLeafTables);

            if (querySpec.Where != null)
            {
                var whereConjuncts = new List<Expression>();
                FlattenAnd(querySpec.Where, whereConjuncts);

                // WHERE conjuncts can only be pushed to preserved tables (not null-supplying).
                var allLeafTables = new HashSet<TableSource>();
                CollectLeafTableSources(querySpec.From, allLeafTables);
                allLeafTables.ExceptWith(nullSupplyingLeafTables);

                foreach (var expr in whereConjuncts)
                {
                    result.Add(new ExtractedConjunct(expr, ConjunctOrigin.Where, allLeafTables));
                }
            }
        }
        else if (querySpec.Where != null)
        {
            var whereConjuncts = new List<Expression>();
            FlattenAnd(querySpec.Where, whereConjuncts);
            foreach (var expr in whereConjuncts)
            {
                result.Add(new ExtractedConjunct(expr, ConjunctOrigin.Where));
            }
        }

        return result;
    }

    /// <summary>
    /// Decomposes nested binary AND expressions and parenthesized expressions into a flat list of conjuncts.
    /// </summary>
    public static void FlattenAnd(Expression expr, List<Expression> collector)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

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

    private static bool HasFullOuterJoin(TableSource source)
    {
        if (source is JoinedTableSource join)
        {
            if (join.Type == JoinType.FullOuter)
            {
                return true;
            }

            return HasFullOuterJoin(join.Left) || HasFullOuterJoin(join.Right);
        }

        return false;
    }

    private static void CollectLeafTableSources(TableSource source, HashSet<TableSource> collector)
    {
        if (source is JoinedTableSource join)
        {
            CollectLeafTableSources(join.Left, collector);
            CollectLeafTableSources(join.Right, collector);
        }
        else
        {
            collector.Add(source);
        }
    }

    private static void CollectJoinConjuncts(
        TableSource source,
        List<ExtractedConjunct> result,
        HashSet<TableSource> nullSupplyingLeafTables)
    {
        if (source is not JoinedTableSource join)
        {
            return;
        }

        CollectJoinConjuncts(join.Left, result, nullSupplyingLeafTables);
        CollectJoinConjuncts(join.Right, result, nullSupplyingLeafTables);

        var leftLeaves = new HashSet<TableSource>();
        CollectLeafTableSources(join.Left, leftLeaves);

        var rightLeaves = new HashSet<TableSource>();
        CollectLeafTableSources(join.Right, rightLeaves);

        if (join.Type == JoinType.LeftOuter)
        {
            nullSupplyingLeafTables.UnionWith(rightLeaves);

            if (join.Condition is OnJoinCondition onCond)
            {
                var onConjuncts = new List<Expression>();
                FlattenAnd(onCond.Predicate, onConjuncts);
                foreach (var expr in onConjuncts)
                {
                    result.Add(new ExtractedConjunct(expr, ConjunctOrigin.LeftOuterJoinOn, rightLeaves));
                }
            }
        }
        else if (join.Type == JoinType.RightOuter)
        {
            nullSupplyingLeafTables.UnionWith(leftLeaves);

            if (join.Condition is OnJoinCondition onCond)
            {
                var onConjuncts = new List<Expression>();
                FlattenAnd(onCond.Predicate, onConjuncts);
                foreach (var expr in onConjuncts)
                {
                    result.Add(new ExtractedConjunct(expr, ConjunctOrigin.RightOuterJoinOn, leftLeaves));
                }
            }
        }
        else if (join.Type == JoinType.Inner)
        {
            if (join.Condition is OnJoinCondition onCond)
            {
                var onConjuncts = new List<Expression>();
                FlattenAnd(onCond.Predicate, onConjuncts);
                var allLeaves = new HashSet<TableSource>(leftLeaves);
                allLeaves.UnionWith(rightLeaves);
                foreach (var expr in onConjuncts)
                {
                    result.Add(new ExtractedConjunct(expr, ConjunctOrigin.InnerJoinOn, allLeaves));
                }
            }
        }
    }
}

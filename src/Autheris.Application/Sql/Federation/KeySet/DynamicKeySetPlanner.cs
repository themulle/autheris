namespace Autheris.Application.Sql.Federation.KeySet;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Result of planning a dynamic key-set pushdown between a driving table and a driven table.
/// </summary>
public sealed record KeySetJoinPlan(
    ResolvedSourceTable DrivingTable,
    ResolvedSourceTable DrivenTable,
    string DrivingKeyColumn,
    string DrivenKeyColumn)
{
    public TableIdentifier DrivingTableIdentifier => DrivingTable.ResolvedIdentifier;
    public TableIdentifier DrivenTableIdentifier => DrivenTable.ResolvedIdentifier;
}

/// <summary>
/// WP-124-1: Dynamic Key-Set Planner & Sizing Heuristics.
/// Detects cross-source equi-joins, determines driving vs driven dependencies using pushed predicates and catalog hints,
/// and guards against non-equi joins and circular dependencies.
/// </summary>
public static class DynamicKeySetPlanner
{
    public static IReadOnlyList<KeySetJoinPlan> Plan(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        IReadOnlyDictionary<TableIdentifier, List<SingleTableConjunct>>? pushedConjuncts = null)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(options);

        if (statement == null || tables.Count < 2 || options.DynamicKeySet?.Enabled == false)
        {
            return Array.Empty<KeySetJoinPlan>();
        }

        var conjuncts = SqlConjunctSplitter.Split(statement);
        if (conjuncts.Count == 0)
        {
            return Array.Empty<KeySetJoinPlan>();
        }

        // Find candidate cross-source equi-join pairs
        var candidateJoins = new List<(ResolvedSourceTable Table1, string Column1, ResolvedSourceTable Table2, string Column2)>();

        foreach (var conjunct in conjuncts)
        {
            var expr = conjunct.Expression;
            while (expr is ParenthesizedExpression paren)
            {
                expr = paren.Expression;
            }

            if (expr is BinaryExpression { Operator: BinaryOperator.Equal } eq)
            {
                var leftCol = ExtractColumnReference(eq.Left);
                var rightCol = ExtractColumnReference(eq.Right);

                if (leftCol != null && rightCol != null)
                {
                    var (t1, c1) = SingleTableConjunctClassifier.ResolveColumn(leftCol, tables);
                    var (t2, c2) = SingleTableConjunctClassifier.ResolveColumn(rightCol, tables);

                    if (t1 != null && t2 != null && t1 != t2)
                    {
                        bool isCrossSource = !string.Equals(t1.EffectiveDataSource, t2.EffectiveDataSource, StringComparison.OrdinalIgnoreCase) ||
                                             !string.Equals(t1.ResolvedIdentifier.Domain, t2.ResolvedIdentifier.Domain, StringComparison.OrdinalIgnoreCase);

                        if (isCrossSource)
                        {
                            candidateJoins.Add((t1, c1, t2, c2));
                        }
                    }
                }
            }
        }

        if (candidateJoins.Count == 0)
        {
            return Array.Empty<KeySetJoinPlan>();
        }

        // Single-table conjuncts by table if not provided
        var singleConjunctsMap = pushedConjuncts ?? SingleTableConjunctClassifier.Classify(conjuncts, tables)
            .SingleTableConjuncts
            .GroupBy(c => c.Table)
            .ToDictionary(g => g.Key, g => g.ToList());

        var resolvedPlans = new List<KeySetJoinPlan>();
        var drivenTables = new HashSet<TableIdentifier>();

        foreach (var (t1, c1, t2, c2) in candidateJoins)
        {
            // Sizing Heuristic (Decision D-1):
            // 1. Selective Single-Table Filter (#122):
            // If Table A has pushable equality/range predicates while Table B is unfiltered, Table A is driving.
            bool t1HasFilters = HasPushableFilters(t1, singleConjunctsMap, decisions, options);
            bool t2HasFilters = HasPushableFilters(t2, singleConjunctsMap, decisions, options);

            ResolvedSourceTable? driving = null;
            ResolvedSourceTable? driven = null;
            string? drivingCol = null;
            string? drivenCol = null;

            if (t1HasFilters && !t2HasFilters)
            {
                driving = t1;
                drivingCol = c1;
                driven = t2;
                drivenCol = c2;
            }
            else if (t2HasFilters && !t1HasFilters)
            {
                driving = t2;
                drivingCol = c2;
                driven = t1;
                drivenCol = c1;
            }
            else
            {
                // 2. Catalog Cardinality Hints (expected_rows):
                // If neither or both have filters, inspect catalog metadata expected_rows.
                // If A.expected_rows * 10 <= B.expected_rows, Table A is designated driving.
                var rows1 = t1.Metadata.EffectiveExpectedRows;
                var rows2 = t2.Metadata.EffectiveExpectedRows;

                if (rows1.HasValue && rows2.HasValue)
                {
                    if (rows1.Value * 10 <= rows2.Value)
                    {
                        driving = t1;
                        drivingCol = c1;
                        driven = t2;
                        drivenCol = c2;
                    }
                    else if (rows2.Value * 10 <= rows1.Value)
                    {
                        driving = t2;
                        drivingCol = c2;
                        driven = t1;
                        drivenCol = c1;
                    }
                }
            }

            if (driving != null && driven != null && drivingCol != null && drivenCol != null)
            {
                // Driven table must be a SQL data source
                if (driven.Metadata.Table.DataSourceType != DataSourceType.Sql)
                {
                    continue;
                }

                // Check join keys clear (SEC-DATA124-01 / SEC-FED-19)
                if (decisions.TryGetValue(driving.ResolvedIdentifier, out var drivingDec) &&
                    decisions.TryGetValue(driven.ResolvedIdentifier, out var drivenDec))
                {
                    if (!KeySetGovernanceGuard.AreJoinKeysClear(
                            driving.Metadata, drivingDec, drivingCol,
                            driven.Metadata, drivenDec, drivenCol))
                    {
                        continue;
                    }
                }

                // Prevent multiple driving sides for the same driven table
                if (drivenTables.Add(driven.ResolvedIdentifier))
                {
                    resolvedPlans.Add(new KeySetJoinPlan(driving, driven, drivingCol, drivenCol));
                }
            }
        }

        // Circular Dependency Guard
        if (HasCircularDependency(resolvedPlans))
        {
            return Array.Empty<KeySetJoinPlan>();
        }

        return resolvedPlans;
    }

    public static KeySetJoinPlan? PlanSingle(
        SqlStatement? statement,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        IReadOnlyDictionary<TableIdentifier, List<SingleTableConjunct>>? pushedConjuncts = null)
    {
        return Plan(statement, tables, decisions, options, pushedConjuncts).FirstOrDefault();
    }

    private static bool HasPushableFilters(
        ResolvedSourceTable table,
        IReadOnlyDictionary<TableIdentifier, List<SingleTableConjunct>> singleConjunctsMap,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options)
    {
        if (!singleConjunctsMap.TryGetValue(table.ResolvedIdentifier, out var conjuncts) || conjuncts.Count == 0)
        {
            return false;
        }

        if (!decisions.TryGetValue(table.ResolvedIdentifier, out var decision))
        {
            return false;
        }

        foreach (var c in conjuncts)
        {
            var eval = PushabilityEvaluator.Evaluate(
                c,
                table.Metadata,
                decision,
                table.Metadata.Dialect,
                null,
                options.PredicatePushdown);

            if (eval.IsPushable)
            {
                return true;
            }
        }

        return false;
    }

    private static ColumnReference? ExtractColumnReference(Expression expr)
    {
        while (expr is ParenthesizedExpression paren)
        {
            expr = paren.Expression;
        }

        return expr as ColumnReference;
    }

    private static bool HasCircularDependency(IReadOnlyList<KeySetJoinPlan> plans)
    {
        if (plans.Count <= 1) return false;

        var graph = new Dictionary<TableIdentifier, HashSet<TableIdentifier>>();
        foreach (var p in plans)
        {
            if (!graph.TryGetValue(p.DrivingTableIdentifier, out var targets))
            {
                targets = new HashSet<TableIdentifier>();
                graph[p.DrivingTableIdentifier] = targets;
            }
            targets.Add(p.DrivenTableIdentifier);
        }

        var visited = new HashSet<TableIdentifier>();
        var stack = new HashSet<TableIdentifier>();

        bool Dfs(TableIdentifier node)
        {
            visited.Add(node);
            stack.Add(node);

            if (graph.TryGetValue(node, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (!visited.Contains(neighbor) && Dfs(neighbor))
                    {
                        return true;
                    }
                    if (stack.Contains(neighbor))
                    {
                        return true;
                    }
                }
            }

            stack.Remove(node);
            return false;
        }

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node) && Dfs(node))
            {
                return true;
            }
        }

        return false;
    }
}

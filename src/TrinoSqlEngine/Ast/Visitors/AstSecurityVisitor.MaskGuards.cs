using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Antlr4.Runtime;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Ast.Visitors;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private void EnsureNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInDml)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private void EnsureNoMaskedColumnReferencesInPredicate(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInPredicates)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private Stack<SqlNode>? _maskGuardStack;

    private void CheckNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        var stack = _maskGuardStack ??= new Stack<SqlNode>(64);   // one visitor per compile: the work stack is reused by every call
        stack.Clear();
        stack.Push(scope);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference colRef)
            {
                string colName = colRef.Name.SimpleName;
                bool isMasked = _options.MaskedColumns.HasMask(normalizedTableName, colName);

                if (!isMasked && colRef.Name.Parts.Count > 1)
                {
                    string qualifier = colRef.Name.Parts[^2].Value;
                    isMasked = _options.MaskedColumns.HasMask(qualifier, colName);
                }

                if (!isMasked && _options.TablesWithMaskedColumns != null)
                {
                    foreach (var tbl in _options.TablesWithMaskedColumns)
                    {
                        if (_options.MaskedColumns.HasMask(tbl, colName))
                        {
                            isMasked = true;
                            break;
                        }
                    }
                }

                if (isMasked)
                {
                    throw new SecurityException($"Masked column '{colName}' of table '{normalizedTableName}' must not be referenced in {clause}.");
                }

                if (_options.RejectWholeRowReferencesInDml &&
                    (colName.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                     colName.Equals(simpleTableName, StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrEmpty(targetAlias) && colName.Equals(targetAlias, StringComparison.OrdinalIgnoreCase))) &&
                    HasMaskingForTable(normalizedTableName))
                {
                    throw new SecurityException($"Whole-row reference to '{colName}' in {clause} is forbidden because table '{normalizedTableName}' contains masked columns.");
                }
            }
            else if (node is UsingJoinCondition usingCond)
            {
                foreach (var col in usingCond.Columns)
                {
                    string colName = col.Value;
                    bool isMasked = _options.MaskedColumns.HasMask(normalizedTableName, colName);
                    if (!isMasked && _options.TablesWithMaskedColumns != null)
                    {
                        foreach (var tbl in _options.TablesWithMaskedColumns)
                        {
                            if (_options.MaskedColumns.HasMask(tbl, colName))
                            {
                                isMasked = true;
                                break;
                            }
                        }
                    }
                    if (isMasked)
                    {
                        throw new SecurityException($"Masked column '{colName}' of table '{normalizedTableName}' must not be referenced in {clause}.");
                    }
                }
            }
            else if (node is WildcardSelectItem wildcard)
            {
                bool hasTableMasking = HasMaskingForTable(normalizedTableName);
                if (wildcard.Qualifier != null)
                {
                    string qualSimple = wildcard.Qualifier.SimpleName;
                    string qualNorm = wildcard.Qualifier.NormalizedName;
                    bool matchesTarget = qualSimple.Equals(simpleTableName, StringComparison.OrdinalIgnoreCase) ||
                                         qualNorm.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                                         (!string.IsNullOrEmpty(targetAlias) &&
                                          (qualSimple.Equals(targetAlias, StringComparison.OrdinalIgnoreCase) ||
                                           qualNorm.Equals(targetAlias, StringComparison.OrdinalIgnoreCase)));

                    if ((matchesTarget && hasTableMasking) ||
                        HasMaskingForTable(qualNorm) ||
                        HasMaskingForTable(qualSimple))
                    {
                        throw new SecurityException($"Whole-row reference to '{wildcard.Qualifier}.*' in {clause} is forbidden because table '{normalizedTableName}' contains masked columns.");
                    }
                }
            }

            // Push children
            PushChildren(node, stack);
        }
    }

    private static readonly FrozenSet<string> AggregateFunctionNames = new[]
    {
        "count", "sum", "avg", "min", "max", "every", "any_value", "median", "mode",
        "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "var", "stdev", "stdevp", "varp",
        "corr", "covar_pop", "covar_samp", "regr_slope", "regr_intercept", "regr_r2",
        "bool_and", "bool_or", "bit_and", "bit_or", "bit_xor", "count_if", "max_by", "min_by", "arbitrary",
        "array_agg", "string_agg", "listagg", "group_concat", "json_agg", "jsonb_agg", "json_arrayagg", "json_objectagg",
        "xmlagg", "collect_list", "collect_set", "approx_distinct", "approx_count_distinct", "approx_percentile",
        "percentile_cont", "percentile_disc", "percentile_approx", "first_value_agg", "checksum", "histogram", "map_agg", "multimap_agg"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A masked column must not be an aggregate argument (including FILTER and the ordered-aggregate ORDER BY) or a grouping expression:
    /// the secured derived table would feed mask output into the aggregate (WP-FEAT-02, <c>AGGREGATION_ON_MASKED_COLUMN</c>).
    /// Subqueries are skipped here; their own query specification is visited with its own tables.
    /// </summary>
    private void EnsureNoMaskedColumnsInAggregatesAndGrouping(QuerySpecification node)
    {
        if (!_options.RejectMaskedColumnsInAggregates || node.From == null)
            return;

        var tables = new List<(string TableName, string? Alias)>();
        CollectTableSources(node.From, tables);
        if (tables.Count == 0)
            return;

        if (node.GroupBy != null)
        {
            CheckAggregationScope(tables, node.GroupBy);
        }

        var stack = new Stack<SqlNode>(16);
        foreach (var item in node.Projections) stack.Push(item);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is SelectStatement) continue;
            if (current is FunctionCallExpression { Window: null } fn && AggregateFunctionNames.Contains(fn.Name.SimpleName))
            {
                CheckAggregationScope(tables, fn);
                continue;
            }

            PushChildren(current, stack);
        }
    }

    private void CheckAggregationScope(List<(string TableName, string? Alias)> tables, SqlNode scope)
    {
        foreach (var (tbl, alias) in tables)
        {
            try
            {
                CheckNoMaskedColumnReferences(tbl, alias, scope, "an aggregate or GROUP BY");
            }
            catch (SecurityException ex) when (ex is not MaskedAggregationException)
            {
                throw new MaskedAggregationException();
            }
        }
    }

    /// <summary>
    /// SR-L-24: a bare whole-row reference in a SELECT projection (<c>SELECT t FROM t</c>, <c>to_json(t)</c>) of a table with masked
    /// columns is rejected. The projection rewrite already makes the row the masked one; this is the explicit second line of defence.
    /// </summary>
    private void EnsureNoWholeRowReferencesInProjections(QuerySpecification node)
    {
        if (!_options.RejectWholeRowReferencesInSelect || node.From == null)
            return;

        var tables = new List<(string TableName, string? Alias)>();
        CollectTableSources(node.From, tables);
        if (tables.Count == 0)
            return;

        var stack = new Stack<SqlNode>(16);
        foreach (var item in node.Projections) stack.Push(item);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ColumnReference { Name.Parts.Count: 1 } reference)
            {
                string name = reference.Name.SimpleName;
                foreach (var (tbl, alias) in tables)
                {
                    if (!IsWholeRowName(name, tbl, alias) || !HasMaskingForTable(tbl))
                        continue;

                    var columns = _options.TableColumns(tbl);
                    if (columns != null && columns.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)))
                        continue; // a real column that merely shares the table's name

                    throw new SecurityException("A whole-row reference in a SELECT list is forbidden because the table contains masked columns.");
                }

                continue;
            }

            PushChildren(current, stack);
        }
    }

    private static bool IsWholeRowName(string name, string normalizedTableName, string? alias) =>
        name.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals(SqlIdentifierHelper.GetSimpleName(normalizedTableName), StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(alias) && name.Equals(alias, StringComparison.OrdinalIgnoreCase));

    private static void CollectTableSources(QueryBody? body, List<(string TableName, string? Alias)> tables)
    {
        if (body == null) return;
        switch (body)
        {
            case QuerySpecification qs:
                CollectTableSources(qs.From, tables);
                break;
            case SetOperationQuery so:
                CollectTableSources(so.Left, tables);
                CollectTableSources(so.Right, tables);
                break;
            case TableQueryBody tq:
                tables.Add((tq.TableName.NormalizedName, null));
                break;
        }
    }

    private static void CollectTableSources(TableSource? from, List<(string TableName, string? Alias)> tables)
    {
        if (from == null) return;
        switch (from)
        {
            case NamedTableSource named:
                tables.Add((named.Name.NormalizedName, named.Alias?.Value));
                break;
            case JoinedTableSource joined:
                CollectTableSources(joined.Left, tables);
                CollectTableSources(joined.Right, tables);
                break;
            case LateralTableSource lateral:
                CollectTableSources(lateral.Subquery.Body, tables);
                break;
        }
    }

    private void CheckJoinConditions(TableSource? from, List<(string TableName, string? Alias)> tables)
    {
        if (from is JoinedTableSource j)
        {
            if (j.Condition is OnJoinCondition on)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, on.Predicate, "JOIN condition");
                }
            }
            else if (j.Condition is UsingJoinCondition usingCond)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, usingCond, "JOIN condition");
                }
            }
            CheckJoinConditions(j.Left, tables);
            CheckJoinConditions(j.Right, tables);
        }
    }
}

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

                if (!isMasked && _options.TablesWithMaskedColumns != null && !IsKnownUnmaskedColumnOfTarget(normalizedTableName, targetAlias, colRef))
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
                    if (!isMasked && _options.TablesWithMaskedColumns != null && !IsKnownUnmaskedColumnOfTarget(normalizedTableName, colName))
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

    private bool IsKnownUnmaskedColumnOfTarget(string normalizedTableName, string? targetAlias, ColumnReference colRef)
    {
        if (colRef.Name.Parts.Count > 1)
        {
            string qualifier = colRef.Name.Parts[^2].Value;
            string simpleTable = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
            bool matchesTarget = qualifier.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                                 qualifier.Equals(simpleTable, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrEmpty(targetAlias) && qualifier.Equals(targetAlias, StringComparison.OrdinalIgnoreCase));
            if (!matchesTarget)
                return false;
        }

        return IsKnownUnmaskedColumnOfTarget(normalizedTableName, colRef.Name.SimpleName);
    }

    private bool IsKnownUnmaskedColumnOfTarget(string normalizedTableName, string colName)
    {
        var cols = CachedColumns(normalizedTableName);   // asked per column reference: cached, and no closure per call
        if (cols == null)
            return false;

        for (int i = 0; i < cols.Count; i++)
        {
            if (cols[i].Equals(colName, StringComparison.OrdinalIgnoreCase))
                return !_options.MaskedColumns.HasMask(normalizedTableName, colName);
        }

        return false;
    }

    private void EnsureNoDerivedMaskedColumnReferencesInPredicate(
        List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived,
        SqlNode scope,
        string clause)
    {
        if (!_options.RejectMaskedColumnsInPredicates || derived.Count == 0)
            return;

        CheckNoDerivedMaskedColumnReferences(derived, scope, clause);
    }

    private void CheckNoDerivedMaskedColumnReferences(
        List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived,
        SqlNode scope,
        string clause)
    {
        if (derived.Count == 0)
            return;

        var stack = _maskGuardStack ??= new Stack<SqlNode>(64);
        stack.Clear();
        stack.Push(scope);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference colRef)
            {
                string colName = colRef.Name.SimpleName;
                foreach (var (sourceName, alias, maskedCols) in derived)
                {
                    if (!maskedCols.Contains(colName))
                        continue;

                    if (colRef.Name.Parts.Count > 1)
                    {
                        string qualifier = colRef.Name.Parts[^2].Value;
                        bool matchesQualifier = qualifier.Equals(sourceName, StringComparison.OrdinalIgnoreCase) ||
                                                (!string.IsNullOrEmpty(alias) && qualifier.Equals(alias, StringComparison.OrdinalIgnoreCase));
                        if (!matchesQualifier)
                            continue;
                    }

                    throw new SecurityException($"Masked column '{colName}' of '{sourceName}' must not be referenced in {clause}.");
                }
            }
            else if (node is UsingJoinCondition usingCond)
            {
                foreach (var col in usingCond.Columns)
                {
                    foreach (var (sourceName, _, maskedCols) in derived)
                    {
                        if (maskedCols.Contains(col.Value))
                        {
                            throw new SecurityException($"Masked column '{col.Value}' of '{sourceName}' must not be referenced in {clause}.");
                        }
                    }
                }
            }

            PushChildren(node, stack);
        }
    }

    // Per-compile state of the mask guards. The visitor is single-use and not thread-safe (one instance per compile), so this state is
    // plain instance fields: cleared on entry by the owner, never static, pooled or held by the engine.
    // _specTables/_specDerived: owner PrepareSpecSources; valid from there until the recursion into the children of that specification.
    private readonly List<(string TableName, string? Alias)> _specTables = new();
    private readonly List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> _specDerived = new();
    // _projectionGuardStack: owner EnsureNoMaskedColumnsInAggregatesAndGrouping / EnsureNoWholeRowReferencesInProjections (never nested).
    // No callee of those loops may touch it; the callees use _maskGuardStack.
    private Stack<SqlNode>? _projectionGuardStack;
    private readonly Dictionary<string, bool> _maskingForTable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>?> _tableColumnLists = new(StringComparer.Ordinal);
    // Number of enclosing specifications whose projection guards ran: a nested specification may reference their (masked) columns.
    private int _maskedScopeDepth;

    private void PrepareSpecSources(QuerySpecification node)
    {
        _specTables.Clear();
        _specDerived.Clear();
        if (node.From == null ||
            !(_options.RejectMaskedColumnsInPredicates || _options.RejectMaskedColumnsInAggregates || _options.RejectWholeRowReferencesInSelect))
        {
            return;
        }

        CollectTableSources(node.From, _specTables);
        CollectDerivedMaskedSources(node.From, _specDerived);
    }

    /// <summary>
    /// Runs the aggregate/GROUP BY and whole-row guards for the specification prepared by <see cref="PrepareSpecSources"/>, unless every
    /// condition for skipping them is positively established. Returns true when the guards ran (the scope may carry masked columns).
    /// </summary>
    private bool EnsureProjectionMaskGuards(QuerySpecification node)
    {
        if (node.From == null ||
            !(_options.RejectMaskedColumnsInAggregates || _options.RejectWholeRowReferencesInSelect) ||
            CanSkipProjectionGuards(node))
        {
            return false;
        }

        EnsureNoMaskedColumnsInAggregatesAndGrouping(node);
        EnsureNoWholeRowReferencesInProjections(node);
        return true;
    }

    /// <summary>
    /// Fail closed: skip only when no mask can be reachable through the inputs the guards themselves consult. The caller-supplied set must
    /// be empty, no derived source carries masks, no enclosing specification ran the guards, and the typed provider reports no mask for
    /// any FROM table (nor for its simple name or alias, which the guards probe as column qualifiers). A failing lookup runs the guard.
    /// </summary>
    private bool CanSkipProjectionGuards(QuerySpecification node)
    {
        if (_maskedScopeDepth > 0 || _options.TablesWithMaskedColumns.Count > 0 || _specDerived.Count > 0)
            return false;

        foreach (var (table, alias) in _specTables)
        {
            if (MayHaveMask(table) ||
                MayHaveMask(SqlIdentifierHelper.GetSimpleName(table)) ||
                (!string.IsNullOrEmpty(alias) && MayHaveMask(alias)))
            {
                return false;
            }
        }

        return ReferencesStayWithinFromTables(node);
    }

    /// <summary>
    /// The guards ask the provider per referenced column and qualifier, so skipping is only sound when every column reference in the
    /// guarded clauses (projections and GROUP BY, not subqueries) is a known column of a FROM table, reached through a qualifier that
    /// names a FROM table or alias. Anything else (an unbound or outer qualifier, a column the catalog list does not cover, a table
    /// without a column list, an alias, a wildcard of another source) runs the guard. Uses _projectionGuardStack: the probe finishes
    /// before the guards start, and nothing it calls clears or fills a stack.
    /// </summary>
    private bool ReferencesStayWithinFromTables(QuerySpecification node)
    {
        var stack = _projectionGuardStack ??= new Stack<SqlNode>(64);
        stack.Clear();
        PushAll(node.Projections, stack);
        if (node.GroupBy != null) stack.Push(node.GroupBy);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is SelectStatement) continue;
            if (current is ColumnReference reference)
            {
                if (!IsKnownFromColumn(reference)) return false;
                continue;
            }

            if (current is WildcardSelectItem { Qualifier: { } qualifier })
            {
                bool matches = false;
                foreach (var (table, alias) in _specTables)
                {
                    if (MatchesWildcardQualifier(qualifier, table, alias)) { matches = true; break; }
                }

                if (!matches) return false;
                continue;
            }

            PushChildren(current, stack);
        }

        return true;
    }

    private bool IsKnownFromColumn(ColumnReference reference)
    {
        string name = reference.Name.SimpleName;
        bool qualified = reference.Name.Parts.Count > 1;
        string? qualifier = qualified ? reference.Name.Parts[^2].Value : null;
        foreach (var (table, alias) in _specTables)
        {
            if (qualified && !IsWholeRowName(qualifier!, table, alias)) continue;
            var columns = ColumnsOf(table);
            if (columns == null) continue;
            for (int i = 0; i < columns.Count; i++)
            {
                if (columns[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    /// <summary>The cached column list of a table; a failing callback propagates (and is not cached).</summary>
    private IReadOnlyList<string>? CachedColumns(string table)
    {
        if (_tableColumnLists.TryGetValue(table, out var columns))
            return columns;

        columns = _options.TableColumns(table);
        _tableColumnLists[table] = columns;
        return columns;
    }

    private IReadOnlyList<string>? ColumnsOf(string table)
    {
        try
        {
            return CachedColumns(table);
        }
        catch (Exception)
        {
            return null;   // intentionally fails toward "the guard runs"
        }
    }

    private bool MayHaveMask(string name)
    {
        try
        {
            return HasMaskingForTable(name);
        }
        catch (Exception)
        {
            // Intentional catch-all: any failing lookup is uncertainty, and uncertainty must fail toward "the guard runs"
            // (the guard then meets the same failing lookup itself and throws).
            return true;
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
    /// A masked column must not be an aggregate argument (including FILTER, ordered-aggregate ORDER BY, and window functions) or a
    /// grouping expression: the secured derived table would feed mask output into the aggregate (WP-FEAT-02, <c>AGGREGATION_ON_MASKED_COLUMN</c>).
    /// Subqueries are skipped here; their own query specification is visited with its own tables.
    /// </summary>
    private void EnsureNoMaskedColumnsInAggregatesAndGrouping(QuerySpecification node)
    {
        if (!_options.RejectMaskedColumnsInAggregates || node.From == null)
            return;

        var tables = _specTables;
        var derived = _specDerived;
        if (tables.Count == 0 && derived.Count == 0)
            return;

        if (node.GroupBy != null)
        {
            CheckAggregationScope(tables, derived, node.GroupBy);
        }

        // The loop below is live while CheckAggregationScope runs, and that call clears _maskGuardStack: this loop must keep its own stack.
        var stack = _projectionGuardStack ??= new Stack<SqlNode>(64);
        stack.Clear();
        PushAll(node.Projections, stack);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is SelectStatement) continue;
            if (current is FunctionCallExpression fn && (fn.Window != null || AggregateFunctionNames.Contains(fn.Name.SimpleName)))
            {
                CheckAggregationScope(tables, derived, fn);
                continue;
            }

            PushChildren(current, stack);
        }
    }

    private void CheckAggregationScope(
        List<(string TableName, string? Alias)> tables,
        List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived,
        SqlNode scope)
    {
        try
        {
            foreach (var (tbl, alias) in tables)
            {
                CheckNoMaskedColumnReferences(tbl, alias, scope, "an aggregate or GROUP BY");
            }

            CheckNoDerivedMaskedColumnReferences(derived, scope, "an aggregate or GROUP BY");
        }
        catch (SecurityException ex) when (ex is not MaskedAggregationException)
        {
            throw new MaskedAggregationException();
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

        var tables = _specTables;
        if (tables.Count == 0)
            return;

        // Shares _projectionGuardStack with the aggregate guard: the two never nest (this loop calls no method that touches a stack).
        var stack = _projectionGuardStack ??= new Stack<SqlNode>(64);
        stack.Clear();
        PushAll(node.Projections, stack);
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

    private void CollectDerivedMaskedSources(QueryBody? body, List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived)
    {
        if (body == null) return;
        switch (body)
        {
            case QuerySpecification qs:
                CollectDerivedMaskedSources(qs.From, derived);
                break;
            case SetOperationQuery so:
                CollectDerivedMaskedSources(so.Left, derived);
                CollectDerivedMaskedSources(so.Right, derived);
                break;
            case TableQueryBody tq:
                if (IsCte(tq.TableName))
                {
                    string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(tq.TableName.Parts[0]);
                    if (_cteMaskedColumns.Peek().TryGetValue(cteKey, out var cols) && cols.Count > 0)
                    {
                        derived.Add((tq.TableName.NormalizedName, null, cols));
                    }
                }
                break;
        }
    }

    private void CollectDerivedMaskedSources(TableSource? from, List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived)
    {
        if (from == null) return;
        switch (from)
        {
            case NamedTableSource named when IsCte(named.Name):
            {
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(named.Name.Parts[0]);
                if (_cteMaskedColumns.Peek().TryGetValue(cteKey, out var cols) && cols.Count > 0)
                {
                    derived.Add((named.Name.NormalizedName, named.Alias?.Value, cols));
                }
                break;
            }
            case JoinedTableSource joined:
                CollectDerivedMaskedSources(joined.Left, derived);
                CollectDerivedMaskedSources(joined.Right, derived);
                break;
            case SubqueryTableSource subquery:
            {
                var cols = CollectMaskedOutputColumns(subquery.Subquery, subquery.ColumnAliases);
                if (cols.Count > 0)
                {
                    string alias = subquery.Alias?.Value ?? "<subquery>";
                    derived.Add((alias, subquery.Alias?.Value, cols));
                }
                break;
            }
            case LateralTableSource lateral:
            {
                var cols = CollectMaskedOutputColumns(lateral.Subquery, lateral.ColumnAliases);
                if (cols.Count > 0)
                {
                    string alias = lateral.Alias?.Value ?? "<lateral>";
                    derived.Add((alias, lateral.Alias?.Value, cols));
                }
                break;
            }
        }
    }

    private HashSet<string> CollectMaskedOutputColumns(SelectStatement query, IReadOnlyList<SqlIdentifier>? columnAliases = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool pushedWith = false;
        if (query.With != null)
        {
            _cteMaskedColumns.Push(new Dictionary<string, HashSet<string>>(_cteMaskedColumns.Peek(), CteScopeComparer));
            _cteScopeStack.Push(new HashSet<string>(_cteScopeStack.Peek(), CteScopeComparer));
            pushedWith = true;
            foreach (var cte in query.With.Ctes)
            {
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
                HashSet<string> cteMasked;
                if (query.With.IsRecursive)
                {
                    _cteScopeStack.Peek().Add(cteKey);
                    cteMasked = CollectRecursiveCteMaskedColumns(cte, cteKey);
                }
                else
                {
                    cteMasked = CollectMaskedOutputColumns(cte.Query, cte.ColumnAliases);
                    _cteScopeStack.Peek().Add(cteKey);
                }

                if (cteMasked.Count > 0)
                {
                    _cteMaskedColumns.Peek()[cteKey] = cteMasked;
                }
            }
        }

        try
        {
            CollectMaskedOutputColumnsFromBody(query.Body, columnAliases, result);
        }
        finally
        {
            if (pushedWith)
            {
                _cteScopeStack.Pop();
                _cteMaskedColumns.Pop();
            }
        }

        return result;
    }

    private void CollectMaskedOutputColumnsFromBody(
        QueryBody? body,
        IReadOnlyList<SqlIdentifier>? columnAliases,
        HashSet<string> result)
    {
        if (body == null) return;
        switch (body)
        {
            case TableQueryBody tq:
            {
                if (IsCte(tq.TableName))
                {
                    string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(tq.TableName.Parts[0]);
                    if (_cteMaskedColumns.Peek().TryGetValue(cteKey, out var cols))
                    {
                        foreach (var c in cols) result.Add(c);
                    }
                }
                else
                {
                    AddTableMaskedColumns(tq.TableName.NormalizedName, columnAliases, result);
                }
                break;
            }
            case SetOperationQuery so:
            {
                CollectMaskedOutputColumnsFromBody(so.Left, columnAliases, result);
                CollectMaskedOutputColumnsFromBody(so.Right, columnAliases, result);
                break;
            }
            case QuerySpecification qs:
            {
                if (qs.From == null) break;
                var tables = new List<(string TableName, string? Alias)>();
                CollectTableSources(qs.From, tables);
                var derived = new List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)>();
                CollectDerivedMaskedSources(qs.From, derived);
                if (tables.Count == 0 && derived.Count == 0) break;

                for (int i = 0; i < qs.Projections.Count; i++)
                {
                    var proj = qs.Projections[i];
                    if (proj is WildcardSelectItem wc)
                    {
                        foreach (var (tbl, alias) in tables)
                        {
                            if (MatchesWildcardQualifier(wc.Qualifier, tbl, alias))
                            {
                                AddTableMaskedColumns(tbl, null, result);
                            }
                        }
                        foreach (var (src, alias, maskedCols) in derived)
                        {
                            if (MatchesWildcardQualifier(wc.Qualifier, src, alias))
                            {
                                foreach (var c in maskedCols) result.Add(c);
                            }
                        }
                    }
                    else if (proj is ColumnSelectItem csi && ExpressionReferencesMaskedColumn(csi.Expression, tables, derived))
                    {
                        string? outName = csi.Alias?.Value ?? (csi.Expression is ColumnReference cr ? cr.Name.SimpleName : null);
                        if (!string.IsNullOrEmpty(outName))
                        {
                            result.Add(outName);
                        }
                        if (columnAliases != null && i < columnAliases.Count)
                        {
                            result.Add(columnAliases[i].Value);
                        }
                    }
                }
                break;
            }
        }
    }

    private void AddTableMaskedColumns(string normalizedTableName, IReadOnlyList<SqlIdentifier>? columnAliases, HashSet<string> result)
    {
        var cols = _options.TableColumns(normalizedTableName);
        if (cols == null) return;
        for (int i = 0; i < cols.Count; i++)
        {
            if (_options.MaskedColumns.HasMask(normalizedTableName, cols[i]))
            {
                result.Add(cols[i]);
                if (columnAliases != null && i < columnAliases.Count)
                {
                    result.Add(columnAliases[i].Value);
                }
            }
        }
    }

    private static bool MatchesWildcardQualifier(SqlQualifiedName? qualifier, string sourceName, string? alias)
    {
        if (qualifier == null) return true;
        string qualSimple = qualifier.SimpleName;
        string qualNorm = qualifier.NormalizedName;
        string simpleSource = SqlIdentifierHelper.GetSimpleName(sourceName);
        return qualSimple.Equals(simpleSource, StringComparison.OrdinalIgnoreCase) ||
               qualNorm.Equals(sourceName, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(alias) &&
                (qualSimple.Equals(alias, StringComparison.OrdinalIgnoreCase) ||
                 qualNorm.Equals(alias, StringComparison.OrdinalIgnoreCase)));
    }

    private bool ExpressionReferencesMaskedColumn(
        SqlNode expr,
        List<(string TableName, string? Alias)> tables,
        List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived)
    {
        var stack = new Stack<SqlNode>(16);
        stack.Push(expr);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is MaskExpression)
                return true;

            if (current is ColumnReference colRef)
            {
                string colName = colRef.Name.SimpleName;
                foreach (var (tbl, alias) in tables)
                {
                    if (_options.MaskedColumns.HasMask(tbl, colName))
                        return true;
                    if (colRef.Name.Parts.Count > 1 && _options.MaskedColumns.HasMask(colRef.Name.Parts[^2].Value, colName))
                        return true;
                    if (_options.TablesWithMaskedColumns != null && !IsKnownUnmaskedColumnOfTarget(tbl, alias, colRef))
                    {
                        foreach (var maskedTbl in _options.TablesWithMaskedColumns)
                        {
                            if (_options.MaskedColumns.HasMask(maskedTbl, colName))
                                return true;
                        }
                    }
                }

                foreach (var (src, alias, maskedCols) in derived)
                {
                    if (!maskedCols.Contains(colName))
                        continue;
                    if (colRef.Name.Parts.Count > 1)
                    {
                        string qualifier = colRef.Name.Parts[^2].Value;
                        if (!qualifier.Equals(src, StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrEmpty(alias) || !qualifier.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }
                    }
                    return true;
                }
            }

            PushChildren(current, stack);
        }

        return false;
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

    private void CheckDerivedJoinConditions(
        TableSource? from,
        List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)> derived)
    {
        if (derived.Count == 0) return;
        if (from is JoinedTableSource j)
        {
            if (j.Condition is OnJoinCondition on)
            {
                EnsureNoDerivedMaskedColumnReferencesInPredicate(derived, on.Predicate, "JOIN condition");
            }
            else if (j.Condition is UsingJoinCondition usingCond)
            {
                EnsureNoDerivedMaskedColumnReferencesInPredicate(derived, usingCond, "JOIN condition");
            }
            CheckDerivedJoinConditions(j.Left, derived);
            CheckDerivedJoinConditions(j.Right, derived);
        }
    }
}

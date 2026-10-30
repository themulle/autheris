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
}

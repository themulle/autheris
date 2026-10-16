namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Globalization;
using TrinoSqlEngine.Ast.Nodes;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    public override SqlNode VisitSetOperationQuery(SetOperationQuery node)
    {
        var left = (QueryBody)WithScope(SecurityScope.SetOperationBranch, () => Visit(node.Left));
        var right = (QueryBody)WithScope(SecurityScope.SetOperationBranch, () => Visit(node.Right));
        return ReferenceEquals(left, node.Left) && ReferenceEquals(right, node.Right) ? node : node with { Left = left, Right = right };
    }

    public override SqlNode VisitLateralTableSource(LateralTableSource node) =>
        WithScope(SecurityScope.Lateral, () => base.VisitLateralTableSource(node));

    public override SqlNode VisitSubqueryTableSource(SubqueryTableSource node) =>
        WithScope(SecurityScope.Subquery, () => base.VisitSubqueryTableSource(node));

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        bool isRootRead = _subqueryDepth == 0;
        _subqueryDepth++;

        WithClause? with = null;
        if (node.With != null)
        {
            _cteScopeStack.Push(new HashSet<string>(_cteScopeStack.Peek(), CteScopeComparer));
            _cteMaskedColumns.Push(new Dictionary<string, HashSet<string>>(_cteMaskedColumns.Peek(), CteScopeComparer));
            var ctes = new List<CommonTableExpression>();
            foreach (var cte in node.With.Ctes)
            {
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
                EnsureCteNameIsUnambiguous(cteKey);
                var cteMaskedCols = CollectMaskedOutputColumns(cte.Query, cte.ColumnAliases);
                if (node.With.IsRecursive)
                {
                    // CR-ADG-29: a recursive CTE sees its own name in its body (self-reference); the base tables of the anchor
                    // and recursive members are still secured. A name that is also a catalog table is ambiguous and rejected.
                    EnsureRecursiveCteIsNotACatalogTable(cte.Name);
                    _cteScopeStack.Peek().Add(cteKey);
                    if (cteMaskedCols.Count > 0)
                        _cteMaskedColumns.Peek()[cteKey] = cteMaskedCols;
                }

                // Non-recursive: visit the CTE body BEFORE adding the name to the scope (Exit-timing, SEC-CTE & SEC C-02)
                if (!node.With.IsRecursive) _definingNonRecursiveCtes.Add(cteKey);
                SelectStatement cteQuery;
                try
                {
                    cteQuery = (SelectStatement)WithScope(SecurityScope.CteBody, () => Visit(cte.Query));
                }
                finally
                {
                    if (!node.With.IsRecursive) _definingNonRecursiveCtes.RemoveAt(_definingNonRecursiveCtes.Count - 1);
                }
                _cteScopeStack.Peek().Add(cteKey);
                if (cteMaskedCols.Count > 0)
                    _cteMaskedColumns.Peek()[cteKey] = cteMaskedCols;

                // CR-ADG-01 / INV-11: on the typed path the definition is emitted as the delimited scope key, so the gateway's
                // CTE decision and the database's name binding cannot diverge (Oracle upper-cases unquoted names, SQL Server
                // keeps the user spelling and may be case-sensitive). References are rewritten to the same identifier.
                ctes.Add(cte with { Name = new SqlIdentifier(cteKey, IsQuoted: true), Query = cteQuery });
            }
            with = node.With with { Ctes = ctes.AsReadOnly() };
        }

        // Fail-Closed Predicate Guard: Check ORDER BY for masked columns
        if (node.OrderBy != null && _options.RejectMaskedColumnsInPredicates)
        {
            var tables = new List<(string TableName, string? Alias)>();
            CollectTableSources(node.Body, tables);
            foreach (var (tbl, alias) in tables)
            {
                EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.OrderBy, "ORDER BY");
            }
            var derived = new List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)>();
            CollectDerivedMaskedSources(node.Body, derived);
            EnsureNoDerivedMaskedColumnReferencesInPredicate(derived, node.OrderBy, "ORDER BY");
        }

        var body = (QueryBody)Visit(node.Body);
        var orderBy = node.OrderBy != null ? (OrderByClause)Visit(node.OrderBy) : null;
        var pagination = node.Pagination != null ? (PaginationClause)Visit(node.Pagination) : null;

        if (isRootRead && _options.EnforcedMaxRows > 0 && !_rootLimitHandled)
        {
            _rootLimitHandled = true;
            if (pagination != null)
            {
                var clampedLimit = ClampLimit(pagination.Limit, _options.EnforcedMaxRows);
                // M-22: WITH TIES can return more rows than the limit, so it is downgraded to ONLY under an enforced cap.
                pagination = pagination with { Limit = clampedLimit, WithTies = false };
            }
            else
            {
                pagination = new PaginationClause(null, new LiteralExpression(_options.EnforcedMaxRows, LiteralType.Integer));
            }
        }

        if (node.With != null)
        {
            _cteScopeStack.Pop();
            _cteMaskedColumns.Pop();
        }

        _subqueryDepth--;

        if (with == node.With && body == node.Body && orderBy == node.OrderBy && pagination == node.Pagination)
            return node;

        return node with { With = with, Body = body, OrderBy = orderBy, Pagination = pagination };
    }

    public override SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        // The table and derived-source lists are computed once per visit and consumed by every guard below, before the recursion into
        // the children: nested specifications refill the same fields, so nothing may read them after base.VisitQuerySpecification.
        PrepareSpecSources(node);

        // Fail-Closed Predicate Guard: Check WHERE, HAVING, and JOIN conditions for masked columns
        if (_options.RejectMaskedColumnsInPredicates && node.From != null)
        {
            var tables = _specTables;
            var derived = _specDerived;

            if (node.Where != null)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.Where, "WHERE");
                }
                EnsureNoDerivedMaskedColumnReferencesInPredicate(derived, node.Where, "WHERE");
            }

            if (node.Having != null)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.Having, "HAVING");
                }
                EnsureNoDerivedMaskedColumnReferencesInPredicate(derived, node.Having, "HAVING");
            }

            CheckJoinConditions(node.From, tables);
            CheckDerivedJoinConditions(node.From, derived);
        }

        // The sources are not read after this call: base.VisitQuerySpecification re-enters this method for nested specifications.
        bool maskedScope = EnsureProjectionMaskGuards(node);
        if (!maskedScope)
            return base.VisitQuerySpecification(node);

        _maskedScopeDepth++;
        try
        {
            return base.VisitQuerySpecification(node);
        }
        finally
        {
            _maskedScopeDepth--;
        }
    }

    private static Expression ClampLimit(Expression? existingLimit, long maxRows)
    {
        if (existingLimit is LiteralExpression lit && lit.Value != null)
        {
            if (long.TryParse(lit.Value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long existingVal))
            {
                if (existingVal <= maxRows) return existingLimit;
            }
        }
        return new LiteralExpression(maxRows, LiteralType.Integer);
    }
}

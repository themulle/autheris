namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Security;
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
            // Issue #277: the opt-in gate is the first check of a recursive WITH, before the ambiguity and catalog-name checks.
            if (node.With.IsRecursive && !_options.AllowRecursiveCte)
            {
                throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "WITH RECURSIVE");
            }

            _cteScopeStack.Push(new HashSet<string>(_cteScopeStack.Peek(), CteScopeComparer));
            _cteMaskedColumns.Push(new Dictionary<string, HashSet<string>>(_cteMaskedColumns.Peek(), CteScopeComparer));
            _cteMasksStack.Push(new Dictionary<string, ImmutableArray<bool>>(_cteMasksStack.Peek(), CteScopeComparer));
            var ctes = new List<CommonTableExpression>();
            foreach (var cte in node.With.Ctes)
            {
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
                EnsureCteNameIsUnambiguous(cteKey);
                HashSet<string> cteMaskedCols;
                if (node.With.IsRecursive)
                {
                    // CR-ADG-29: a recursive CTE sees its own name in its body (self-reference); the base tables of the anchor
                    // and recursive members are still secured. A name that is also a catalog table is ambiguous and rejected.
                    EnsureRecursiveCteIsNotACatalogTable(cte.Name);
                    _cteScopeStack.Peek().Add(cteKey);
                    // computed with the name in scope, over every member, by output position.
                    cteMaskedCols = CollectRecursiveCteMaskedColumns(cte, cteKey);
                    if (cteMaskedCols.Count > 0)
                        _cteMaskedColumns.Peek()[cteKey] = cteMaskedCols;
                }
                else
                {
                    cteMaskedCols = CollectMaskedOutputColumns(cte.Query, cte.ColumnAliases);
                }

                // Non-recursive: visit the CTE body BEFORE adding the name to the scope (Exit-timing, & SEC C-02)
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
                _cteMasksStack.Peek()[cteKey] = ComputeRootOutputColumnMasks(cteQuery.Body);

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
            try
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
            catch (SecurityException ex) when (ex.Message.StartsWith("Masked column '", StringComparison.Ordinal))
            {
                ex.Data["FILTER_ON_MASKED_COLUMN"] = true;
                throw;
            }
        }

        var body = (QueryBody)Visit(node.Body);
        var orderBy = node.OrderBy != null ? (OrderByClause)Visit(node.OrderBy) : null;
        var pagination = node.Pagination != null ? (PaginationClause)Visit(node.Pagination) : null;

        if (isRootRead && _options.EnforcedMaxRows > 0 && !_rootLimitHandled)
        {
            _rootLimitHandled = true;
            if (_options.BindPageOffset)
            {
                pagination = new PaginationClause(
                    Offset: new PageOffsetParameterExpression(),
                    Limit: new LiteralExpression(_options.EnforcedMaxRows, LiteralType.Integer));
            }
            else if (pagination != null)
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

        if (isRootRead)
        {
            OutputColumnMasks = ComputeRootOutputColumnMasks(body);
        }

        if (node.With != null)
        {
            _cteScopeStack.Pop();
            _cteMaskedColumns.Pop();
            _cteMasksStack.Pop();
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
        EnsureRequiredFilterColumnsSatisfied(node);

        // Fail-Closed Predicate Guard: Check WHERE, HAVING, and JOIN conditions for masked columns
        if (_options.RejectMaskedColumnsInPredicates && node.From != null)
        {
            try
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
            catch (SecurityException ex) when (ex.Message.StartsWith("Masked column '", StringComparison.Ordinal))
            {
                ex.Data["FILTER_ON_MASKED_COLUMN"] = true;
                throw;
            }
        }

        // The sources are not read after this call: base.VisitQuerySpecification re-enters this method for nested specifications.
        bool maskedScope = EnsureProjectionMaskGuards(node);

        // after the mask guards, so a masked or unknown column keeps its own answer and no type is disclosed.
        EnsureAggregateTypesValid(node);
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

    private ImmutableArray<bool> ComputeRootOutputColumnMasks(QueryBody? body)
    {
        if (body == null)
            return ImmutableArray<bool>.Empty;

        switch (body)
        {
            case SetOperationQuery so:
            {
                var left = ComputeRootOutputColumnMasks(so.Left);
                var right = ComputeRootOutputColumnMasks(so.Right);
                int len = Math.Max(left.Length, right.Length);
                var builder = ImmutableArray.CreateBuilder<bool>(len);
                for (int i = 0; i < len; i++)
                {
                    bool l = i < left.Length ? left[i] : true;
                    bool r = i < right.Length ? right[i] : true;
                    builder.Add(l || r);
                }
                return builder.MoveToImmutable();
            }
            case TableQueryBody tq:
            {
                if (IsCte(tq.TableName))
                {
                    string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(tq.TableName.Parts[0]);
                    if (_cteMasksStack.Count > 0 && _cteMasksStack.Peek().TryGetValue(cteKey, out var cteMasks))
                        return cteMasks;
                    return ImmutableArray.Create(true);
                }
                var cols = ColumnsOf(tq.TableName.NormalizedName);
                if (cols != null)
                {
                    var b = ImmutableArray.CreateBuilder<bool>(cols.Count);
                    foreach (var col in cols)
                        b.Add(_options.MaskedColumns.HasMask(tq.TableName.NormalizedName, col));
                    return b.MoveToImmutable();
                }
                return ImmutableArray.Create(true);
            }
            case QuerySpecification qs:
            {
                var builder = ImmutableArray.CreateBuilder<bool>();
                var tables = new List<(string TableName, string? Alias)>();
                CollectTableSources(qs.From, tables);
                var derived = new List<(string SourceName, string? Alias, HashSet<string> MaskedColumns)>();
                CollectDerivedMaskedSources(qs.From, derived);

                foreach (var proj in qs.Projections)
                {
                    if (proj is WildcardSelectItem wc)
                    {
                        ExpandWildcardMasks(qs.From, wc.Qualifier, builder);
                    }
                    else if (proj is ColumnSelectItem csi)
                    {
                        builder.Add(ExpressionReferencesMaskedColumn(csi.Expression, tables, derived));
                    }
                    else
                    {
                        builder.Add(true);
                    }
                }
                return builder.ToImmutable();
            }
            default:
                return ImmutableArray<bool>.Empty;
        }
    }

    private void ExpandWildcardMasks(TableSource? from, SqlQualifiedName? qualifier, ImmutableArray<bool>.Builder builder)
    {
        if (from == null) return;
        switch (from)
        {
            case JoinedTableSource joined:
                ExpandWildcardMasks(joined.Left, qualifier, builder);
                ExpandWildcardMasks(joined.Right, qualifier, builder);
                break;
            case NamedTableSource named:
                if (MatchesWildcardQualifier(qualifier, named.Name.NormalizedName, named.Alias?.Value))
                {
                    if (IsCte(named.Name))
                    {
                        string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(named.Name.Parts[0]);
                        if (_cteMasksStack.Count > 0 && _cteMasksStack.Peek().TryGetValue(cteKey, out var cteMasks))
                        {
                            builder.AddRange(cteMasks);
                        }
                        else
                        {
                            builder.Add(true);
                        }
                    }
                    else
                    {
                        var cols = ColumnsOf(named.Name.NormalizedName);
                        if (cols != null)
                        {
                            foreach (var col in cols)
                            {
                                builder.Add(_options.MaskedColumns.HasMask(named.Name.NormalizedName, col));
                            }
                        }
                        else
                        {
                            builder.Add(true);
                        }
                    }
                }
                break;
            case SubqueryTableSource subquery:
                string alias = subquery.Alias?.Value ?? "<subquery>";
                if (MatchesWildcardQualifier(qualifier, alias, subquery.Alias?.Value))
                {
                    var subMasks = ComputeRootOutputColumnMasks(subquery.Subquery.Body);
                    builder.AddRange(subMasks);
                }
                break;
            case LateralTableSource lateral:
                string latAlias = lateral.Alias?.Value ?? "<lateral>";
                if (MatchesWildcardQualifier(qualifier, latAlias, lateral.Alias?.Value))
                {
                    var latMasks = ComputeRootOutputColumnMasks(lateral.Subquery.Body);
                    builder.AddRange(latMasks);
                }
                break;
        }
    }
}

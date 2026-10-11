namespace TrinoSqlEngine.Ast.Visitors;

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

/// <summary>
/// Core security and governance visitor implementing:
/// - Row-Level Security (RLS) injection (subquery encapsulation and direct WHERE conjunction)
/// - Reserved alias binding (RowFilterAliases.Target) for correlated row filters
/// - Lexical CTE scope tracking (exit-timing and isolation)
/// - Column masking expansion and delimited quoting guarantees (SEC M-24)
/// - Consent-filtered table write protection (SQ-07) and row_scope_key preservation
/// - DML guardrails: anti-tautology filter checks (RejectUnfilteredDml) and WITH CHECK OPTION verification
/// </summary>
public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private readonly RlsOptions _options;
    private readonly ISqlEngine _engine;
    private readonly Stack<HashSet<string>> _cteScopeStack = new();
    // CR-ADG-32: names of non-recursive CTEs whose body is being visited; a reference to one of them (not shadowed) is a self-reference.
    private readonly List<string> _definingNonRecursiveCtes = new();
    private readonly TypedPolicyContext? _typed;
    private readonly Stack<SecurityScope> _securityScopes = new();
    private int _subqueryDepth = 0;
    private bool _rootLimitHandled = false;

    /// <param name="typedPolicy">
    /// When set, row-level security comes from typed providers: injected predicates are wrapped in
    /// <see cref="SecurityPredicateExpression"/>, physical names come from the catalog, and values are parameters. The legacy
    /// string providers of <paramref name="options"/> are not consulted for tables.
    /// </param>
    public AstSecurityVisitor(RlsOptions? options = null, ISqlEngine? engine = null, TypedPolicyContext? typedPolicy = null)
    {
        _options = options ?? new RlsOptions();
        _engine = engine ?? new FastSqlEngine();
        _typed = typedPolicy;
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
    }

    private SecurityScope CurrentScope =>
        _securityScopes.Count > 0 ? _securityScopes.Peek() : (_subqueryDepth <= 1 ? SecurityScope.Root : SecurityScope.Subquery);

    private SqlNode WithScope(SecurityScope scope, Func<SqlNode> visit)
    {
        if (_typed is null) return visit();
        _securityScopes.Push(scope);
        try
        {
            return visit();
        }
        finally
        {
            _securityScopes.Pop();
        }
    }

    public override SqlNode VisitSetOperationQuery(SetOperationQuery node)
    {
        if (_typed is null) return base.VisitSetOperationQuery(node);
        var left = (QueryBody)WithScope(SecurityScope.SetOperationBranch, () => Visit(node.Left));
        var right = (QueryBody)WithScope(SecurityScope.SetOperationBranch, () => Visit(node.Right));
        return ReferenceEquals(left, node.Left) && ReferenceEquals(right, node.Right) ? node : node with { Left = left, Right = right };
    }

    public override SqlNode VisitExistsExpression(ExistsExpression node) =>
        WithScope(SecurityScope.ExistsSubquery, () => base.VisitExistsExpression(node));

    public override SqlNode VisitInSubqueryExpression(InSubqueryExpression node) =>
        WithScope(SecurityScope.InSubquery, () => base.VisitInSubqueryExpression(node));

    public override SqlNode VisitScalarSubqueryExpression(ScalarSubqueryExpression node) =>
        WithScope(SecurityScope.ScalarSubquery, () => base.VisitScalarSubqueryExpression(node));

    public override SqlNode VisitLateralTableSource(LateralTableSource node) =>
        WithScope(SecurityScope.Lateral, () => base.VisitLateralTableSource(node));

    public override SqlNode VisitSubqueryTableSource(SubqueryTableSource node) =>
        WithScope(SecurityScope.Subquery, () => base.VisitSubqueryTableSource(node));

    /// <summary>
    /// A policy filter as an expression: verbatim and parenthesized when it is gateway-rendered target-dialect SQL
    /// (<see cref="RlsOptions.PolicyFiltersAreTargetDialectSql"/>), otherwise parsed as Trino SQL.
    /// </summary>
    private Expression PolicyFilterExpression(string filterSql) =>
        _options.PolicyFiltersAreTargetDialectSql
            ? new TrustedSqlExpression($"({filterSql})")
            : ParseFilterExpression(filterSql);

    private Expression ParseFilterExpression(string filterSql)
    {
        var tokenOptions = SqlTokenSecurityOptions.FromRlsOptions(_options);
        var (tree, _) = _engine.ParseExpression(filterSql.AsMemory(), tokenOptions);
        var builderOptions = AstBuilderOptions.FromRlsOptions(_options) with { EnforceReadOnlyQueries = false };
        var builder = new SqlAstBuilder(builderOptions);
        return builder.BuildStandaloneExpression(tree);
    }

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        bool isRootRead = _subqueryDepth == 0;
        _subqueryDepth++;

        WithClause? with = null;
        if (node.With != null)
        {
            _cteScopeStack.Push(new HashSet<string>(_cteScopeStack.Peek(), StringComparer.Ordinal));
            var ctes = new List<CommonTableExpression>();
            foreach (var cte in node.With.Ctes)
            {
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
                EnsureCteNameIsUnambiguous(cteKey);
                if (node.With.IsRecursive)
                {
                    // CR-ADG-29: a recursive CTE sees its own name in its body (self-reference); the base tables of the anchor
                    // and recursive members are still secured. A name that is also a catalog table is ambiguous and rejected.
                    EnsureRecursiveCteIsNotACatalogTable(cte.Name, cteKey);
                    _cteScopeStack.Peek().Add(cteKey);
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

                // CR-ADG-01 / INV-11: on the typed path the definition is emitted as the delimited scope key, so the gateway's
                // CTE decision and the database's name binding cannot diverge (Oracle upper-cases unquoted names, SQL Server
                // keeps the user spelling and may be case-sensitive). References are rewritten to the same identifier.
                ctes.Add(_typed != null
                    ? cte with { Name = new SqlIdentifier(cteKey, IsQuoted: true), Query = cteQuery }
                    : cte with { Query = cteQuery });
            }
            with = node.With with { Ctes = ctes.AsReadOnly() };
        }

        // Fail-Closed Predicate Guard: Check ORDER BY for masked columns
        if (node.OrderBy != null && _options.RejectMaskedColumnsInPredicates && _options.ColumnMaskingProvider != null)
        {
            var tables = new List<(string TableName, string? Alias)>();
            CollectTableSources(node.Body, tables);
            foreach (var (tbl, alias) in tables)
            {
                EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.OrderBy, "ORDER BY");
            }
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
                pagination = pagination with { Limit = clampedLimit };
            }
            else
            {
                pagination = new PaginationClause(null, new LiteralExpression(_options.EnforcedMaxRows, LiteralType.Integer));
            }
        }

        if (node.With != null)
        {
            _cteScopeStack.Pop();
        }

        _subqueryDepth--;

        if (with == node.With && body == node.Body && orderBy == node.OrderBy && pagination == node.Pagination)
            return node;

        return node with { With = with, Body = body, OrderBy = orderBy, Pagination = pagination };
    }

    public override SqlNode VisitQuerySpecification(QuerySpecification node)
    {
        // Fail-Closed Predicate Guard: Check WHERE, HAVING, and JOIN conditions for masked columns
        if (_options.RejectMaskedColumnsInPredicates && _options.ColumnMaskingProvider != null && node.From != null)
        {
            var tables = new List<(string TableName, string? Alias)>();
            CollectTableSources(node.From, tables);

            if (node.Where != null)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.Where, "WHERE");
                }
            }

            if (node.Having != null)
            {
                foreach (var (tbl, alias) in tables)
                {
                    EnsureNoMaskedColumnReferencesInPredicate(tbl, alias, node.Having, "HAVING");
                }
            }

            CheckJoinConditions(node.From, tables);
        }

        return base.VisitQuerySpecification(node);
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


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
public sealed class AstSecurityVisitor : SqlAstRewriter
{
    private readonly RlsOptions _options;
    private readonly ISqlEngine _engine;
    private readonly Stack<HashSet<string>> _cteScopeStack = new();
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
                // Visit CTE body BEFORE adding CTE name to current scope (Exit-timing, SEC-CTE & SEC C-02)
                var cteQuery = (SelectStatement)WithScope(SecurityScope.CteBody, () => Visit(cte.Query));
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
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

    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        string normalizedName = node.Name.NormalizedName;
        string simpleName = node.Name.SimpleName;
        string scopeKey = SqlIdentifierHelper.FoldIdentifierForScope(node.Name.Parts[^1]);

        // SEC C-02: Only simple (unqualified) names in CTE scope are considered CTEs
        if (node.Name.IsSimple && _cteScopeStack.Peek().Contains(scopeKey))
        {
            if (_typed == null)
            {
                return node;
            }

            // CR-ADG-01: the reference is emitted from the definition identifier (delimited scope key). The user spelling
            // survives as the alias so that qualified column references keep binding under the dialect's own folding.
            return node with
            {
                Name = new SqlQualifiedName(new[] { new SqlIdentifier(scopeKey, IsQuoted: true) }),
                Alias = node.Alias ?? node.Name.Parts[0]
            };
        }

        if (_typed != null)
        {
            return SecureTyped(node);
        }

        bool shouldApplyRls = _options.PolicyProvider.ShouldApplyPolicy(normalizedName);
        bool hasMasking = HasMaskingForTable(normalizedName);
        bool enforceCatalog = _options.EnforceCatalogProjection && _options.TableColumnsProvider != null && _options.TableColumnsProvider(normalizedName) is { Count: > 0 };

        if (!shouldApplyRls && !hasMasking && !enforceCatalog)
        {
            return node;
        }

        return CreateSecuredSubqueryTableSource(node, normalizedName, shouldApplyRls, hasMasking);
    }

    /// <summary>
    /// Typed injection (WP-A5): the table becomes <c>(SELECT cataloged columns FROM [schema].[table] WHERE tenant AND policy)</c>.
    /// The tenant predicate (binary-exact) always applies when the catalog declares a tenant column; the policy predicate
    /// applies when the provider says so. Both are opaque <see cref="SecurityPredicateExpression"/> nodes.
    /// </summary>
    private SubqueryTableSource SecureTyped(NamedTableSource node)
    {
        var typed = _typed!;
        var entry = typed.Catalog.Resolve(node.Name)
            ?? throw new SecurityException("A table reference could not be resolved against the catalog.");
        var tid = entry.Identity;
        var scope = CurrentScope;
        typed.RecordDependency(node.Name, entry);

        var conjuncts = new List<Expression>(2);
        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant != null)
        {
            var id = new SecurityPredicateId(tid.ToString(), 0);
            conjuncts.Add(new SecurityPredicateExpression(tenant, id, scope));
            typed.RecordApplied(id);
        }

        bool applies = typed.RowFilters.ShouldApplyPolicy(tid);
        bool referencesTarget = false;
        string fingerprint = "-";
        if (applies)
        {
            var predicate = typed.RowFilters.GetPredicate(tid);
            typed.AddValues(predicate.Parameters);
            var expression = (Expression)new PolicySubqueryTenantRewriter(typed).Visit(predicate.Expression);
            expression = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(expression);
            referencesTarget = AstReflection.Collect<ColumnReference>(expression).Any(c =>
                c.Name.Parts.Count >= 2 && string.Equals(c.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
            var id = new SecurityPredicateId(tid.ToString(), 1);
            conjuncts.Add(new SecurityPredicateExpression(expression, id, scope));
            typed.RecordApplied(id);
            fingerprint = predicate.Fingerprint;
        }

        Expression? where = null;
        foreach (var conjunct in conjuncts)
        {
            where = where is null ? conjunct : new BinaryExpression(where, BinaryOperator.And, conjunct);
        }

        var projections = new List<SelectItem>(entry.Columns.Length);
        var maskFingerprints = new List<string>();
        foreach (var column in entry.Columns)
        {
            var colId = new SqlIdentifier(column.Name, IsQuoted: true);
            var columnRef = new ColumnReference(new SqlQualifiedName(new[] { colId }));
            if (typed.Masks.HasMask(tid, column.Name))
            {
                var spec = typed.GetMaskSpec(tid, column.Name);
                typed.AddValues(spec.Parameters);
                maskFingerprints.Add($"{column.Name}:{spec.Kind}:{AstReflection.Fingerprint(spec.Arguments)}");
                projections.Add(new ColumnSelectItem(new MaskExpression(spec.Kind, columnRef, spec.Arguments, column.DataType), colId));
            }
            else
            {
                projections.Add(new ColumnSelectItem(columnRef, colId));
            }
        }

        typed.RecordTable(new TableUsage(tid, applies, fingerprint,
            maskFingerprints.Count == 0 ? "-" : AstReflection.Fingerprint(maskFingerprints)));

        var canonical = tid.ToQualifiedName();
        var innerAlias = referencesTarget ? new SqlIdentifier(RowFilterAliases.Target) : null;
        var inner = new SelectStatement(null,
            new QuerySpecification(false, projections, new NamedTableSource(canonical, innerAlias), where, null, null),
            null, null);
        // An unquoted user name stays unquoted, so the dialect folds the alias and the user's references the same way.
        return new SubqueryTableSource(inner, node.Alias ?? new SqlIdentifier(node.Name.SimpleName, IsQuoted: node.Name.Parts[^1].IsQuoted));
    }

    private SubqueryTableSource CreateSecuredSubqueryTableSource(
        NamedTableSource node,
        string normalizedName,
        bool shouldApplyRls,
        bool hasMasking)
    {
        IReadOnlyList<SelectItem> projections;
        if (_options.TableColumnsProvider != null)
        {
            var columns = _options.TableColumnsProvider(normalizedName);
            if (columns != null && columns.Count > 0)
            {
                var list = new List<SelectItem>(columns.Count);
                foreach (var col in columns)
                {
                    // SEC M-24: Delimited identifiers for columns
                    var colId = new SqlIdentifier(col, IsQuoted: true);
                    if (_options.ColumnMaskingProvider != null && _options.ColumnMaskingProvider.HasMask(normalizedName, col))
                    {
                        // SQL-4: mask expressions are rendered by the gateway for the target dialect (bound key parameters,
                        // T-SQL brackets); re-parsing them as Trino SQL failed for every HMAC column. They are emitted
                        // verbatim, exactly like the legacy rewriter does.
                        string maskExpr = _options.ColumnMaskingProvider.GetMaskedExpression(normalizedName, col);
                        list.Add(new ColumnSelectItem(new TrustedSqlExpression(maskExpr), colId));
                    }
                    else
                    {
                        list.Add(new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { colId })), colId));
                    }
                }
                projections = list.AsReadOnly();
            }
            else
            {
                if (hasMasking)
                {
                    throw new SecurityException($"Table '{normalizedName}' contains masked columns but column provider returned empty schema.");
                }
                projections = new[] { new WildcardSelectItem(null) };
            }
        }
        else
        {
            if (hasMasking)
            {
                throw new SecurityException($"Table '{normalizedName}' contains masked columns but no TableColumnsProvider was configured to expand the projection.");
            }
            projections = new[] { new WildcardSelectItem(null) };
        }

        string policyFilter = shouldApplyRls ? _options.PolicyProvider.GetPolicyFilter(normalizedName) : string.Empty;
        bool referencesTarget = RowFilterAliases.ReferencesTarget(policyFilter);

        // Bind inner target table: if filter references autheris_target, alias must be declared
        var innerTableAlias = referencesTarget ? new SqlIdentifier(RowFilterAliases.Target) : null;
        var innerSource = new NamedTableSource(node.Name, innerTableAlias);

        Expression? whereClause = !string.IsNullOrWhiteSpace(policyFilter)
            ? PolicyFilterExpression(policyFilter)
            : null;

        var subqueryBody = new QuerySpecification(
            Distinct: false,
            Projections: projections,
            From: innerSource,
            Where: whereClause,
            GroupBy: null,
            Having: null);

        var subqueryStatement = new SelectStatement(null, subqueryBody, null, null);

        SqlIdentifier subqueryAlias;
        if (node.Alias != null)
        {
            subqueryAlias = node.Alias;
        }
        else
        {
            bool isQuoted = node.Name.Parts[^1].IsQuoted || _options.TargetDialect == TargetSqlDialect.SqlServer;
            subqueryAlias = new SqlIdentifier(node.Name.SimpleName, isQuoted);
        }

        return new SubqueryTableSource(subqueryStatement, subqueryAlias);
    }

    public override SqlNode VisitTableQueryBody(TableQueryBody node)
    {
        var namedTableSource = new NamedTableSource(node.TableName, null);
        var securedSource = (TableSource)Visit(namedTableSource);
        return new QuerySpecification(
            Distinct: false,
            Projections: new[] { new WildcardSelectItem(null) },
            From: securedSource,
            Where: null,
            GroupBy: null,
            Having: null);
    }

    public override SqlNode VisitDeleteStatement(DeleteStatement node)
    {
        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;

        string? targetAlias = node.TargetTable.Alias?.Value;

        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in WHERE
        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, node.Where, "DELETE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "DELETE");

        // Visit WHERE clause so subqueries within WHERE have RLS/masking applied
        var visitedWhere = node.Where != null ? (Expression)Visit(node.Where) : null;

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node with { Where = visitedWhere };
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = PolicyFilterExpression(policyFilter);
        Expression combinedWhere = visitedWhere != null
            ? (Expression)new BinaryExpression(new ParenthesizedExpression(visitedWhere), BinaryOperator.And, new ParenthesizedExpression(rlsFilter))
            : new ParenthesizedExpression(rlsFilter);

        return node with { Where = combinedWhere };
    }

    public override SqlNode VisitUpdateStatement(UpdateStatement node)
    {
        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;
        string? targetAlias = node.TargetTable.Alias?.Value;

        var visitedAssignments = new List<UpdateAssignment>(node.Assignments.Count);
        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in SET or WHERE
        foreach (var assignment in node.Assignments)
        {
            string colName = assignment.Column.Value;
            if (_options.ColumnMaskingProvider != null && _options.ColumnMaskingProvider.HasMask(normalizedName, colName))
            {
                throw new SecurityException($"Masked column '{colName}' of table '{normalizedName}' must not be referenced in UPDATE SET.");
            }
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, assignment.Value, "UPDATE SET");

            var visitedValue = (Expression)Visit(assignment.Value);
            visitedAssignments.Add(assignment with { Value = visitedValue });
        }

        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, targetAlias, node.Where, "UPDATE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "UPDATE");

        // Visit WHERE clause so subqueries within WHERE have RLS/masking applied
        var visitedWhere = node.Where != null ? (Expression)Visit(node.Where) : null;

        // WITH CHECK OPTION verification on assignments
        string tenantColumn = _options.GetTenantColumnName(normalizedName);
        if (_options.EnforceWithCheckOption)
        {
            foreach (var assignment in visitedAssignments)
            {
                string colName = assignment.Column.Value;
                if (colName.Equals(tenantColumn, StringComparison.OrdinalIgnoreCase))
                {
                    if (_options.DisallowTenantColumnModificationInUpdate)
                    {
                        throw new SecurityException($"Modification of tenant column '{colName}' is not allowed in UPDATE statement.");
                    }

                    if (string.IsNullOrEmpty(_options.ExpectedTenantValue))
                    {
                        throw new SecurityException("Expected tenant value must be configured when WITH CHECK OPTION is active.");
                    }

                    string expectedTenant = SqlIdentifierHelper.UnquoteStringLiteral(_options.ExpectedTenantValue);
                    EnsureTenantLiteral(assignment.Value, expectedTenant, "UPDATE", tenantColumn);
                }
            }
        }

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node with { Assignments = visitedAssignments.AsReadOnly(), Where = visitedWhere };
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = PolicyFilterExpression(policyFilter);
        Expression combinedWhere = visitedWhere != null
            ? (Expression)new BinaryExpression(new ParenthesizedExpression(visitedWhere), BinaryOperator.And, new ParenthesizedExpression(rlsFilter))
            : new ParenthesizedExpression(rlsFilter);

        return node with { Assignments = visitedAssignments.AsReadOnly(), Where = combinedWhere };
    }

    public override SqlNode VisitInsertStatement(InsertStatement node)
    {
        // SQL-7: the enforced row limit applies to the root SELECT of a read. A DML statement has none; a SELECT inside its
        // WHERE (or the source of INSERT ... SELECT) must not be truncated, that would change which rows are written.
        _rootLimitHandled = true;
        string normalizedName = node.TargetTable.Name.NormalizedName;
        string simpleTableName = node.TargetTable.Name.SimpleName;
        string tenantColumn = _options.GetTenantColumnName(normalizedName);

        // SQ-07: Reject INSERT on tables that have custom row-level consent filters beyond simple tenant partition
        if (_options.RejectConsentFilteredInsert && _options.TablesWithConsentRowFilter.Count > 0)
        {
            if (_options.TablesWithConsentRowFilter.Contains(normalizedName) ||
                _options.TablesWithConsentRowFilter.Contains(simpleTableName))
            {
                throw new SecurityException($"INSERT into table '{normalizedName}' with custom row-level consent filter is not permitted.");
            }
        }

        if (_options.EnforceWithCheckOption)
        {
            int tenantIndex = -1;
            if (node.Columns != null)
            {
                for (int i = 0; i < node.Columns.Count; i++)
                {
                    if (node.Columns[i].Value.Equals(tenantColumn, StringComparison.OrdinalIgnoreCase))
                    {
                        tenantIndex = i;
                        break;
                    }
                }
            }

            if (tenantIndex == -1)
            {
                if (_options.RequireTenantColumnInInsert)
                {
                    throw new SecurityException($"Tenant column '{tenantColumn}' must be explicitly specified in INSERT statement.");
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_options.ExpectedTenantValue))
                {
                    throw new SecurityException("Expected tenant value must be configured when WITH CHECK OPTION is active.");
                }
                string expectedTenant = SqlIdentifierHelper.UnquoteStringLiteral(_options.ExpectedTenantValue);
                VerifyInsertSource(node.Source, tenantIndex, expectedTenant, tenantColumn);
            }
        }

        var source = (QueryBody)Visit(node.Source);
        if (source == node.Source) return node;
        return node with { Source = source };
    }

    private void VerifyInsertSource(QueryBody source, int tenantIndex, string expectedTenant, string tenantColumn)
    {
        switch (source)
        {
            case ValuesQueryBody values:
                if (values.Rows.Count == 0)
                {
                    throw new SecurityException("INSERT VALUES clause without rows cannot be verified.");
                }
                foreach (var row in values.Rows)
                {
                    if (tenantIndex >= row.Elements.Count)
                    {
                        throw new SecurityException($"Tenant column '{tenantColumn}' has no value in an INSERT VALUES row.");
                    }
                    EnsureTenantLiteral(row.Elements[tenantIndex], expectedTenant, "INSERT", tenantColumn);
                }
                break;
            case QuerySpecification select:
                for (int i = 0; i < select.Projections.Count && i <= tenantIndex; i++)
                {
                    if (select.Projections[i] is not ColumnSelectItem)
                    {
                        throw new SecurityException($"Tenant column '{tenantColumn}' in INSERT SELECT cannot be verified (wildcard projection).");
                    }
                }
                if (tenantIndex >= select.Projections.Count || select.Projections[tenantIndex] is not ColumnSelectItem colItem)
                {
                    throw new SecurityException($"Tenant column '{tenantColumn}' has no value in INSERT SELECT.");
                }
                EnsureTenantLiteral(colItem.Expression, expectedTenant, "INSERT SELECT", tenantColumn);
                break;
            case SetOperationQuery setOp:
                VerifyInsertSource(setOp.Left, tenantIndex, expectedTenant, tenantColumn);
                VerifyInsertSource(setOp.Right, tenantIndex, expectedTenant, tenantColumn);
                break;
            default:
                throw new SecurityException("INSERT source query shape cannot be verified against the tenant WITH CHECK OPTION.");
        }
    }

    private void EnsureTenantLiteral(Expression expr, string expectedTenant, string operation, string tenantColumn)
    {
        if (expr is not LiteralExpression lit || lit.Value == null)
        {
            throw new SecurityException($"Tenant column '{tenantColumn}' in {operation} must be a literal value.");
        }

        string val = lit.Value.ToString() ?? string.Empty;
        if (!val.Equals(expectedTenant, StringComparison.Ordinal))
        {
            throw new SecurityException($"Tenant column '{tenantColumn}' {operation} value '{val}' does not match expected tenant '{expectedTenant}'.");
        }
    }

    private void EnsureFilteredDml(Expression? where, string operation)
    {
        if (!_options.RejectUnfilteredDml) return;

        if (where == null)
        {
            throw new UnfilteredDmlException($"{operation} without a WHERE clause is not permitted.");
        }

        // Plan 1: DML statement WHERE clause must reference at least one table column; literal-only predicates are forbidden
        if (!ContainsColumnReference(where))
        {
            throw new UnfilteredDmlException($"{operation} statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.");
        }

        if (IsTriviallyTrue(where))
        {
            throw new UnfilteredDmlException($"{operation} with a trivially true WHERE clause is not permitted.");
        }
    }

    private static bool ContainsColumnReference(Expression? expr)
    {
        if (expr == null) return false;

        return expr switch
        {
            ColumnReference => true,
            BinaryExpression b => ContainsColumnReference(b.Left) || ContainsColumnReference(b.Right),
            UnaryExpression u => ContainsColumnReference(u.Operand),
            LikeExpression l => ContainsColumnReference(l.Operand) || ContainsColumnReference(l.Pattern) || (l.Escape != null && ContainsColumnReference(l.Escape)),
            InListExpression inList => ContainsColumnReference(inList.Operand) || inList.Items.Any(ContainsColumnReference),
            InSubqueryExpression inSq => ContainsColumnReference(inSq.Operand) || ContainsColumnReferenceInQuery(inSq.Subquery),
            ExistsExpression ex => ContainsColumnReferenceInQuery(ex.Subquery),
            ScalarSubqueryExpression sc => ContainsColumnReferenceInQuery(sc.Subquery),
            BetweenExpression between => ContainsColumnReference(between.Operand) || ContainsColumnReference(between.Lower) || ContainsColumnReference(between.Upper),
            CaseExpression caseExpr => (caseExpr.Operand != null && ContainsColumnReference(caseExpr.Operand)) ||
                                       caseExpr.WhenClauses.Any(w => ContainsColumnReference(w.Condition) || ContainsColumnReference(w.Result)) ||
                                       (caseExpr.ElseResult != null && ContainsColumnReference(caseExpr.ElseResult)),
            FunctionCallExpression func => func.Arguments.Any(ContainsColumnReference),
            SubstringExpression sub => ContainsColumnReference(sub.Source) || ContainsColumnReference(sub.Start) || (sub.Length != null && ContainsColumnReference(sub.Length)),
            TrimExpression trim => ContainsColumnReference(trim.Source) || (trim.Characters != null && ContainsColumnReference(trim.Characters)),
            PositionExpression pos => ContainsColumnReference(pos.Needle) || ContainsColumnReference(pos.Haystack),
            CastExpression cast => ContainsColumnReference(cast.Operand),
            DateFunctionExpression dateFunc => ContainsColumnReference(dateFunc.Source),
            ExtractExpression extract => ContainsColumnReference(extract.Source),
            IsDistinctFromExpression distinct => ContainsColumnReference(distinct.Left) || ContainsColumnReference(distinct.Right),
            QuantifiedComparisonExpression quant => ContainsColumnReference(quant.Left) || ContainsColumnReferenceInQuery(quant.Subquery),
            _ => false
        };
    }

    private static bool ContainsColumnReferenceInQuery(SelectStatement subquery)
    {
        var stack = new Stack<SqlNode>();
        stack.Push(subquery);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference or WildcardSelectItem or UsingJoinCondition)
            {
                return true;
            }
            PushChildren(node, stack);
        }

        return false;
    }

    private static bool TryCompareNumericLiterals(LiteralExpression left, LiteralExpression right, out int comparison)
    {
        comparison = 0;
        if (left.Value == null || right.Value == null) return false;

        if (double.TryParse(left.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d1) &&
            double.TryParse(right.Value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d2))
        {
            comparison = d1.CompareTo(d2);
            return true;
        }

        return false;
    }

    private static bool IsTriviallyTrue(Expression? expr)
    {
        if (expr == null) return false;

        switch (expr)
        {
            case ParenthesizedExpression p:
                return IsTriviallyTrue(p.Expression);
            case LiteralExpression lit when lit.Type == LiteralType.Boolean:
                return true.Equals(lit.Value);
            case BinaryExpression b when b.Operator == BinaryOperator.Or:
                return IsTriviallyTrue(b.Left) || IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator == BinaryOperator.And:
                return IsTriviallyTrue(b.Left) && IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator == BinaryOperator.Equal:
                if (b.Left is LiteralExpression el1 && b.Right is LiteralExpression el2)
                {
                    if (TryCompareNumericLiterals(el1, el2, out int cmpEq))
                        return cmpEq == 0;
                    return Equals(el1.Value?.ToString(), el2.Value?.ToString());
                }
                if (b.Left is ColumnReference ec1 && b.Right is ColumnReference ec2)
                {
                    return ec1.Name.NormalizedName.Equals(ec2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.NotEqual:
                if (b.Left is LiteralExpression nl1 && b.Right is LiteralExpression nl2)
                {
                    if (TryCompareNumericLiterals(nl1, nl2, out int cmpNeq))
                        return cmpNeq != 0;
                    return !Equals(nl1.Value?.ToString(), nl2.Value?.ToString());
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThan:
                if (b.Left is LiteralExpression ltl && b.Right is LiteralExpression ltr &&
                    TryCompareNumericLiterals(ltl, ltr, out int cmpLt))
                {
                    return cmpLt < 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.LessThanOrEqual:
                if (b.Left is LiteralExpression le1 && b.Right is LiteralExpression le2)
                {
                    if (TryCompareNumericLiterals(le1, le2, out int cmpLe))
                        return cmpLe <= 0;
                    return Equals(le1.Value?.ToString(), le2.Value?.ToString());
                }
                if (b.Left is ColumnReference leCol1 && b.Right is ColumnReference leCol2)
                {
                    return leCol1.Name.NormalizedName.Equals(leCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThan:
                if (b.Left is LiteralExpression gtl && b.Right is LiteralExpression gtr &&
                    TryCompareNumericLiterals(gtl, gtr, out int cmpGt))
                {
                    return cmpGt > 0;
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.GreaterThanOrEqual:
                if (b.Left is LiteralExpression ge1 && b.Right is LiteralExpression ge2)
                {
                    if (TryCompareNumericLiterals(ge1, ge2, out int cmpGe))
                        return cmpGe >= 0;
                    return Equals(ge1.Value?.ToString(), ge2.Value?.ToString());
                }
                if (b.Left is ColumnReference geCol1 && b.Right is ColumnReference geCol2)
                {
                    return geCol1.Name.NormalizedName.Equals(geCol2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BetweenExpression between:
                if (between.Operand is ColumnReference opCol &&
                    between.Lower is ColumnReference lowCol &&
                    between.Upper is ColumnReference upCol)
                {
                    if (opCol.Name.NormalizedName.Equals(lowCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase) &&
                        opCol.Name.NormalizedName.Equals(upCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            case InListExpression inList:
                if (inList.Operand is ColumnReference inCol &&
                    inList.Items.Any(item => item is ColumnReference itemCol &&
                                            itemCol.Name.NormalizedName.Equals(inCol.Name.NormalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
                return false;
            case UnaryExpression u when u.Operator == UnaryOperator.Not:
                return u.Operand is LiteralExpression boolLit && boolLit.Type == LiteralType.Boolean && false.Equals(boolLit.Value);
            case UnaryExpression u when u.Operator == UnaryOperator.IsNotNull:
                return u.Operand is LiteralExpression constLit && constLit.Type != LiteralType.Null;
            default:
                return false;
        }
    }

    private void EnsureNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInDml || _options.ColumnMaskingProvider == null)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private void EnsureNoMaskedColumnReferencesInPredicate(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInPredicates || _options.ColumnMaskingProvider == null)
            return;

        CheckNoMaskedColumnReferences(normalizedTableName, targetAlias, scope, clause);
    }

    private void CheckNoMaskedColumnReferences(string normalizedTableName, string? targetAlias, SqlNode scope, string clause)
    {
        if (_options.ColumnMaskingProvider == null)
            return;

        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        var stack = new Stack<SqlNode>();
        stack.Push(scope);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is ColumnReference colRef)
            {
                string colName = colRef.Name.SimpleName;
                bool isMasked = _options.ColumnMaskingProvider.HasMask(normalizedTableName, colName);

                if (!isMasked && colRef.Name.Parts.Count > 1)
                {
                    string qualifier = colRef.Name.Parts[^2].Value;
                    isMasked = _options.ColumnMaskingProvider.HasMask(qualifier, colName);
                }

                if (!isMasked && _options.TablesWithMaskedColumns != null)
                {
                    foreach (var tbl in _options.TablesWithMaskedColumns)
                    {
                        if (_options.ColumnMaskingProvider.HasMask(tbl, colName))
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
                    bool isMasked = _options.ColumnMaskingProvider.HasMask(normalizedTableName, colName);
                    if (!isMasked && _options.TablesWithMaskedColumns != null)
                    {
                        foreach (var tbl in _options.TablesWithMaskedColumns)
                        {
                            if (_options.ColumnMaskingProvider.HasMask(tbl, colName))
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

    /// <summary>
    /// R-53 / B-06 / Section 4: Generates dialect-specific SQL expressions for advanced masking rules:
    /// - GEO_JITTER: CASE WHEN IS NULL OR 0.0 THEN ... ELSE ROUND(...) END
    /// - PARTIAL_MASK: CASE WHEN IS NULL THEN NULL WHEN LEN(...) &lt;= ... THEN '*****' ELSE CONCAT(...) END
    /// - REDACT: typgerechtes CAST(NULL AS ...) for numeric/temporal/boolean, or '[REDACTED]' for text
    /// </summary>
    public static string BuildDialectMaskExpression(
        string columnName,
        string ruleType,
        TargetSqlDialect dialect,
        string? dataType = null,
        int decimals = 2,
        int keepPrefix = 1,
        int keepSuffix = 0,
        char maskChar = '*',
        bool fixedLength = false,
        string? replacement = null)
    {
        var normRule = (ruleType ?? "REDACT").Trim().ToUpperInvariant();
        var isSqlServer = dialect == TargetSqlDialect.SqlServer;
        var quotedCol = isSqlServer
            ? $"[{columnName.Replace("]", "]]")}]"
            : $"\"{columnName.Replace("\"", "\"\"")}\"";

        switch (normRule)
        {
            case "GEO_JITTER":
            {
                var dec = Math.Clamp(decimals, 0, 6);
                if (isSqlServer)
                {
                    return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}, {dec}) END";
                }
                if (dialect is TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb)
                {
                    return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}::numeric, {dec})::double precision END";
                }
                return $"CASE WHEN {quotedCol} IS NULL OR {quotedCol} = 0.0 THEN {quotedCol} ELSE ROUND({quotedCol}, {dec}) END";
            }

            case "PARTIAL_MASK":
            {
                var p = Math.Max(0, keepPrefix);
                var s = Math.Max(0, keepSuffix);
                var maskStr = fixedLength ? new string(maskChar, 5) : (p + s >= 0 ? new string(maskChar, 5) : "*****");

                if (isSqlServer)
                {
                    if (s > 0)
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPLICATE('{maskChar}', CASE WHEN LEN({quotedCol}) > {p + s} THEN LEN({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LEN({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(LEFT({quotedCol}, {p}), {maskPart}, RIGHT({quotedCol}, {s})) END";
                    }
                    else
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPLICATE('{maskChar}', CASE WHEN LEN({quotedCol}) > {p + s} THEN LEN({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LEN({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(LEFT({quotedCol}, {p}), {maskPart}) END";
                    }
                }
                else if (dialect == TargetSqlDialect.Sqlite)
                {
                    if (s > 0)
                    {
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE SUBSTR({quotedCol}, 1, {p}) || '{maskStr}' || SUBSTR({quotedCol}, -{s}) END";
                    }
                    else
                    {
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE SUBSTR({quotedCol}, 1, {p}) || '{maskStr}' END";
                    }
                }
                else // PostgreSql, DuckDb, Trino, Ansi
                {
                    if (s > 0)
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPEAT('{maskChar}', CASE WHEN LENGTH({quotedCol}) > {p + s} THEN LENGTH({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(SUBSTRING({quotedCol} FROM 1 FOR {p}), {maskPart}, SUBSTRING({quotedCol} FROM LENGTH({quotedCol}) - {s - 1} FOR {s})) END";
                    }
                    else
                    {
                        var maskPart = fixedLength
                            ? $"'{maskStr}'"
                            : $"REPEAT('{maskChar}', CASE WHEN LENGTH({quotedCol}) > {p + s} THEN LENGTH({quotedCol}) - {p + s} ELSE 5 END)";
                        return $"CASE WHEN {quotedCol} IS NULL THEN NULL WHEN LENGTH({quotedCol}) <= {p + s} THEN '{new string(maskChar, 5)}' ELSE CONCAT(SUBSTRING({quotedCol} FROM 1 FOR {p}), {maskPart}) END";
                    }
                }
            }

            case "REDACT":
            {
                if (!string.IsNullOrWhiteSpace(dataType) && IsNumericOrTemporalType(dataType))
                {
                    var castType = MapToDialectTypeName(dataType, dialect);
                    return $"CAST(NULL AS {castType})";
                }
                var prefix = isSqlServer ? "N" : "";
                var repl = replacement ?? "[REDACTED]";
                return $"{prefix}'{repl.Replace("'", "''")}'";
            }

            case "NULLIFY":
                return "NULL";

            default:
                if (!string.IsNullOrWhiteSpace(dataType) && IsNumericOrTemporalType(dataType))
                {
                    var castType = MapToDialectTypeName(dataType, dialect);
                    return $"CAST(NULL AS {castType})";
                }
                var dPrefix = isSqlServer ? "N" : "";
                var dRepl = replacement ?? "[REDACTED]";
                return $"{dPrefix}'{dRepl.Replace("'", "''")}'";
        }
    }

    private static bool IsNumericOrTemporalType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();
        return dt is "int" or "integer" or "bigint" or "smallint" or "tinyint" or "numeric" or "decimal"
            or "money" or "smallmoney" or "real" or "float" or "double precision" or "double"
            or "bit" or "bool" or "boolean" or "date" or "datetime" or "datetime2" or "smalldatetime"
            or "timestamp" or "timestamptz" or "time" or "uniqueidentifier" or "uuid";
    }

    private static string MapToDialectTypeName(string dataType, TargetSqlDialect dialect)
    {
        var dt = dataType.Trim().ToLowerInvariant();
        var orig = dataType.Trim();
        if (dt.Contains('(')) dt = dt[..dt.IndexOf('(')].Trim();

        return dialect switch
        {
            TargetSqlDialect.SqlServer => dt switch
            {
                "int" or "integer" => "INT",
                "bigint" => "BIGINT",
                "smallint" => "SMALLINT",
                "tinyint" => "TINYINT",
                "decimal" or "numeric" => orig.Contains('(') ? orig.ToUpperInvariant() : "DECIMAL(18, 4)",
                "float" or "double" or "real" => "FLOAT",
                "bit" or "bool" or "boolean" => "BIT",
                "date" => "DATE",
                "datetime" or "datetime2" or "timestamp" => "DATETIME2",
                "uniqueidentifier" or "uuid" => "UNIQUEIDENTIFIER",
                _ => "DECIMAL(18, 4)"
            },
            TargetSqlDialect.PostgreSql or TargetSqlDialect.DuckDb => dt switch
            {
                "int" or "integer" => "INTEGER",
                "bigint" => "BIGINT",
                "smallint" or "tinyint" => "SMALLINT",
                "decimal" or "numeric" => "NUMERIC",
                "float" or "real" => "REAL",
                "double" or "double precision" => "DOUBLE PRECISION",
                "bit" or "bool" or "boolean" => "BOOLEAN",
                "date" => "DATE",
                "datetime" or "datetime2" or "timestamp" or "timestamptz" => "TIMESTAMP",
                "uuid" or "uniqueidentifier" => "UUID",
                _ => "NUMERIC"
            },
            TargetSqlDialect.Sqlite => dt switch
            {
                "int" or "integer" or "bigint" or "smallint" or "tinyint" => "INTEGER",
                _ => "NUMERIC"
            },
            _ => dt switch
            {
                "int" or "integer" => "INTEGER",
                "bigint" => "BIGINT",
                "decimal" or "numeric" => "NUMERIC",
                "float" or "double" => "DOUBLE PRECISION",
                "date" => "DATE",
                "datetime" or "timestamp" => "TIMESTAMP",
                "bool" or "boolean" => "BOOLEAN",
                _ => "NUMERIC"
            }
        };
    }

    private static void PushChildren(SqlNode node, Stack<SqlNode> stack)
    {
        switch (node)
        {
            case ParenthesizedExpression p:
                stack.Push(p.Expression);
                break;
            case BinaryExpression b:
                stack.Push(b.Right);
                stack.Push(b.Left);
                break;
            case UnaryExpression u:
                stack.Push(u.Operand);
                break;
            case CastExpression cast:
                stack.Push(cast.Operand);
                break;
            case LikeExpression lk:
                if (lk.Escape != null) stack.Push(lk.Escape);
                stack.Push(lk.Pattern);
                stack.Push(lk.Operand);
                break;
            case IsDistinctFromExpression df:
                stack.Push(df.Right);
                stack.Push(df.Left);
                break;
            case BetweenExpression bt:
                stack.Push(bt.Upper);
                stack.Push(bt.Lower);
                stack.Push(bt.Operand);
                break;
            case InListExpression inL:
                foreach (var it in inL.Items) stack.Push(it);
                stack.Push(inL.Operand);
                break;
            case InSubqueryExpression inSq:
                stack.Push(inSq.Subquery);
                stack.Push(inSq.Operand);
                break;
            case ScalarSubqueryExpression sc:
                stack.Push(sc.Subquery);
                break;
            case ExistsExpression ex:
                stack.Push(ex.Subquery);
                break;
            case QuantifiedComparisonExpression qc:
                stack.Push(qc.Subquery);
                stack.Push(qc.Left);
                break;
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments) stack.Push(arg);
                if (fn.Filter != null) stack.Push(fn.Filter);
                if (fn.OrderWithin != null) stack.Push(fn.OrderWithin);
                if (fn.Window?.PartitionBy != null) foreach (var p in fn.Window.PartitionBy) stack.Push(p);
                if (fn.Window?.OrderBy != null) stack.Push(fn.Window.OrderBy);
                break;
            case CaseExpression cs:
                if (cs.ElseResult != null) stack.Push(cs.ElseResult);
                foreach (var w in cs.WhenClauses) { stack.Push(w.Result); stack.Push(w.Condition); }
                if (cs.Operand != null) stack.Push(cs.Operand);
                break;
            case RowValueExpression row:
                foreach (var el in row.Elements) stack.Push(el);
                break;
            case ArrayConstructorExpression arr:
                foreach (var el in arr.Elements) stack.Push(el);
                break;
            case SubscriptExpression sub:
                stack.Push(sub.Index);
                stack.Push(sub.Target);
                break;
            case ExtractExpression ext:
                stack.Push(ext.Source);
                break;
            case SubstringExpression sub:
                stack.Push(sub.Source);
                stack.Push(sub.Start);
                if (sub.Length != null) stack.Push(sub.Length);
                break;
            case TrimExpression trim:
                stack.Push(trim.Source);
                if (trim.Characters != null) stack.Push(trim.Characters);
                break;
            case DateFunctionExpression date:
                stack.Push(date.Source);
                break;
            case PositionExpression pos:
                stack.Push(pos.Needle);
                stack.Push(pos.Haystack);
                break;
            case SelectStatement s:
                if (s.With != null) { foreach (var cte in s.With.Ctes) stack.Push(cte.Query); }
                stack.Push(s.Body);
                if (s.OrderBy != null) stack.Push(s.OrderBy);
                if (s.Pagination != null)
                {
                    if (s.Pagination.Offset != null) stack.Push(s.Pagination.Offset);
                    if (s.Pagination.Limit != null) stack.Push(s.Pagination.Limit);
                }
                break;
            case QuerySpecification qs:
                foreach (var p in qs.Projections) stack.Push(p);
                if (qs.From != null) stack.Push(qs.From);
                if (qs.Where != null) stack.Push(qs.Where);
                if (qs.GroupBy != null) stack.Push(qs.GroupBy);
                if (qs.Having != null) stack.Push(qs.Having);
                break;
            case SetOperationQuery so:
                stack.Push(so.Left);
                stack.Push(so.Right);
                break;
            case ValuesQueryBody vq:
                foreach (var r in vq.Rows) stack.Push(r);
                break;
            case JoinedTableSource jt:
                stack.Push(jt.Left);
                stack.Push(jt.Right);
                if (jt.Condition != null) stack.Push(jt.Condition);
                break;
            case SubqueryTableSource st:
                stack.Push(st.Subquery);
                break;
            case LateralTableSource lt:
                stack.Push(lt.Subquery);
                break;
            case OnJoinCondition on:
                stack.Push(on.Predicate);
                break;
            case UsingJoinCondition:
                break;
            case GroupByClause gb:
                foreach (var g in gb.GroupingExpressions) stack.Push(g);
                if (gb.AdvancedElements != null)
                {
                    foreach (var adv in gb.AdvancedElements)
                    {
                        foreach (var set in adv.Sets)
                        {
                            foreach (var g in set) stack.Push(g);
                        }
                    }
                }
                break;
            case ColumnSelectItem csi:
                stack.Push(csi.Expression);
                break;
            case OrderByClause ob:
                foreach (var el in ob.Elements) stack.Push(el.Expression);
                break;
        }
    }

    private bool IsCte(SqlQualifiedName name)
    {
        if (!name.IsSimple) return false;
        string key = SqlIdentifierHelper.FoldIdentifierForScope(name.Parts[0]);
        return _cteScopeStack.Peek().Contains(key);
    }

    private bool HasMaskingForTable(string normalizedTableName)
    {
        if (_options.ColumnMaskingProvider == null) return false;

        string simpleTableName = SqlIdentifierHelper.GetSimpleName(normalizedTableName);
        if (_options.TablesWithMaskedColumns.Contains(normalizedTableName) ||
            _options.TablesWithMaskedColumns.Contains(simpleTableName))
        {
            return true;
        }

        if (_options.TableColumnsProvider != null)
        {
            var columns = _options.TableColumnsProvider(normalizedTableName);
            if (columns != null)
            {
                foreach (var col in columns)
                {
                    if (_options.ColumnMaskingProvider.HasMask(normalizedTableName, col))
                        return true;
                }
            }
        }

        return false;
    }

    public const string RedactedLiteralPlaceholder = "@p_redacted";
    public const int DefaultMaxAuditSqlLength = 4096;

    private static readonly Regex FallbackStringLiteralRegex = new(
        @"'([^']|'')*'",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    private static readonly Regex FallbackNumericLiteralRegex = new(
        @"(?<=[=<>!,\s(+\-*/%])\d+(\.\d+)?(?=[=<>!,\s);+\-*/%]|$)|\b\d+(\.\d+)?\b",
        RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// AU-06: DSGVO Art. 17 AST-Literal-Anonymisierung für SQL-Audit-Details (@p_redacted).
    /// Ersetzt alle String- und numerischen Literale durch @p_redacted und liefert den SHA-256 Hash des Original-SQL.
    /// </summary>
    public static (string RedactedSql, string OriginalSqlHash) AnonymizeSqlForAudit(string? sql, int maxLength = DefaultMaxAuditSqlLength)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return (string.Empty, Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())));
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        string result;
        try
        {
            var charStream = new ZeroCopyCaseInsensitiveStream(sql.AsMemory());
            var lexer = new SqlBaseLexer(charStream);
            lexer.RemoveErrorListeners();
            var tokens = new CommonTokenStream(lexer);
            tokens.Fill();

            var rewriter = new TokenStreamRewriter(tokens);
            var tokenList = tokens.GetTokens();

            for (int i = 0; i < tokenList.Count; i++)
            {
                var tok = tokenList[i];
                if (tok.Type == TokenConstants.EOF) break;

                if (tok.Type == SqlBaseLexer.STRING ||
                    tok.Type == SqlBaseLexer.UNICODE_STRING ||
                    tok.Type == SqlBaseLexer.DOLLAR_STRING ||
                    tok.Type == SqlBaseLexer.INTEGER_VALUE ||
                    tok.Type == SqlBaseLexer.DECIMAL_VALUE)
                {
                    rewriter.Replace(tok.TokenIndex, RedactedLiteralPlaceholder);
                }
            }

            result = rewriter.GetText();
        }
        catch
        {
            var s = FallbackStringLiteralRegex.Replace(sql, RedactedLiteralPlaceholder);
            result = FallbackNumericLiteralRegex.Replace(s, RedactedLiteralPlaceholder);
        }

        if (result.Length > maxLength)
        {
            result = result[..maxLength] + "...[TRUNCATED]";
        }

        return (result, hash);
    }
}


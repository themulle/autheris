namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;

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
    private int _subqueryDepth = 0;
    private bool _rootLimitHandled = false;

    public AstSecurityVisitor(RlsOptions? options = null, ISqlEngine? engine = null)
    {
        _options = options ?? new RlsOptions();
        _engine = engine ?? new FastSqlEngine();
        _cteScopeStack.Push(new HashSet<string>(StringComparer.Ordinal));
    }

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
                var cteQuery = (SelectStatement)Visit(cte.Query);
                string cteKey = SqlIdentifierHelper.FoldIdentifierForScope(cte.Name);
                _cteScopeStack.Peek().Add(cteKey);
                ctes.Add(cte with { Query = cteQuery });
            }
            with = node.With with { Ctes = ctes.AsReadOnly() };
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
            return node;
        }

        bool shouldApplyRls = _options.PolicyProvider.ShouldApplyPolicy(normalizedName);
        bool hasMasking = HasMaskingForTable(normalizedName);

        if (!shouldApplyRls && !hasMasking)
        {
            return node;
        }

        return CreateSecuredSubqueryTableSource(node, normalizedName, shouldApplyRls, hasMasking);
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
                        string maskExpr = _options.ColumnMaskingProvider.GetMaskedExpression(normalizedName, col);
                        var maskAst = ParseFilterExpression(maskExpr);
                        list.Add(new ColumnSelectItem(maskAst, colId));
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
            ? ParseFilterExpression(policyFilter)
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

    public override SqlNode VisitDeleteStatement(DeleteStatement node)
    {
        string normalizedName = node.TargetTable.Name.NormalizedName;

        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in WHERE
        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, node.Where, "DELETE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "DELETE");

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node;
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = ParseFilterExpression(policyFilter);
        var combinedWhere = node.Where != null
            ? new BinaryExpression(node.Where, BinaryOperator.And, rlsFilter)
            : rlsFilter;

        return node with { Where = combinedWhere };
    }

    public override SqlNode VisitUpdateStatement(UpdateStatement node)
    {
        string normalizedName = node.TargetTable.Name.NormalizedName;

        // SEC H-15 / SQ-03: Ensure no masked column or whole-row references in SET or WHERE
        foreach (var assignment in node.Assignments)
        {
            string colName = assignment.Column.Value;
            if (_options.ColumnMaskingProvider != null && _options.ColumnMaskingProvider.HasMask(normalizedName, colName))
            {
                throw new SecurityException($"Masked column '{colName}' of table '{normalizedName}' must not be referenced in UPDATE SET.");
            }
            EnsureNoMaskedColumnReferences(normalizedName, assignment.Value, "UPDATE SET");
        }

        if (node.Where != null)
        {
            EnsureNoMaskedColumnReferences(normalizedName, node.Where, "UPDATE WHERE");
        }

        // DML guardrail: Reject unfiltered or tautological WHERE
        EnsureFilteredDml(node.Where, "UPDATE");

        // WITH CHECK OPTION verification on assignments
        if (_options.EnforceWithCheckOption)
        {
            foreach (var assignment in node.Assignments)
            {
                string colName = assignment.Column.Value;
                if (colName.Equals(_options.TenantColumnName, StringComparison.OrdinalIgnoreCase))
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
                    EnsureTenantLiteral(assignment.Value, expectedTenant, "UPDATE");
                }
            }
        }

        if (IsCte(node.TargetTable.Name) || !_options.PolicyProvider.ShouldApplyPolicy(normalizedName))
        {
            return node;
        }

        string policyFilter = _options.PolicyProvider.GetPolicyFilter(normalizedName);
        if (RowFilterAliases.ReferencesTarget(policyFilter))
        {
            throw new SecurityException("Correlated row filters are not supported for UPDATE/DELETE statements.");
        }

        var rlsFilter = ParseFilterExpression(policyFilter);
        var combinedWhere = node.Where != null
            ? new BinaryExpression(node.Where, BinaryOperator.And, rlsFilter)
            : rlsFilter;

        return node with { Where = combinedWhere };
    }

    public override SqlNode VisitInsertStatement(InsertStatement node)
    {
        string normalizedName = node.TargetTable.Name.NormalizedName;
        string simpleTableName = node.TargetTable.Name.SimpleName;

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
                    if (node.Columns[i].Value.Equals(_options.TenantColumnName, StringComparison.OrdinalIgnoreCase))
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
                    throw new SecurityException($"Tenant column '{_options.TenantColumnName}' must be explicitly specified in INSERT statement.");
                }
            }
            else
            {
                if (string.IsNullOrEmpty(_options.ExpectedTenantValue))
                {
                    throw new SecurityException("Expected tenant value must be configured when WITH CHECK OPTION is active.");
                }
                string expectedTenant = SqlIdentifierHelper.UnquoteStringLiteral(_options.ExpectedTenantValue);
                VerifyInsertSource(node.Source, tenantIndex, expectedTenant);
            }
        }

        var source = (QueryBody)Visit(node.Source);
        if (source == node.Source) return node;
        return node with { Source = source };
    }

    private void VerifyInsertSource(QueryBody source, int tenantIndex, string expectedTenant)
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
                        throw new SecurityException($"Tenant column '{_options.TenantColumnName}' has no value in an INSERT VALUES row.");
                    }
                    EnsureTenantLiteral(row.Elements[tenantIndex], expectedTenant, "INSERT");
                }
                break;
            case QuerySpecification select:
                for (int i = 0; i < select.Projections.Count && i <= tenantIndex; i++)
                {
                    if (select.Projections[i] is not ColumnSelectItem)
                    {
                        throw new SecurityException($"Tenant column '{_options.TenantColumnName}' in INSERT SELECT cannot be verified (wildcard projection).");
                    }
                }
                if (tenantIndex >= select.Projections.Count || select.Projections[tenantIndex] is not ColumnSelectItem colItem)
                {
                    throw new SecurityException($"Tenant column '{_options.TenantColumnName}' has no value in INSERT SELECT.");
                }
                EnsureTenantLiteral(colItem.Expression, expectedTenant, "INSERT SELECT");
                break;
            case SetOperationQuery setOp:
                VerifyInsertSource(setOp.Left, tenantIndex, expectedTenant);
                VerifyInsertSource(setOp.Right, tenantIndex, expectedTenant);
                break;
            default:
                throw new SecurityException("INSERT source query shape cannot be verified against the tenant WITH CHECK OPTION.");
        }
    }

    private void EnsureTenantLiteral(Expression expr, string expectedTenant, string operation)
    {
        if (expr is not LiteralExpression lit || lit.Value == null)
        {
            throw new SecurityException($"Tenant column '{_options.TenantColumnName}' in {operation} must be a literal value.");
        }

        string val = lit.Value.ToString() ?? string.Empty;
        if (!val.Equals(expectedTenant, StringComparison.Ordinal))
        {
            throw new SecurityException($"Tenant column '{_options.TenantColumnName}' {operation} value '{val}' does not match expected tenant '{expectedTenant}'.");
        }
    }

    private void EnsureFilteredDml(Expression? where, string operation)
    {
        if (!_options.RejectUnfilteredDml) return;

        if (where == null)
        {
            throw new UnfilteredDmlException($"{operation} without a WHERE clause is not permitted.");
        }

        if (IsTriviallyTrue(where))
        {
            throw new UnfilteredDmlException($"{operation} with a trivially true WHERE clause is not permitted.");
        }
    }

    private static bool IsTriviallyTrue(Expression? expr)
    {
        if (expr == null) return false;

        switch (expr)
        {
            case LiteralExpression lit when lit.Type == LiteralType.Boolean:
                return true.Equals(lit.Value);
            case BinaryExpression b when b.Operator == BinaryOperator.Or:
                return IsTriviallyTrue(b.Left) || IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator == BinaryOperator.And:
                return IsTriviallyTrue(b.Left) && IsTriviallyTrue(b.Right);
            case BinaryExpression b when b.Operator is BinaryOperator.Equal or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThanOrEqual:
                if (b.Left is LiteralExpression l1 && b.Right is LiteralExpression l2)
                {
                    return Equals(l1.Value?.ToString(), l2.Value?.ToString());
                }
                if (b.Left is ColumnReference c1 && b.Right is ColumnReference c2)
                {
                    return c1.Name.NormalizedName.Equals(c2.Name.NormalizedName, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            case BinaryExpression b when b.Operator == BinaryOperator.NotEqual:
                if (b.Left is LiteralExpression nl1 && b.Right is LiteralExpression nl2)
                {
                    return !Equals(nl1.Value?.ToString(), nl2.Value?.ToString());
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

    private void EnsureNoMaskedColumnReferences(string normalizedTableName, Expression scope, string clause)
    {
        if (!_options.RejectMaskedColumnsInDml || _options.ColumnMaskingProvider == null)
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
                if (_options.ColumnMaskingProvider.HasMask(normalizedTableName, colName))
                {
                    throw new SecurityException($"Masked column '{colName}' of table '{normalizedTableName}' must not be referenced in {clause}.");
                }

                if (_options.RejectWholeRowReferencesInDml &&
                    (colName.Equals(normalizedTableName, StringComparison.OrdinalIgnoreCase) ||
                     colName.Equals(simpleTableName, StringComparison.OrdinalIgnoreCase)) &&
                    HasMaskingForTable(normalizedTableName))
                {
                    throw new SecurityException($"Whole-row reference to '{colName}' in {clause} is forbidden because table '{normalizedTableName}' contains masked columns.");
                }
            }

            // Push children
            PushChildren(node, stack);
        }
    }

    private static void PushChildren(SqlNode node, Stack<SqlNode> stack)
    {
        switch (node)
        {
            case BinaryExpression b:
                stack.Push(b.Right);
                stack.Push(b.Left);
                break;
            case UnaryExpression u:
                stack.Push(u.Operand);
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
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments) stack.Push(arg);
                break;
            case CaseExpression cs:
                if (cs.ElseResult != null) stack.Push(cs.ElseResult);
                foreach (var w in cs.WhenClauses) { stack.Push(w.Result); stack.Push(w.Condition); }
                if (cs.Operand != null) stack.Push(cs.Operand);
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

    private static bool IsQuotedIdentifier(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        return text.Length >= 2 &&
               ((text[0] == '"' && text[^1] == '"') ||
                (text[0] == '`' && text[^1] == '`') ||
                (text[0] == '[' && text[^1] == ']'));
    }
}

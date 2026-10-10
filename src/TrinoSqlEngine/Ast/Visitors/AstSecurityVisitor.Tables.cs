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
    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        string normalizedName = node.Name.NormalizedName;
        string simpleName = node.Name.SimpleName;
        string scopeKey = SqlIdentifierHelper.FoldIdentifierForScope(node.Name.Parts[^1]);

        // CR-ADG-32: a self-reference requires WITH RECURSIVE on every dialect (SQL Server and Oracle would otherwise recurse silently
        // while the gateway would treat a catalog-named reference as a secured physical scan).
        if (node.Name.IsSimple && !_cteScopeStack.Peek().Contains(scopeKey) && _definingNonRecursiveCtes.Contains(scopeKey))
        {
            throw new SecurityException("A CTE that references itself requires WITH RECURSIVE.");
        }

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
        }

        bool applies = typed.RowFilters.ShouldApplyPolicy(tid);
        bool referencesTarget = false;
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
        }

        Expression? where = null;
        foreach (var conjunct in conjuncts)
        {
            where = where is null ? conjunct : new BinaryExpression(where, BinaryOperator.And, conjunct);
        }

        var projections = new List<SelectItem>(entry.Columns.Length);
        foreach (var column in entry.Columns)
        {
            var colId = new SqlIdentifier(column.Name, IsQuoted: true);
            var columnRef = new ColumnReference(new SqlQualifiedName(new[] { colId }));
            if (typed.Masks.HasMask(tid, column.Name))
            {
                var spec = typed.GetMaskSpec(tid, column.Name);
                typed.AddValues(spec.Parameters);
                projections.Add(new ColumnSelectItem(new MaskExpression(spec.Kind, columnRef, spec.Arguments, column.DataType), colId));
            }
            else
            {
                projections.Add(new ColumnSelectItem(columnRef, colId));
            }
        }


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
}

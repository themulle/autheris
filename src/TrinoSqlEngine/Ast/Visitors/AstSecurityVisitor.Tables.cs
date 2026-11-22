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
    /// <summary>True once a table of the statement received a tenant predicate or a row policy; see <see cref="Security.RowPolicyErrorGuard"/>.</summary>
    public bool RowPolicyApplied { get; private set; }

    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        string scopeKey = SqlIdentifierHelper.FoldIdentifierForScope(node.Name.Parts[^1]);

        // CR-ADG-32: a self-reference requires WITH RECURSIVE on every dialect (SQL Server and Oracle would otherwise recurse silently
        // while the gateway would treat a catalog-named reference as a secured physical scan).
        if (node.Name.IsSimple && !_cteScopeStack.Peek().Contains(scopeKey) && _definingNonRecursiveCtes.Contains(scopeKey))
        {
            // Issue #277: the same typed answer as the router's pre-check (422); fixed text, no CTE or table name.
            throw new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, "CTE self-reference without WITH RECURSIVE");
        }

        // SEC C-02: Only simple (unqualified) names in CTE scope are considered CTEs
        if (node.Name.IsSimple && _cteScopeStack.Peek().Contains(scopeKey))
        {
            // CR-ADG-01: the reference is emitted from the definition identifier (delimited scope key). The user spelling
            // survives as the alias so that qualified column references keep binding under the dialect's own folding.
            return node with
            {
                Name = new SqlQualifiedName(new[] { new SqlIdentifier(scopeKey, IsQuoted: true) }),
                Alias = node.Alias ?? node.Name.Parts[0]
            };
        }

        return SecureTyped(node);
    }

    /// <summary>
    /// Typed injection: the table becomes<c>(SELECT cataloged columns FROM [schema].[table] WHERE tenant AND policy)</c>.
    /// The tenant predicate (binary-exact) always applies when the catalog declares a tenant column; the policy predicate
    /// applies when the provider says so. Both are opaque <see cref="SecurityPredicateExpression"/> nodes.
    /// </summary>
    private SubqueryTableSource SecureTyped(NamedTableSource node)
    {
        var typed = _typed;
        var entry = typed.Catalog.Resolve(node.Name)
            ?? throw new SecurityException("A table reference could not be resolved against the catalog.");
        if (entry.IsMetricView)
        {
            throw new SecurityException("A metric view may only be queried as a single top-level FROM source.");
        }
        var tid = entry.Identity;
        var scope = CurrentScope;
        typed.RecordDependency(node.Name, entry);

        if (typed.PerCellColumns.GetPolicy(tid) is { } perCell)
        {
            return SecureTypedPerCell(node, entry, perCell, typed, scope);
        }

        var conjuncts = new List<Expression>(2);
        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant != null)
        {
            RowPolicyApplied = true;
            var id = new SecurityPredicateId(tid.ToString(), 0);
            conjuncts.Add(new SecurityPredicateExpression(tenant, id, scope));
        }

        bool applies = typed.RowFilters.ShouldApplyPolicy(tid);
        bool referencesTarget = false;
        if (applies)
        {
            RowPolicyApplied = true;
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

        var plain = CatalogProjectionNodes.For(entry);
        var projections = new List<SelectItem>(entry.Columns.Length);
        for (int i = 0; i < entry.Columns.Length; i++)
        {
            var column = entry.Columns[i];
            var nodes = plain[i];
            if (typed.Masks.HasMask(tid, column.Name))
            {
                var spec = typed.GetMaskSpec(tid, column.Name);
                typed.AddValues(spec.Parameters);
                projections.Add(new ColumnSelectItem(new MaskExpression(spec.Kind, nodes.ColumnRef, spec.Arguments, column.DataType), nodes.Identifier));
            }
            else
            {
                projections.Add(nodes.Item);
            }
        }

        var canonical = tid.ToQualifiedName();
        var innerAlias = referencesTarget ? new SqlIdentifier(RowFilterAliases.Target) : null;
        var inner = new SelectStatement(null,
            new QuerySpecification(false, projections, new NamedTableSource(canonical, innerAlias), where, null, null),
            null, null);
        // An unquoted user name stays unquoted, so the dialect folds the alias and the user's references the same way.
        return new SubqueryTableSource(inner, node.Alias ?? new SqlIdentifier(node.Name.SimpleName, IsQuoted: node.Name.Parts[^1].IsQuoted))
        {
            EvaluationFence = !IsDmlCompile && _sqlServerRecursiveMemberDepth == 0 && typed.Capabilities.HasEvaluationFence && conjuncts.Count > 0
        };
    }

    private SubqueryTableSource SecureTypedPerCell(
        NamedTableSource node,
        TableCatalogEntry entry,
        PerCellTablePolicy perCell,
        TypedPolicyContext typed,
        SecurityScope scope)
    {
        RowPolicyApplied = true;
        var tid = entry.Identity;
        foreach (var sig in perCell.Signatures)
        {
            typed.AddValues(sig.Scope.Parameters);
        }

        if (perCell.NarrowingPredicate is not null)
        {
            RowPolicyApplied = true;
            typed.AddValues(perCell.NarrowingPredicate.Parameters);
        }

        var decomposed = PerCellFlagDecomposition.Decompose(
            perCell,
            entry,
            expr => (Expression)new PolicySubqueryTenantRewriter(typed).Visit(expr),
            stmt => (SelectStatement)new PolicySubqueryTenantRewriter(typed).Visit(stmt));

        var conjuncts = new List<Expression>(2);
        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant != null)
        {
            conjuncts.Add(new SecurityPredicateExpression(tenant, new SecurityPredicateId(tid.ToString(), 0), scope));
        }

        Expression? narrowingExpr = null;
        if (perCell.NarrowingPredicate is not null)
        {
            narrowingExpr = (Expression)new PolicySubqueryTenantRewriter(typed).Visit(perCell.NarrowingPredicate.Expression);
            narrowingExpr = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(narrowingExpr);
        }

        Expression? rowFilterExpr = decomposed.GrantUnionPredicate switch
        {
            null => narrowingExpr,
            var grantUnion when narrowingExpr is null => grantUnion,
            var grantUnion => new BinaryExpression(grantUnion, BinaryOperator.And, narrowingExpr)
        };

        if (rowFilterExpr is not null)
        {
            conjuncts.Add(new SecurityPredicateExpression(rowFilterExpr, new SecurityPredicateId(tid.ToString(), 1), scope));
        }

        Expression? where = null;
        foreach (var conjunct in conjuncts)
        {
            where = where is null ? conjunct : new BinaryExpression(where, BinaryOperator.And, conjunct);
        }

        var plain = CatalogProjectionNodes.For(entry);
        var projections = new List<SelectItem>(entry.Columns.Length);
        var branchPredicateId = new SecurityPredicateId(tid.ToString(), 2);

        for (int i = 0; i < entry.Columns.Length; i++)
        {
            var column = entry.Columns[i];
            var nodes = plain[i];
            if (decomposed.Columns.TryGetValue(column.Name, out var branches))
            {
                var whenClauses = new List<WhenClause>(2);
                if (branches.ClearCondition is not null)
                {
                    whenClauses.Add(new WhenClause(
                        new SecurityPredicateExpression(branches.ClearCondition, branchPredicateId, scope),
                        nodes.ColumnRef));
                }

                if (branches.MaskCondition is not null)
                {
                    var spec = typed.GetMaskSpec(tid, column.Name);
                    typed.AddValues(spec.Parameters);
                    var maskExpr = new MaskExpression(spec.Kind, nodes.ColumnRef, spec.Arguments, column.DataType);
                    whenClauses.Add(new WhenClause(
                        new SecurityPredicateExpression(branches.MaskCondition, branchPredicateId, scope),
                        maskExpr));
                }

                var caseExpr = new CaseExpression(
                    Operand: null,
                    whenClauses,
                    new LiteralExpression(null, LiteralType.Null));
                projections.Add(new ColumnSelectItem(caseExpr, nodes.Identifier));
            }
            else if (typed.Masks.HasMask(tid, column.Name))
            {
                var spec = typed.GetMaskSpec(tid, column.Name);
                typed.AddValues(spec.Parameters);
                projections.Add(new ColumnSelectItem(new MaskExpression(spec.Kind, nodes.ColumnRef, spec.Arguments, column.DataType), nodes.Identifier));
            }
            else
            {
                projections.Add(nodes.Item);
            }
        }

        var canonical = tid.ToQualifiedName();
        var innerAlias = decomposed.ReferencesTarget ? new SqlIdentifier(RowFilterAliases.Target) : null;
        TableSource from = new NamedTableSource(canonical, innerAlias);
        foreach (var flagJoin in decomposed.FlagJoins)
        {
            from = new JoinedTableSource(
                from,
                JoinType.LeftOuter,
                new SubqueryTableSource(flagJoin.Subquery, flagJoin.Alias),
                flagJoin.OnCondition);
        }

        var inner = new SelectStatement(null,
            new QuerySpecification(false, projections, from, where, null, null),
            null, null);
        return new SubqueryTableSource(inner, node.Alias ?? new SqlIdentifier(node.Name.SimpleName, IsQuoted: node.Name.Parts[^1].IsQuoted))
        {
            EvaluationFence = !IsDmlCompile && _sqlServerRecursiveMemberDepth == 0 && typed.Capabilities.HasEvaluationFence
        };
    }
}

/// <summary>
/// The unmasked projection nodes of a catalog entry (<c>"column"</c> as a reference and as a select item). They depend on the
/// immutable entry only (never on a request value, a policy or a mask), so they are built once per entry and shared between
/// compiles; AST nodes are immutable. A mask is still looked up per request and per column by the caller.
/// </summary>
internal static class CatalogProjectionNodes
{
    internal sealed record Column(SqlIdentifier Identifier, ColumnReference ColumnRef, ColumnSelectItem Item);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TableCatalogEntry, Column[]> Cache = new();

    public static Column[] For(TableCatalogEntry entry) => Cache.GetValue(entry, static e =>
    {
        var nodes = new Column[e.Columns.Length];
        for (int i = 0; i < nodes.Length; i++)
        {
            var id = new SqlIdentifier(e.Columns[i].Name, IsQuoted: true);
            var reference = new ColumnReference(new SqlQualifiedName(new[] { id }));
            nodes[i] = new Column(id, reference, new ColumnSelectItem(reference, id));
        }

        return nodes;
    });
}

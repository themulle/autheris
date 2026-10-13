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
    /// Typed injection (WP-A5): the table becomes <c>(SELECT cataloged columns FROM [schema].[table] WHERE tenant AND policy)</c>.
    /// The tenant predicate (binary-exact) always applies when the catalog declares a tenant column; the policy predicate
    /// applies when the provider says so. Both are opaque <see cref="SecurityPredicateExpression"/> nodes.
    /// </summary>
    private SubqueryTableSource SecureTyped(NamedTableSource node)
    {
        var typed = _typed;
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
        return new SubqueryTableSource(inner, node.Alias ?? new SqlIdentifier(node.Name.SimpleName, IsQuoted: node.Name.Parts[^1].IsQuoted));
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

namespace TrinoSqlEngine.Ast.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using TrinoSqlEngine.Governance;

/// <summary>
/// SEC-ADG-11: tables referenced inside a policy subquery (correlated row filters, virtual filters) receive the tenant
/// predicate, so another tenant's rows can never decide which of this tenant's rows are visible. They do not receive
/// consent or row filters (no recursion). Policy subqueries are one level deep; a nested one is rejected. Physical names are
/// replaced by the catalog's canonical schema-qualified names (INV-11).
/// </summary>
internal sealed class PolicySubqueryTenantRewriter : SqlAstRewriter
{
    private readonly TypedPolicyContext _typed;
    private int _depth;

    public PolicySubqueryTenantRewriter(TypedPolicyContext typed)
    {
        _typed = typed;
    }

    public override SqlNode VisitSelectStatement(SelectStatement node)
    {
        if (++_depth > 1)
        {
            throw new SecurityException("Nested policy subqueries are not permitted.");
        }

        try
        {
            return base.VisitSelectStatement(node);
        }
        finally
        {
            _depth--;
        }
    }

    public override SqlNode VisitNamedTableSource(NamedTableSource node)
    {
        if (_depth == 0)
        {
            return node;
        }

        var entry = _typed.Catalog.Resolve(node.Name)
            ?? throw new SecurityException("A policy subquery references a table that is not in the catalog.");
        _typed.RecordDependency(node.Name, entry);
        var canonical = entry.Identity.ToQualifiedName();

        var tenant = _typed.BuildTenantPredicate(entry);
        if (tenant is null)
        {
            return new NamedTableSource(canonical, node.Alias);
        }

        var id = new SecurityPredicateId(entry.Identity.ToString(), 0);
        var projections = entry.Columns
            .Select(c =>
            {
                var colId = new SqlIdentifier(c.Name, true);
                return (SelectItem)new ColumnSelectItem(new ColumnReference(new SqlQualifiedName(new[] { colId })), colId);
            })
            .ToList();
        var inner = new SelectStatement(null,
            new QuerySpecification(
                Distinct: false,
                Projections: projections,
                From: new NamedTableSource(canonical, null),
                Where: new SecurityPredicateExpression(tenant, id, SecurityScope.PolicySubquery),
                GroupBy: null,
                Having: null),
            null, null);
        // CR-ADG-16: an unquoted user name stays unquoted (as in SecureTyped), so the dialect folds the alias and the policy's own
        // references the same way (Oracle upper-cases unquoted names; a delimited lower-case alias would never match ORA-00904).
        return new SubqueryTableSource(inner, node.Alias ?? new SqlIdentifier(node.Name.SimpleName, IsQuoted: node.Name.Parts[^1].IsQuoted));
    }
}

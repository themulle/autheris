using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Ast.Visitors;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private QuerySpecification RewriteDirectMetricView(
        QuerySpecification node,
        NamedTableSource fromSource,
        TableCatalogEntry entry,
        bool maskedScope)
    {
        var typed = _typed;
        var tid = entry.Identity;
        if (typed.PerCellColumns.GetPolicy(tid) is not null)
        {
            throw new SecurityException("Per-cell column policies are not supported on a metric view.");
        }

        if (node.Projections.Any(static p => p is WildcardSelectItem))
        {
            throw new SecurityException("SELECT * is not permitted on a metric view.");
        }

        EnsureNoMaskedColumnsOnMetricView(entry, node.Projections, "SELECT");
        EnsureNoMaskedColumnsOnMetricView(entry, node.Where, "WHERE");
        EnsureNoMaskedColumnsOnMetricView(entry, node.GroupBy, "GROUP BY");
        EnsureNoMaskedColumnsOnMetricView(entry, node.Having, "HAVING");

        typed.RecordDependency(fromSource.Name, entry);
        var scope = CurrentScope;
        var conjuncts = new List<Expression>(2);

        var tenant = typed.BuildTenantPredicate(entry);
        if (tenant is not null)
        {
            RowPolicyApplied = true;
            conjuncts.Add(new SecurityPredicateExpression(tenant, new SecurityPredicateId(tid.ToString(), 0), scope));
        }

        bool referencesTarget = false;
        SqlIdentifier? rewrittenAlias = fromSource.Alias;
        if (typed.RowFilters.ShouldApplyPolicy(tid))
        {
            RowPolicyApplied = true;
            var predicate = typed.RowFilters.GetPredicate(tid);
            typed.AddValues(predicate.Parameters);
            var expression = (Expression)new PolicySubqueryTenantRewriter(typed).Visit(predicate.Expression);
            expression = (Expression)new PolicyColumnTypeAnnotator(entry).Visit(expression);
            referencesTarget = AstReflection.Collect<ColumnReference>(expression).Any(static c =>
                c.Name.Parts.Count >= 2 &&
                string.Equals(c.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
            if (referencesTarget)
            {
                var targetAlias = fromSource.Alias ?? new SqlIdentifier(tid.Table, IsQuoted: true);
                rewrittenAlias ??= targetAlias;
                expression = (Expression)new TargetAliasRewriter(targetAlias).Visit(expression);
            }

            conjuncts.Add(new SecurityPredicateExpression(expression, new SecurityPredicateId(tid.ToString(), 1), scope));
        }

        Expression? secWhere = null;
        foreach (var conjunct in conjuncts)
        {
            secWhere = secWhere is null ? conjunct : new BinaryExpression(secWhere, BinaryOperator.And, conjunct);
        }

        List<SelectItem> projections;
        Expression? visitedWhere;
        GroupByClause? groupBy;
        Expression? having;

        if (maskedScope)
        {
            _maskedScopeDepth++;
        }

        try
        {
            projections = new List<SelectItem>(node.Projections.Count);
            foreach (var item in node.Projections)
            {
                projections.Add((SelectItem)Visit(item));
            }

            visitedWhere = node.Where is not null ? (Expression)Visit(node.Where) : null;
            groupBy = node.GroupBy is not null ? (GroupByClause)Visit(node.GroupBy) : null;
            having = node.Having is not null ? (Expression)Visit(node.Having) : null;
        }
        finally
        {
            if (maskedScope)
            {
                _maskedScopeDepth--;
            }
        }

        Expression? combinedWhere = (secWhere, visitedWhere) switch
        {
            (null, var u) => u,
            (var s, null) => s,
            (var s, BinaryExpression { Operator: BinaryOperator.Or } orExpr) =>
                new BinaryExpression(s, BinaryOperator.And, new ParenthesizedExpression(orExpr)),
            (var s, var u) => new BinaryExpression(s, BinaryOperator.And, u)
        };

        var rewrittenFrom = new NamedTableSource(tid.ToQualifiedName(), rewrittenAlias);
        return node with
        {
            Projections = projections.AsReadOnly(),
            From = rewrittenFrom,
            Where = combinedWhere,
            GroupBy = groupBy,
            Having = having
        };
    }

    private void EnsureNoMaskedColumnsOnMetricView(TableCatalogEntry entry, IReadOnlyList<SelectItem> items, string clause)
    {
        for (int i = 0; i < items.Count; i++)
        {
            EnsureNoMaskedColumnsOnMetricView(entry, items[i], clause);
        }
    }

    private void EnsureNoMaskedColumnsOnMetricView(TableCatalogEntry entry, SqlNode? scope, string clause)
    {
        if (scope is null)
        {
            return;
        }

        AstReflection.Walk(scope, node =>
        {
            if (node is ColumnReference col && _typed.Masks.HasMask(entry.Identity, col.Name.SimpleName))
            {
                throw new MaskedColumnPredicateException($"Masked column '{col.Name.SimpleName}' of metric view '{entry.Identity}' must not be referenced in {clause}.");
            }

            return true;
        });
    }

    private void EnsureNoMaskedColumnsInMetricViewOrderBy(
        TableCatalogEntry entry,
        IReadOnlyList<SelectItem> projections,
        OrderByClause? orderBy)
    {
        if (orderBy is null)
        {
            return;
        }

        var selectAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in projections)
        {
            if (item is ColumnSelectItem { Alias.Value: { Length: > 0 } alias })
            {
                selectAliases.Add(alias);
            }
        }

        foreach (var el in orderBy.Elements)
        {
            AstReflection.Walk(el.Expression, node =>
            {
                if (node is ColumnReference col)
                {
                    if (col.Name.Parts.Count == 1 && selectAliases.Contains(col.Name.Parts[0].Value))
                    {
                        return true;
                    }

                    if (_typed.Masks.HasMask(entry.Identity, col.Name.SimpleName))
                    {
                        throw new MaskedColumnPredicateException($"Masked column '{col.Name.SimpleName}' of metric view '{entry.Identity}' must not be referenced in ORDER BY.");
                    }
                }

                return true;
            });
        }
    }

    private sealed class TargetAliasRewriter(SqlIdentifier targetAlias) : SqlAstRewriter
    {
        public TargetAliasRewriter()
            : this(new SqlIdentifier(RowFilterAliases.Target, IsQuoted: false))
        {
        }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            if (node.Name.Parts.Count >= 2 &&
                string.Equals(node.Name.Parts[^2].Value, RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase))
            {
                var updated = node.Name.Parts.ToArray();
                updated[^2] = targetAlias;
                return node with { Name = new SqlQualifiedName(updated) };
            }

            return base.VisitColumnReference(node);
        }
    }
}

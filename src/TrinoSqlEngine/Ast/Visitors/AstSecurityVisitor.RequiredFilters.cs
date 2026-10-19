namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

public sealed partial class AstSecurityVisitor : SqlAstRewriter
{
    private void EnsureRequiredFilterColumnsSatisfied(QuerySpecification node)
    {
        if (node.From == null)
            return;

        var tables = new List<(NamedTableSource Source, string TableName, string? Alias)>();
        CollectNamedTableSources(node.From, tables);

        if (tables.Count == 0)
            return;

        var onPredicates = new List<Expression>();
        CollectOnPredicates(node.From, onPredicates);

        foreach (var (source, tbl, alias) in tables)
        {
            if (IsCte(source.Name))
                continue;

            var entry = _typed.Catalog.Resolve(source.Name);
            if (entry == null || (entry.RequiredFilterColumns.Length == 0 && entry.RequiredFilterColumnGroups.Length == 0))
                continue;

            if (entry.RequiredFilterColumnGroups.Length > 0)
            {
                bool anyGroupSatisfied = false;
                foreach (var group in entry.RequiredFilterColumnGroups)
                {
                    if (group.Length == 0) continue;
                    bool groupAllSatisfied = true;
                    foreach (var reqCol in group)
                    {
                        if (!IsRequiredFilterSatisfied(entry, reqCol, tbl, alias, node.Where, onPredicates))
                        {
                            groupAllSatisfied = false;
                            break;
                        }
                    }
                    if (groupAllSatisfied)
                    {
                        anyGroupSatisfied = true;
                        break;
                    }
                }
                if (!anyGroupSatisfied)
                {
                    throw new RequiredFilterColumnMissingException(_typed.Capabilities.Dialect);
                }
            }
            else
            {
                foreach (var reqCol in entry.RequiredFilterColumns)
                {
                    if (IsRequiredFilterSatisfied(entry, reqCol, tbl, alias, node.Where, onPredicates))
                        continue;

                    throw new RequiredFilterColumnMissingException(_typed.Capabilities.Dialect);
                }
            }
        }
    }

    private static void CollectNamedTableSources(TableSource? from, List<(NamedTableSource Source, string TableName, string? Alias)> tables)
    {
        if (from == null) return;
        switch (from)
        {
            case NamedTableSource named:
                tables.Add((named, named.Name.NormalizedName, named.Alias?.Value));
                break;
            case JoinedTableSource joined:
                CollectNamedTableSources(joined.Left, tables);
                CollectNamedTableSources(joined.Right, tables);
                break;
            case LateralTableSource:
                // Subqueries are handled in their own QuerySpecification scope
                break;
            case SubqueryTableSource:
                // Subqueries are handled in their own QuerySpecification scope
                break;
        }
    }

    private static void CollectOnPredicates(TableSource? from, List<Expression> predicates)
    {
        if (from is JoinedTableSource j)
        {
            if (j.Condition is OnJoinCondition on)
            {
                predicates.Add(on.Predicate);
            }
            CollectOnPredicates(j.Left, predicates);
            CollectOnPredicates(j.Right, predicates);
        }
    }

    private bool IsRequiredFilterSatisfied(
        TableCatalogEntry entry,
        string reqCol,
        string tbl,
        string? alias,
        Expression? where,
        List<Expression> onPredicates)
    {
        // 1. Check active Tenant policy
        if (entry.TenantColumn != null &&
            string.Equals(entry.TenantColumn, reqCol, StringComparison.OrdinalIgnoreCase) &&
            _typed.BuildTenantPredicate(entry) != null)
        {
            return true;
        }

        // 2. Check active Row-Level Security policy
        if (_typed.RowFilters.ShouldApplyPolicy(entry.Identity))
        {
            var rlsPredicate = _typed.RowFilters.GetPredicate(entry.Identity);
            if (rlsPredicate.ReferencedColumns.Contains(reqCol.ToLowerInvariant()))
            {
                return true;
            }
        }

        // 3. Check WHERE clause
        if (where != null && IsColumnConstrained(where, tbl, alias, reqCol))
        {
            return true;
        }

        // 4. Check JOIN ON conditions
        foreach (var onPred in onPredicates)
        {
            if (IsColumnConstrained(onPred, tbl, alias, reqCol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsColumnConstrained(Expression expr, string tbl, string? alias, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        switch (expr)
        {
            case BinaryExpression b when b.Operator == BinaryOperator.And:
                return IsColumnConstrained(b.Left, tbl, alias, col) || IsColumnConstrained(b.Right, tbl, alias, col);

            case BinaryExpression b when b.Operator == BinaryOperator.Or:
                return IsColumnConstrained(b.Left, tbl, alias, col) && IsColumnConstrained(b.Right, tbl, alias, col);

            case BinaryExpression b when b.Operator is BinaryOperator.Equal
                                      or BinaryOperator.LessThan
                                      or BinaryOperator.LessThanOrEqual
                                      or BinaryOperator.GreaterThan
                                      or BinaryOperator.GreaterThanOrEqual:
                if (MatchesTargetColumn(b.Left, tbl, alias, col) && !ContainsColumnReference(b.Right))
                    return true;
                if (MatchesTargetColumn(b.Right, tbl, alias, col) && !ContainsColumnReference(b.Left))
                    return true;
                return false;

            case BetweenExpression bt when !bt.IsNotBetween:
                if (MatchesTargetColumn(bt.Operand, tbl, alias, col) &&
                    !ContainsColumnReference(bt.Lower) &&
                    !ContainsColumnReference(bt.Upper))
                {
                    return true;
                }
                return false;

            case InListExpression inList when !inList.IsNotIn && inList.Items.Count > 0:
                if (MatchesTargetColumn(inList.Operand, tbl, alias, col) &&
                    !inList.Items.Any(ContainsColumnReference))
                {
                    return true;
                }
                return false;

            case InSubqueryExpression inSq when !inSq.IsNotIn:
                if (MatchesTargetColumn(inSq.Operand, tbl, alias, col))
                {
                    return true;
                }
                return false;

            case LikeExpression lk when !lk.IsNotLike:
                if (MatchesTargetColumn(lk.Operand, tbl, alias, col) &&
                    !ContainsColumnReference(lk.Pattern) &&
                    (lk.Escape == null || !ContainsColumnReference(lk.Escape)))
                {
                    return true;
                }
                return false;

            case UnaryExpression u when u.Operator == UnaryOperator.IsNull:
                return MatchesTargetColumn(u.Operand, tbl, alias, col);

            default:
                return false;
        }
    }

    private static bool MatchesTargetColumn(Expression expr, string tbl, string? alias, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        if (expr is CastExpression cast)
        {
            return MatchesTargetColumn(cast.Operand, tbl, alias, col);
        }

        if (expr is ColumnReference cr)
        {
            return MatchesColumn(cr, tbl, alias, col);
        }

        return false;
    }

    private static bool MatchesColumn(ColumnReference colRef, string tableName, string? alias, string columnName)
    {
        var parts = colRef.Name.Parts;
        if (parts.Count == 0) return false;

        string refCol = parts[^1].Value;
        if (!string.Equals(refCol, columnName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (parts.Count == 1)
            return true;

        if (parts.Count == 2)
        {
            string qualifier = parts[0].Value;
            if (!string.IsNullOrEmpty(alias) && string.Equals(qualifier, alias, StringComparison.OrdinalIgnoreCase))
                return true;

            string simpleTable = SqlIdentifierHelper.GetSimpleName(tableName);
            return string.Equals(qualifier, simpleTable, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(qualifier, tableName, StringComparison.OrdinalIgnoreCase);
        }

        if (parts.Count == 3)
        {
            string schemaTable = $"{parts[0].Value}.{parts[1].Value}";
            return string.Equals(schemaTable, tableName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(parts[1].Value, SqlIdentifierHelper.GetSimpleName(tableName), StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

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

        var scope = new List<FromScopeEntry>();
        CollectFromScope(node.From, scope);

        if (!scope.Exists(static e => e.Source is NamedTableSource))
            return;

        var onPredicates = new List<Expression>();
        CollectOnPredicates(node.From, onPredicates);

        foreach (var target in scope.ToArray())
        {
            if (target.Source is not NamedTableSource source || IsCte(source.Name))
                continue;

            var entry = _typed.Catalog.Resolve(source.Name);
            if (entry == null || (entry.RequiredFilterColumns.Length == 0 && entry.RequiredFilterColumnGroups.Length == 0))
                continue;

            EnsureTableRequiredFiltersSatisfied(entry, target, scope, node.Where, onPredicates);
        }
    }

    private void EnsureDmlRequiredFiltersSatisfied(NamedTableSource targetTable, TableCatalogEntry entry, Expression? where)
    {
        if (entry.RequiredFilterColumns.Length == 0 && entry.RequiredFilterColumnGroups.Length == 0)
            return;

        var columns = new HashSet<string>(entry.Columns.Select(static c => c.Name), StringComparer.OrdinalIgnoreCase);
        var dmlTarget = new FromScopeEntry(
            targetTable,
            entry.Identity.ToQualifiedName().NormalizedName,
            targetTable.Alias?.Value ?? entry.Identity.Table,
            columns);
        EnsureTableRequiredFiltersSatisfied(entry, dmlTarget, [dmlTarget], where, Array.Empty<Expression>());
    }

    private void EnsureTableRequiredFiltersSatisfied(
        TableCatalogEntry entry,
        FromScopeEntry target,
        List<FromScopeEntry> scope,
        Expression? where,
        IReadOnlyList<Expression> onPredicates)
    {
        if (entry.RequiredFilterColumnGroups.Length > 0)
        {
            bool anyGroupSatisfied = false;
            foreach (var group in entry.RequiredFilterColumnGroups)
            {
                if (group.Length == 0) continue;
                bool groupAllSatisfied = true;
                foreach (var reqCol in group)
                {
                    if (!IsRequiredFilterSatisfied(entry, reqCol, target, scope, where, onPredicates))
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
                if (IsRequiredFilterSatisfied(entry, reqCol, target, scope, where, onPredicates))
                    continue;

                throw new RequiredFilterColumnMissingException(_typed.Capabilities.Dialect);
            }
        }
    }

    /// <summary>
    /// SQLE-08: one source of a FROM scope. <paramref name="ExposedName"/> is the only name a column can be qualified with (the alias,
    /// else the simple table name); <paramref name="OutputColumns"/> is null when the exposed columns cannot be known (fail closed).
    /// </summary>
    private readonly record struct FromScopeEntry(TableSource Source, string? TableName, string ExposedName, IReadOnlySet<string>? OutputColumns);

    private void CollectFromScope(TableSource? from, List<FromScopeEntry> scope)
    {
        if (from == null) return;
        switch (from)
        {
            case NamedTableSource named:
            {
                string tableName = named.Name.NormalizedName;
                string exposed = named.Alias?.Value ?? SqlIdentifierHelper.GetSimpleName(tableName);
                IReadOnlySet<string>? columns = null;
                if (!IsCte(named.Name) && _typed.Catalog.Resolve(named.Name) is { } catalogEntry)
                {
                    columns = new HashSet<string>(catalogEntry.Columns.Select(static c => c.Name), StringComparer.OrdinalIgnoreCase);
                }

                scope.Add(new FromScopeEntry(named, tableName, exposed, columns));
                break;
            }
            case JoinedTableSource joined:
                CollectFromScope(joined.Left, scope);
                CollectFromScope(joined.Right, scope);
                break;
            case LateralTableSource lateral:
                scope.Add(new FromScopeEntry(lateral, null, lateral.Alias.Value, DerivedOutputColumns(lateral.Subquery.Body, lateral.ColumnAliases)));
                break;
            case SubqueryTableSource subquery:
                scope.Add(new FromScopeEntry(subquery, null, subquery.Alias.Value, DerivedOutputColumns(subquery.Subquery.Body, subquery.ColumnAliases)));
                break;
        }
    }

    private static IReadOnlySet<string>? DerivedOutputColumns(QueryBody body, IReadOnlyList<SqlIdentifier>? columnAliases)
    {
        if (columnAliases is { Count: > 0 })
        {
            return new HashSet<string>(columnAliases.Select(static a => a.Value), StringComparer.OrdinalIgnoreCase);
        }

        switch (body)
        {
            case QuerySpecification spec:
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in spec.Projections)
                {
                    if (item is not ColumnSelectItem column)
                    {
                        return null;   // a wildcard exposes columns that are not known here
                    }

                    if (column.Alias != null)
                    {
                        names.Add(column.Alias.Value);
                    }
                    else if (column.Expression is ColumnReference reference)
                    {
                        names.Add(reference.Name.Parts[^1].Value);
                    }
                }

                return names;
            }
            case SetOperationQuery setOperation:
                return DerivedOutputColumns(setOperation.Left, null);
            default:
                return null;
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
        FromScopeEntry target,
        List<FromScopeEntry> scope,
        Expression? where,
        IReadOnlyList<Expression> onPredicates)
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
        if (where != null && IsColumnConstrained(where, target, scope, reqCol))
        {
            return true;
        }

        // 4. Check JOIN ON conditions
        foreach (var onPred in onPredicates)
        {
            if (IsColumnConstrained(onPred, target, scope, reqCol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsColumnConstrained(Expression expr, FromScopeEntry target, List<FromScopeEntry> scope, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        switch (expr)
        {
            case BinaryExpression b when b.Operator == BinaryOperator.And:
            {
                Expression cur = b;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.And } a)
                {
                    rights.Add(a.Right);
                    cur = a.Left;
                }

                if (IsColumnConstrained(cur, target, scope, col)) return true;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (IsColumnConstrained(rights[i], target, scope, col)) return true;
                }

                return false;
            }

            case BinaryExpression b when b.Operator == BinaryOperator.Or:
            {
                Expression cur = b;
                var rights = new List<Expression>();
                while (cur is BinaryExpression { Operator: BinaryOperator.Or } o)
                {
                    rights.Add(o.Right);
                    cur = o.Left;
                }

                if (!IsColumnConstrained(cur, target, scope, col)) return false;
                for (int i = rights.Count - 1; i >= 0; i--)
                {
                    if (!IsColumnConstrained(rights[i], target, scope, col)) return false;
                }

                return true;
            }

            case BinaryExpression b when b.Operator is BinaryOperator.Equal
                                      or BinaryOperator.LessThan
                                      or BinaryOperator.LessThanOrEqual
                                      or BinaryOperator.GreaterThan
                                      or BinaryOperator.GreaterThanOrEqual:
                if (MatchesTargetColumn(b.Left, target, scope, col) && !ContainsColumnReference(b.Right) && !IsNullLiteral(b.Right))
                    return true;
                if (MatchesTargetColumn(b.Right, target, scope, col) && !ContainsColumnReference(b.Left) && !IsNullLiteral(b.Left))
                    return true;
                return false;

            case BetweenExpression bt when !bt.IsNotBetween:
                if (MatchesTargetColumn(bt.Operand, target, scope, col) &&
                    !ContainsColumnReference(bt.Lower) && !IsNullLiteral(bt.Lower) &&
                    !ContainsColumnReference(bt.Upper) && !IsNullLiteral(bt.Upper))
                {
                    return true;
                }
                return false;

            case InListExpression inList when !inList.IsNotIn && inList.Items.Count > 0:
                if (MatchesTargetColumn(inList.Operand, target, scope, col) &&
                    !inList.Items.Any(ContainsColumnReference) &&
                    inList.Items.Any(static item => !IsNullLiteral(item)))
                {
                    return true;
                }
                return false;

            case InSubqueryExpression inSq when !inSq.IsNotIn:
                if (MatchesTargetColumn(inSq.Operand, target, scope, col))
                {
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    public static bool IsNullLiteral(Expression expr)
    {
        while (true)
        {
            switch (expr)
            {
                case ParenthesizedExpression p:
                    expr = p.Expression;
                    break;
                case CastExpression c:
                    expr = c.Operand;
                    break;
                default:
                    return expr is LiteralExpression { Type: LiteralType.Null };
            }
        }
    }

    /// <summary>
    /// Extracts catalog columns strictly constrained by a single-table Trino predicate expression using the AST rules of
    /// <see cref="IsColumnConstrained"/>. Fails closed (empty list) when blank or unparseable.
    /// </summary>
    public static IReadOnlyList<string> ExtractSingleTableConstrainedColumns(
        string? trinoPredicate,
        IEnumerable<string> catalogColumns)
    {
        ArgumentNullException.ThrowIfNull(catalogColumns);
        if (string.IsNullOrWhiteSpace(trinoPredicate))
        {
            return Array.Empty<string>();
        }

        var cols = catalogColumns as IReadOnlyList<string> ?? catalogColumns.ToList();
        if (cols.Count == 0)
        {
            return Array.Empty<string>();
        }

        Expression expr;
        try
        {
            var (tree, _) = FastSqlEngine.Default.ParseExpression(trinoPredicate.AsMemory(), SqlTokenSecurityOptions.Strict);
            expr = new Builder.SqlAstBuilder(new Builder.AstBuilderOptions
            {
                EnforceReadOnlyQueries = true,
                EnforceFunctionPolicy = true,
                AllowedFunctions = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.DuckDb),
                RejectTimeTravelQueries = true
            }).BuildStandaloneExpression(tree);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Array.Empty<string>();
        }

        var outputCols = new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        var target = new FromScopeEntry(
            new NamedTableSource(new SqlQualifiedName("single_table"), null),
            "single_table",
            "single_table",
            outputCols);
        var scope = new List<FromScopeEntry> { target };

        var constrained = new List<string>();
        foreach (var col in cols)
        {
            if (IsColumnConstrained(expr, target, scope, col))
            {
                constrained.Add(col);
            }
        }

        return constrained;
    }

    private static bool MatchesTargetColumn(Expression expr, FromScopeEntry target, List<FromScopeEntry> scope, string col)
    {
        while (expr is ParenthesizedExpression p)
        {
            expr = p.Expression;
        }

        if (expr is CastExpression cast)
        {
            return MatchesTargetColumn(cast.Operand, target, scope, col);
        }

        if (expr is ColumnReference cr)
        {
            return MatchesColumn(cr, target, scope, col);
        }

        return false;
    }

    private static bool MatchesColumn(ColumnReference colRef, FromScopeEntry target, List<FromScopeEntry> scope, string columnName)
    {
        var parts = colRef.Name.Parts;
        if (parts.Count == 0) return false;

        string refCol = parts[^1].Value;
        if (!string.Equals(refCol, columnName, StringComparison.OrdinalIgnoreCase))
            return false;

        // SQLE-08: every ambiguity counts as "not constrained" (fail closed).
        if (parts.Count == 1)
        {
            // Only when no other source of the scope may expose a column of that name.
            foreach (var other in scope)
            {
                if (ReferenceEquals(other.Source, target.Source)) continue;
                if (other.OutputColumns is null || other.OutputColumns.Contains(refCol)) return false;
            }

            return true;
        }

        if (parts.Count == 2)
        {
            // An aliased table is reachable only through its alias, and only when the exposed name is unique in the scope.
            if (!string.Equals(parts[0].Value, target.ExposedName, StringComparison.OrdinalIgnoreCase)) return false;
            foreach (var other in scope)
            {
                if (ReferenceEquals(other.Source, target.Source)) continue;
                if (string.Equals(other.ExposedName, target.ExposedName, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        if (parts.Count == 3)
        {
            // schema.table.column reaches only an unaliased table.
            if (target.Source is not NamedTableSource { Alias: null } || target.TableName is null) return false;
            string schemaTable = $"{parts[0].Value}.{parts[1].Value}";
            return string.Equals(schemaTable, target.TableName, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}

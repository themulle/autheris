namespace Autheris.Application.Sql.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

public sealed record MetricViewGuardResult(
    string? AugmentedSql,
    string? HiddenCountColumnAlias,
    int EffectiveMinGroupSize)
{
    public static readonly MetricViewGuardResult None = new(null, null, 0);
}

/// <summary>
/// Enforces D-MV-5, D-MV-7 and D-MV-8 for governed SQL statements targeting a Databricks metric view (fail-closed).
/// </summary>
public static class MetricViewStatementGuard
{
    public const string RefusalMessage = "The statement is not permitted on a metric view.";
    public const string HiddenCountAlias = "__autheris_mv_group_count";

    private static readonly FastSqlEngine ParserEngine = new();

    public static MetricViewGuardResult EnforceForSingleTable(
        string sql,
        TableMetadata table,
        TableAccessDecision decision,
        DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);

        var id = new TableIdentity(table.Identifier.Schema, table.Identifier.TableName);
        return Enforce(
            sql,
            new Dictionary<TableIdentity, TableMetadata> { [id] = table },
            new Dictionary<TableIdentity, TableAccessDecision> { [id] = decision },
            dialect);
    }

    public static MetricViewGuardResult Enforce(
        string sql,
        IReadOnlyDictionary<TableIdentity, TableMetadata> tables,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        DatabaseDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        var metricViews = tables.Values.Where(static t => t.Table.ObjectKind == DiscoveryObjectKind.MetricView).ToList();

        SqlStatement statement;
        try
        {
            var (tree, _) = ParserEngine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.Strict);
            statement = new SqlAstBuilder(new AstBuilderOptions()).BuildStatement(tree);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (metricViews.Count > 0)
            {
                throw Refuse();
            }

            return MetricViewGuardResult.None;
        }

        if (metricViews.Count == 0)
        {
            if (ContainsMeasureCall(statement))
            {
                throw Refuse();
            }

            return MetricViewGuardResult.None;
        }

        if (dialect != DatabaseDialect.Databricks || tables.Count != 1 || metricViews.Count != 1)
        {
            throw Refuse();
        }

        var mv = metricViews[0];
        var mvIdentity = new TableIdentity(mv.Identifier.Schema, mv.Identifier.TableName);
        if (!decisions.TryGetValue(mvIdentity, out var decision) || !decision.IsAllowed)
        {
            throw Refuse();
        }

        if (statement is not SelectStatement select || select.With is not null || select.Body is not QuerySpecification spec)
        {
            throw Refuse();
        }

        if (spec.From is not NamedTableSource)
        {
            throw Refuse();
        }

        var columnsByName = mv.Columns.ToDictionary(static c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        if (ContainsSubquery(spec))
        {
            throw Refuse();
        }

        // D-MV-7: row policy and tenant predicate columns must be exposed dimensions.
        ValidatePredicatesOnDimensions(mv, decision, columnsByName);

        // D-MV-8: minimum group size requires a declared measure column.
        int minGroupSize = mv.EffectiveMinGroupSize ?? 0;
        string? countMeasure = null;
        if (minGroupSize > 1)
        {
            if (string.IsNullOrWhiteSpace(mv.Table.MetricViewCountMeasure) ||
                !columnsByName.TryGetValue(mv.Table.MetricViewCountMeasure, out var countCol) ||
                countCol.Role != MetricViewColumnRole.Measure ||
                countCol.DiscoveryHidden)
            {
                throw Refuse();
            }

            countMeasure = countCol.ColumnName;
        }

        // Collect GROUP BY dimensions (unless GROUP BY ALL).
        bool groupByAll = spec.GroupBy?.IsAll == true;
        var groupedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (spec.GroupBy is not null && !groupByAll)
        {
            if (spec.GroupBy.AdvancedElements is { Count: > 0 })
            {
                throw Refuse();
            }

            foreach (var expr in spec.GroupBy.GroupingExpressions)
            {
                ValidateDimensionOnlyExpression(expr, columnsByName);
                CollectColumnNames(expr, groupedColumns);
            }
        }

        if (spec.Where is not null)
        {
            ValidateDimensionOnlyExpression(spec.Where, columnsByName);
        }

        if (spec.Projections.Count == 0)
        {
            throw Refuse();
        }

        var selectAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in spec.Projections)
        {
            if (item is WildcardSelectItem)
            {
                throw Refuse();
            }

            if (item is not ColumnSelectItem colItem)
            {
                throw Refuse();
            }

            if (colItem.Alias is not null && !string.IsNullOrWhiteSpace(colItem.Alias.Value))
            {
                selectAliases.Add(colItem.Alias.Value);
            }

            ValidateSelectExpression(colItem.Expression, columnsByName, groupByAll, groupedColumns, spec.GroupBy);
        }

        if (spec.Having is not null)
        {
            ValidateHavingOrOrderExpression(spec.Having, columnsByName, selectAliases, allowAliases: false);
        }

        if (select.OrderBy?.Elements is { Count: > 0 } orderElements)
        {
            foreach (var order in orderElements)
            {
                ValidateHavingOrOrderExpression(order.Expression, columnsByName, selectAliases, allowAliases: true);
            }
        }

        if (countMeasure is not null)
        {
            var augmented = InjectHiddenCountMeasure(sql, countMeasure, HiddenCountAlias);
            return new MetricViewGuardResult(augmented, HiddenCountAlias, minGroupSize);
        }

        return MetricViewGuardResult.None;
    }

    private static void ValidatePredicatesOnDimensions(
        TableMetadata mv,
        TableAccessDecision decision,
        IReadOnlyDictionary<string, TableColumn> columnsByName)
    {
        if (!string.IsNullOrWhiteSpace(mv.TenantColumnName) && !IsExposedDimension(mv.TenantColumnName, columnsByName))
        {
            throw Refuse();
        }

        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            try
            {
                var (tree, _) = ParserEngine.Parse($"SELECT 1 FROM t WHERE {decision.CombinedRowFilterSql}".AsMemory(), SqlTokenSecurityOptions.Strict);
                var stmt = (SelectStatement)new SqlAstBuilder(new AstBuilderOptions()).BuildStatement(tree);
                var where = ((QuerySpecification)stmt.Body).Where!;
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectColumnNames(where, referenced);
                foreach (var col in referenced)
                {
                    if (!IsExposedDimension(col, columnsByName))
                    {
                        throw Refuse();
                    }
                }
            }
            catch (WebSqlPolicyException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw Refuse();
            }
        }
    }

    private static bool IsExposedDimension(string columnName, IReadOnlyDictionary<string, TableColumn> columnsByName) =>
        columnsByName.TryGetValue(columnName, out var col) &&
        !col.DiscoveryHidden &&
        col.Role == MetricViewColumnRole.Dimension;

    private static void ValidateSelectExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        bool groupByAll,
        IReadOnlySet<string> groupedColumns,
        GroupByClause? groupBy)
    {
        if (ContainsMeasureCall(expr))
        {
            ValidateMeasureExpression(expr, columnsByName);
            return;
        }

        ValidateDimensionOnlyExpression(expr, columnsByName);

        if (!groupByAll)
        {
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectColumnNames(expr, cols);
            if (cols.Count > 0 && (groupBy is null || !cols.All(c => groupedColumns.Contains(c))))
            {
                throw Refuse();
            }
        }
    }

    private static void ValidateMeasureExpression(Expression expr, IReadOnlyDictionary<string, TableColumn> columnsByName)
    {
        switch (expr)
        {
            case FunctionCallExpression fn when IsMeasureFunction(fn):
                ValidateMeasureCall(fn, columnsByName);
                return;
            case CastExpression cast:
                ValidateMeasureExpression(cast.Operand, columnsByName);
                return;
            case ParenthesizedExpression paren:
                ValidateMeasureExpression(paren.Expression, columnsByName);
                return;
            case BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide } bin:
                ValidateMeasureOperand(bin.Left, columnsByName);
                ValidateMeasureOperand(bin.Right, columnsByName);
                return;
            case UnaryExpression { Operator: UnaryOperator.Negate } un:
                ValidateMeasureExpression(un.Operand, columnsByName);
                return;
            default:
                throw Refuse();
        }
    }

    private static void ValidateMeasureOperand(Expression expr, IReadOnlyDictionary<string, TableColumn> columnsByName)
    {
        if (expr is LiteralExpression)
        {
            return;
        }

        ValidateMeasureExpression(expr, columnsByName);
    }

    private static void ValidateHavingOrOrderExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        IReadOnlySet<string> selectAliases,
        bool allowAliases)
    {
        switch (expr)
        {
            case FunctionCallExpression fn when IsMeasureFunction(fn):
                ValidateMeasureCall(fn, columnsByName);
                return;
            case ColumnReference colRef:
                var name = colRef.Name.Parts[^1].Value;
                if (allowAliases && selectAliases.Contains(name))
                {
                    return;
                }

                if (!IsExposedDimension(name, columnsByName))
                {
                    throw Refuse();
                }

                return;
            case LiteralExpression or ParameterReference or NamedBindMarkerExpression:
                return;
            case ParenthesizedExpression p:
                ValidateHavingOrOrderExpression(p.Expression, columnsByName, selectAliases, allowAliases);
                return;
            case CastExpression c:
                ValidateHavingOrOrderExpression(c.Operand, columnsByName, selectAliases, allowAliases);
                return;
            case BinaryExpression b:
                ValidateHavingOrOrderExpression(b.Left, columnsByName, selectAliases, allowAliases);
                ValidateHavingOrOrderExpression(b.Right, columnsByName, selectAliases, allowAliases);
                return;
            case UnaryExpression u:
                ValidateHavingOrOrderExpression(u.Operand, columnsByName, selectAliases, allowAliases);
                return;
            case BetweenExpression bw:
                ValidateHavingOrOrderExpression(bw.Operand, columnsByName, selectAliases, allowAliases);
                ValidateHavingOrOrderExpression(bw.Lower, columnsByName, selectAliases, allowAliases);
                ValidateHavingOrOrderExpression(bw.Upper, columnsByName, selectAliases, allowAliases);
                return;
            default:
                throw Refuse();
        }
    }

    private static void ValidateDimensionOnlyExpression(Expression expr, IReadOnlyDictionary<string, TableColumn> columnsByName)
    {
        if (ContainsMeasureCall(expr))
        {
            throw Refuse();
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectColumnNames(expr, referenced);
        foreach (var col in referenced)
        {
            if (!IsExposedDimension(col, columnsByName))
            {
                throw Refuse();
            }
        }
    }

    private static void ValidateMeasureCall(FunctionCallExpression fn, IReadOnlyDictionary<string, TableColumn> columnsByName)
    {
        if (fn.Window is not null || fn.Filter is not null || fn.Distinct || fn.IsStar || fn.Arguments.Count != 1)
        {
            throw Refuse();
        }

        if (fn.Arguments[0] is not ColumnReference colRef)
        {
            throw Refuse();
        }

        var name = colRef.Name.Parts[^1].Value;
        if (!columnsByName.TryGetValue(name, out var col) || col.DiscoveryHidden || col.Role != MetricViewColumnRole.Measure)
        {
            throw Refuse();
        }
    }

    private static bool IsMeasureFunction(FunctionCallExpression fn) =>
        fn.Name.Parts.Count == 1 && fn.Name.Parts[0].Value.Equals("measure", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsMeasureCall(SqlNode node)
    {
        bool found = false;
        AstReflection.Walk(node, n =>
        {
            if (n is FunctionCallExpression fn && IsMeasureFunction(fn))
            {
                found = true;
                return false;
            }

            return true;
        });
        return found;
    }

    private static bool ContainsSubquery(SqlNode node)
    {
        bool found = false;
        AstReflection.Walk(node, n =>
        {
            if (n is SubqueryTableSource or LateralTableSource or InSubqueryExpression or ExistsExpression or ScalarSubqueryExpression or QuantifiedComparisonExpression)
            {
                found = true;
                return false;
            }

            return true;
        });
        return found;
    }

    private static void CollectColumnNames(SqlNode node, HashSet<string> names)
    {
        AstReflection.Walk(node, n =>
        {
            if (n is ColumnReference col && col.Name.Parts.Count > 0)
            {
                names.Add(col.Name.Parts[^1].Value);
            }

            return true;
        });
    }

    private static string InjectHiddenCountMeasure(string sql, string countMeasure, string alias)
    {
        var span = sql.AsSpan();
        bool inSingle = false, inDouble = false, inBacktick = false;
        int depth = 0;

        for (int i = 0; i <= span.Length - 4; i++)
        {
            char ch = span[i];
            if (ch == '\'' && !inDouble && !inBacktick) { inSingle = !inSingle; continue; }
            if (ch == '"' && !inSingle && !inBacktick) { inDouble = !inDouble; continue; }
            if (ch == '`' && !inSingle && !inDouble) { inBacktick = !inBacktick; continue; }
            if (inSingle || inDouble || inBacktick) continue;

            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }

            if (depth == 0 &&
                (i == 0 || char.IsWhiteSpace(span[i - 1])) &&
                span.Slice(i, 4).Equals("FROM", StringComparison.OrdinalIgnoreCase) &&
                (i + 4 == span.Length || char.IsWhiteSpace(span[i + 4])))
            {
                return string.Concat(span[..i], $", MEASURE({countMeasure}) AS {alias} ", span[i..]);
            }
        }

        return sql;
    }

    private static WebSqlPolicyException Refuse() =>
        new(RefusalMessage, GovernedSqlErrorCodes.PolicyViolation);
}

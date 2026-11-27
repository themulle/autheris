namespace Autheris.Application.Sql.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime.Tree;
using Autheris.Application.Analytics;
using Autheris.Application.Sql.Policy;
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
using TrinoSqlEngine.Parsing;

public sealed record MetricViewGuardResult(
    string? AugmentedSql,
    string? HiddenCountColumnAlias,
    int EffectiveMinGroupSize,
    string? InternalCountMeasureColumn = null)
{
    public static readonly MetricViewGuardResult None = new(null, null, 0, null);
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
        DatabaseDialect dialect,
        int minGroupSize,
        bool restrictNegatedFilters)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(decision);

        var id = new TableIdentity(table.Identifier.Schema, table.Identifier.TableName);
        return Enforce(
            sql,
            new Dictionary<TableIdentity, TableMetadata> { [id] = table },
            new Dictionary<TableIdentity, TableAccessDecision> { [id] = decision },
            dialect,
            minGroupSize,
            restrictNegatedFilters);
    }

    public static MetricViewGuardResult Enforce(
        string sql,
        IReadOnlyDictionary<TableIdentity, TableMetadata> tables,
        IReadOnlyDictionary<TableIdentity, TableAccessDecision> decisions,
        DatabaseDialect dialect,
        int minGroupSize,
        bool restrictNegatedFilters)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        var metricViews = tables.Values.Where(static t => t.Table.ObjectKind == DiscoveryObjectKind.MetricView).ToList();

        SqlBaseParser.SingleStatementContext tree;
        SqlStatement statement;
        try
        {
            (tree, _) = ParserEngine.Parse(sql.AsMemory(), ClientSqlTokenPolicy.Analysis);
            var targetDialect = SqlDialectMapper.TryToTargetDialect(dialect, out var mapped)
                ? mapped
                : TargetSqlDialect.DuckDb;
            statement = new SqlAstBuilder(new AstBuilderOptions
            {
                EnforceFunctionPolicy = true,
                AllowedFunctions = SqlFunctionAllowlists.WithDateTranslation(
                    SqlFunctionAllowlists.Build(targetDialect, ["measure"]))
            }).BuildStatement(tree);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (metricViews.Count > 0 || sql.Contains("measure", StringComparison.OrdinalIgnoreCase))
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

        // D-MV-8: minimum group size requires a declared exposed non-masked measure column.
        int effectiveK = Math.Max(minGroupSize, mv.EffectiveMinGroupSize ?? 0);
        string? countMeasure = null;
        if (effectiveK > 1)
        {
            if (string.IsNullOrWhiteSpace(mv.Table.MetricViewCountMeasure) ||
                !columnsByName.TryGetValue(mv.Table.MetricViewCountMeasure, out var countCol) ||
                countCol.Role != MetricViewColumnRole.Measure ||
                countCol.DiscoveryHidden ||
                decision.GetEffectiveColumnAccess(countCol.ColumnName, mv) == ColumnAccessLevel.Mask)
            {
                throw Refuse();
            }

            countMeasure = countCol.ColumnName;
        }

        // Collect GROUP BY dimensions (unless GROUP BY ALL).
        bool groupByAll = spec.GroupBy?.IsAll == true;
        var groupedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bareGroupedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool negationCheck = effectiveK > 1 && restrictNegatedFilters;
        if (spec.GroupBy is not null && !groupByAll)
        {
            if (spec.GroupBy.AdvancedElements is { Count: > 0 })
            {
                throw Refuse();
            }

            foreach (var expr in spec.GroupBy.GroupingExpressions)
            {
                ValidateDimensionOnlyExpression(expr, columnsByName, decision, mv);
                CollectColumnNames(expr, groupedColumns);
                if (expr is ColumnReference { Name.Parts.Count: > 0 } bare)
                {
                    bareGroupedColumns.Add(bare.Name.Parts[^1].Value);
                }
            }
        }

        if (spec.Where is not null)
        {
            ValidateDimensionOnlyExpression(spec.Where, columnsByName, decision, mv);
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

            ValidateSelectExpression(colItem.Expression, columnsByName, decision, mv, groupByAll, groupedColumns, spec.GroupBy);

            if (groupByAll && !ContainsMeasureCall(colItem.Expression) &&
                colItem.Expression is ColumnReference { Name.Parts.Count: > 0 } projected)
            {
                bareGroupedColumns.Add(projected.Name.Parts[^1].Value);
            }
        }

        if (countMeasure is not null && selectAliases.Contains(HiddenCountAlias))
        {
            throw Refuse();
        }

        if (negationCheck && spec.Where is not null)
        {
            var whereText = ExtractWhereText(sql, tree);
            if (AggregationNegationGuard.RejectsPredicate(whereText, bareGroupedColumns, mv, knownColumnsOnly: true))
            {
                throw Refuse();
            }
        }

        if (spec.Having is not null)
        {
            ValidateHavingOrOrderExpression(
                spec.Having,
                columnsByName,
                decision,
                mv,
                selectAliases,
                allowAliases: false,
                groupedDimensions: negationCheck ? bareGroupedColumns : null);
        }

        if (select.OrderBy?.Elements is { Count: > 0 } orderElements)
        {
            foreach (var order in orderElements)
            {
                ValidateHavingOrOrderExpression(order.Expression, columnsByName, decision, mv, selectAliases, allowAliases: true);
            }
        }

        if (countMeasure is not null)
        {
            var augmented = InjectHiddenCountMeasure(sql, tree, countMeasure, HiddenCountAlias);
            return new MetricViewGuardResult(augmented, HiddenCountAlias, effectiveK, countMeasure);
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

        if (!decision.HasRowFilter)
        {
            return;
        }

        try
        {
            var policyFunctions = SqlFunctionAllowlists.BuildForPolicy(TargetSqlDialect.Databricks, null);
            if (!DecisionRowFilter.TryResolveExpression(decision, policyFunctions, out var expr, ParserEngine))
            {
                return;
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
        catch (WebSqlPolicyException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Refuse();
        }
    }

    private static bool IsExposedDimension(string columnName, IReadOnlyDictionary<string, TableColumn> columnsByName) =>
        columnsByName.TryGetValue(columnName, out var col) &&
        !col.DiscoveryHidden &&
        col.Role == MetricViewColumnRole.Dimension;

    private static bool IsExposedClearDimension(
        string columnName,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv) =>
        columnsByName.TryGetValue(columnName, out var col) &&
        !col.DiscoveryHidden &&
        col.Role == MetricViewColumnRole.Dimension &&
        decision.GetEffectiveColumnAccess(col.ColumnName, mv) == ColumnAccessLevel.Clear;

    private static void ValidateSelectExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv,
        bool groupByAll,
        IReadOnlySet<string> groupedColumns,
        GroupByClause? groupBy)
    {
        if (ContainsMeasureCall(expr))
        {
            ValidateMeasureExpression(expr, columnsByName, decision, mv);
            return;
        }

        ValidateDimensionOnlyExpression(expr, columnsByName, decision, mv);

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

    private static void ValidateMeasureExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv)
    {
        switch (expr)
        {
            case FunctionCallExpression fn when IsMeasureFunction(fn):
                ValidateMeasureCall(fn, columnsByName, decision, mv);
                return;
            case CastExpression cast:
                ValidateMeasureExpression(cast.Operand, columnsByName, decision, mv);
                return;
            case ParenthesizedExpression paren:
                ValidateMeasureExpression(paren.Expression, columnsByName, decision, mv);
                return;
            case BinaryExpression { Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.Divide } bin:
                ValidateMeasureOperand(bin.Left, columnsByName, decision, mv);
                ValidateMeasureOperand(bin.Right, columnsByName, decision, mv);
                return;
            case UnaryExpression { Operator: UnaryOperator.Negate } un:
                ValidateMeasureExpression(un.Operand, columnsByName, decision, mv);
                return;
            default:
                throw Refuse();
        }
    }

    private static void ValidateMeasureOperand(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv)
    {
        if (expr is LiteralExpression)
        {
            return;
        }

        ValidateMeasureExpression(expr, columnsByName, decision, mv);
    }

    private static void ValidateHavingOrOrderExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv,
        IReadOnlySet<string> selectAliases,
        bool allowAliases,
        IReadOnlySet<string>? groupedDimensions = null)
    {
        switch (expr)
        {
            case FunctionCallExpression fn when IsMeasureFunction(fn):
                ValidateMeasureCall(fn, columnsByName, decision, mv);
                return;
            case ColumnReference colRef:
                var name = colRef.Name.Parts[^1].Value;
                if (allowAliases && selectAliases.Contains(name))
                {
                    return;
                }

                if (!IsExposedClearDimension(name, columnsByName, decision, mv) ||
                    (groupedDimensions is not null && !groupedDimensions.Contains(name)))
                {
                    throw Refuse();
                }

                return;
            case LiteralExpression or ParameterReference or NamedBindMarkerExpression:
                return;
            case ParenthesizedExpression p:
                ValidateHavingOrOrderExpression(p.Expression, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                return;
            case CastExpression c:
                ValidateHavingOrOrderExpression(c.Operand, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                return;
            case BinaryExpression b:
                ValidateHavingOrOrderExpression(b.Left, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                ValidateHavingOrOrderExpression(b.Right, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                return;
            case UnaryExpression u:
                ValidateHavingOrOrderExpression(u.Operand, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                return;
            case BetweenExpression bw:
                ValidateHavingOrOrderExpression(bw.Operand, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                ValidateHavingOrOrderExpression(bw.Lower, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                ValidateHavingOrOrderExpression(bw.Upper, columnsByName, decision, mv, selectAliases, allowAliases, groupedDimensions);
                return;
            default:
                throw Refuse();
        }
    }

    private static void ValidateDimensionOnlyExpression(
        Expression expr,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv)
    {
        if (ContainsMeasureCall(expr))
        {
            throw Refuse();
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectColumnNames(expr, referenced);
        foreach (var col in referenced)
        {
            if (!IsExposedClearDimension(col, columnsByName, decision, mv))
            {
                throw Refuse();
            }
        }
    }

    private static void ValidateMeasureCall(
        FunctionCallExpression fn,
        IReadOnlyDictionary<string, TableColumn> columnsByName,
        TableAccessDecision decision,
        TableMetadata mv)
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
        if (!columnsByName.TryGetValue(name, out var col) ||
            col.DiscoveryHidden ||
            col.Role != MetricViewColumnRole.Measure ||
            decision.GetEffectiveColumnAccess(col.ColumnName, mv) != ColumnAccessLevel.Clear)
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

    private static string InjectHiddenCountMeasure(
        string sql,
        SqlBaseParser.SingleStatementContext tree,
        string countMeasure,
        string alias)
    {
        var querySpec = FindFirstQuerySpecification(tree);
        var selectItems = querySpec?.selectItem();
        if (selectItems is null || selectItems.Length == 0 || selectItems[^1].Stop is not { } lastStop)
        {
            throw Refuse();
        }

        int insertPos = lastStop.StopIndex + 1;
        if (insertPos < 0 || insertPos > sql.Length)
        {
            throw Refuse();
        }

        var quotedMeasure = SqlIdentifierHelper.QuoteIdentifier(countMeasure);
        return string.Concat(sql.AsSpan(0, insertPos), $", MEASURE({quotedMeasure}) AS {alias} ", sql.AsSpan(insertPos));
    }

    private static string ExtractWhereText(string sql, SqlBaseParser.SingleStatementContext tree)
    {
        var where = FindFirstQuerySpecification(tree)?.where;
        if (where?.Start is not { } start || where.Stop is not { } stop ||
            start.StartIndex < 0 || stop.StopIndex < start.StartIndex || stop.StopIndex >= sql.Length)
        {
            throw Refuse();
        }

        return sql.Substring(start.StartIndex, stop.StopIndex - start.StartIndex + 1);
    }

    private static SqlBaseParser.QuerySpecificationContext? FindFirstQuerySpecification(IParseTree node)
    {
        if (node is SqlBaseParser.QuerySpecificationContext qs)
        {
            return qs;
        }

        for (int i = 0; i < node.ChildCount; i++)
        {
            var found = FindFirstQuerySpecification(node.GetChild(i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static WebSqlPolicyException Refuse() =>
        new(RefusalMessage, GovernedSqlErrorCodes.PolicyViolation);
}

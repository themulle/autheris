using System.Globalization;
using System.Text;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree;

public static partial class TreeSqlCompiler
{
    private static string DatabricksMaskExpression(MaskingRule rule, CompileContext context, bool isString)
    {
        if (!isString || string.Equals(rule.RuleType, "NULLIFY", StringComparison.OrdinalIgnoreCase))
        {
            return "CAST(NULL AS STRING)";
        }

        var text = !string.IsNullOrWhiteSpace(rule.Replacement) ? rule.Replacement : "***";
        return $"CAST({context.AddParameter(text)} AS STRING)";
    }

    private static string DatabricksObject(NodePlan plan, CompileContext context, string? rowAlias = null) =>
        BuildDatabricksStruct(ObjectPairs(plan, context, rowAlias), context.Dialect);

    private static string BuildDatabricksStruct(IEnumerable<(string Key, string Expression)> pairs, DatabaseDialect dialect)
    {
        var args = new List<string>();
        foreach (var (key, expr) in pairs)
        {
            if (!SqlIdentifierGuard.IsValidIdentifier(key))
            {
                throw new GatewayInvalidQueryException("Invalid property name.");
            }

            args.Add($"'{dialect.EscapeSqlLiteral(key)}', {expr}");
        }

        return args.Count == 0 ? "named_struct()" : $"named_struct({string.Join(", ", args)})";
    }

    private static string BuildDatabricksCorrelatedRoot(NodePlan plan, CompileContext context)
    {
        var inner = BuildInner(plan, context, parent: null, relation: null);
        var alias = Q(context, plan.Alias);
        if (plan.Children.Count == 0)
        {
            var directArr = BuildDatabricksCollectArray(
                DatabricksObject(plan, context),
                alias,
                plan.Order,
                limit: null,
                context);
            return $"SELECT coalesce(to_json({directArr}, map('ignoreNullFields', 'false')), '[]') FROM ({inner}) AS {alias}";
        }

        return BuildDatabricksProjectedRoot(
            fromClause: $"({inner}) AS {alias}",
            rowAlias: alias,
            structExpr: DatabricksObject(plan, context),
            order: plan.Order,
            context);
    }

    private static string BuildDatabricksProjectedRoot(
        string fromClause,
        string rowAlias,
        string structExpr,
        IReadOnlyList<(string Column, bool Descending)> order,
        CompileContext context)
    {
        var (projSql, aggAlias, arrExpr) = BuildDatabricksProjectedCollect(
            fromClause,
            rowAlias,
            structExpr,
            order,
            limit: null,
            context);
        return $"SELECT coalesce(to_json({arrExpr}, map('ignoreNullFields', 'false')), '[]') FROM ({projSql}) AS {aggAlias}";
    }

    private static string BuildDatabricksChild(
        NodePlan plan,
        CompileContext context,
        NodePlan parent,
        TreeRelationNode relation)
    {
        var target = Q(context, TargetAlias);
        var fromSource = plan.Access.SecuredSource is { } securedSource
            ? $"({BindSecuredSource(context, securedSource)})"
            : context.Dialect.FormatTableIdentifier(plan.Access.Metadata.Identifier);
        var where = BuildInnerPredicates(plan, context, parent, relation, correlate: true);
        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty;
        int limit = relation.IsList ? (plan.Node.ExecutionLimit ?? plan.Node.Limit) : 1;

        if (plan.Children.Count == 0)
        {
            var leafArr = BuildDatabricksCollectArray(
                DatabricksObject(plan, context, TargetAlias),
                target,
                plan.Order,
                limit,
                context);
            return relation.IsList
                ? $"(SELECT coalesce({leafArr}, array()) FROM {fromSource} AS {target}{whereSql})"
                : $"(SELECT try_element_at({leafArr}, 1) FROM {fromSource} AS {target}{whereSql})";
        }

        var innerSql = $"SELECT {string.Join(", ", plan.InnerColumns.Select(c => $"{target}.{Q(context, c)} AS {Q(context, c)}"))} FROM {fromSource} AS {target}{whereSql}";
        var alias = Q(context, plan.Alias);
        var (projSql, aggAlias, arrExpr) = BuildDatabricksProjectedCollect(
            $"({innerSql}) AS {alias}",
            alias,
            DatabricksObject(plan, context),
            plan.Order,
            limit,
            context);

        return relation.IsList
            ? $"(SELECT coalesce({arrExpr}, array()) FROM ({projSql}) AS {aggAlias})"
            : $"(SELECT try_element_at({arrExpr}, 1) FROM ({projSql}) AS {aggAlias})";
    }

    private static (string ProjSql, string AggAlias, string ArrExpr) BuildDatabricksProjectedCollect(
        string fromClause,
        string rowAlias,
        string structExpr,
        IReadOnlyList<(string Column, bool Descending)> order,
        int? limit,
        CompileContext context)
    {
        var aggRaw = context.NextAlias();
        var aggAlias = Q(context, aggRaw);
        var vCol = Q(context, "__v");
        var projCols = new List<string>(order.Count + 1);
        for (int i = 0; i < order.Count; i++)
        {
            projCols.Add($"{rowAlias}.{Q(context, order[i].Column)} AS {Q(context, "__o" + i.ToString(CultureInfo.InvariantCulture))}");
        }

        projCols.Add($"{structExpr} AS {vCol}");
        var projSql = $"SELECT {string.Join(", ", projCols)} FROM {fromClause}";

        var orderProjected = order
            .Select((o, i) => ("__o" + i.ToString(CultureInfo.InvariantCulture), o.Descending))
            .ToList();
        var arrExpr = BuildDatabricksCollectArray($"{aggAlias}.{vCol}", aggAlias, orderProjected, limit, context);
        return (projSql, aggAlias, arrExpr);
    }

    private static string BuildDatabricksCollectArray(
        string structExpr,
        string rowAlias,
        IReadOnlyList<(string Column, bool Descending)> order,
        int? limit,
        CompileContext context)
    {
        if (limit == 0)
        {
            return $"slice(collect_list({structExpr}), 1, 0)";
        }

        if (order.Count == 0)
        {
            var listExpr = $"collect_list({structExpr})";
            return limit is { } cap
                ? $"slice({listExpr}, 1, {cap.ToString(CultureInfo.InvariantCulture)})"
                : listExpr;
        }

        var fields = new StringBuilder();
        for (int i = 0; i < order.Count; i++)
        {
            var key = "__o" + i.ToString(CultureInfo.InvariantCulture);
            fields.Append('\'').Append(key).Append("', ").Append(rowAlias).Append('.').Append(Q(context, order[i].Column)).Append(", ");
        }

        fields.Append("'__v', ").Append(structExpr);
        var sorted = $"array_sort(collect_list(named_struct({fields})), (l, r) -> {BuildDatabricksComparator(order, context)})";
        if (limit is { } maxItems)
        {
            sorted = $"slice({sorted}, 1, {maxItems.ToString(CultureInfo.InvariantCulture)})";
        }

        return $"transform({sorted}, x -> x.{Q(context, "__v")})";
    }

    private static string BuildDatabricksComparator(IReadOnlyList<(string Column, bool Descending)> order, CompileContext context)
    {
        var expr = "0";
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var fk = Q(context, "__o" + i.ToString(CultureInfo.InvariantCulture));
            bool desc = order[i].Descending;
            var nullL = desc ? "1" : "-1";
            var nullR = desc ? "-1" : "1";
            var opLt = desc ? ">" : "<";
            var opGt = desc ? "<" : ">";
            expr = $"CASE WHEN l.{fk} IS NULL AND r.{fk} IS NULL THEN {expr} " +
                   $"WHEN l.{fk} IS NULL THEN {nullL} WHEN r.{fk} IS NULL THEN {nullR} " +
                   $"WHEN l.{fk} {opLt} r.{fk} THEN -1 WHEN l.{fk} {opGt} r.{fk} THEN 1 ELSE {expr} END";
        }

        return expr;
    }
}

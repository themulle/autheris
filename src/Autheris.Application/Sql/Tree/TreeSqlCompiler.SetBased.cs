using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autheris.Application.Sql.Indexing;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree;

public static partial class TreeSqlCompiler
{
    private static void PlanListStrategies(
        NodePlan plan,
        CompileContext context,
        bool isRoot,
        IReadOnlyList<string> path)
    {
        foreach (var (relation, child) in plan.Children)
        {
            var childPath = path.Concat([relation.ResponseKey]).ToArray();
            if (relation.IsList)
            {
                var (strategy, reason) = EvaluateListStrategy(child, relation, plan, isRoot, context);
                plan.ListStrategies[relation] = (strategy, reason);
                context.Lists.Add(new TreeListPlan(childPath, strategy, reason, child.Node.ExecutionLimit ?? child.Node.Limit));
                GatewayDiagnostics.TreeNestedListStrategy.Add(1,
                    new KeyValuePair<string, object?>("dialect", context.Dialect.ToString().ToLowerInvariant()),
                    new KeyValuePair<string, object?>("strategy", strategy.ToString()),
                    new KeyValuePair<string, object?>("reason", reason ?? "none"));
            }

            PlanListStrategies(child, context, isRoot: false, childPath);
        }
    }

    private static (TreeListStrategy Strategy, string? Reason) EvaluateListStrategy(
        NodePlan childPlan,
        TreeRelationNode relation,
        NodePlan parentPlan,
        bool isRoot,
        CompileContext context)
    {
        if (!isRoot)
        {
            return (TreeListStrategy.CorrelatedUnindexed, "NotRootChild");
        }

        if (childPlan.Node.OrderBy.Count == 0)
        {
            return (TreeListStrategy.CorrelatedUnindexed, "DefaultOrder");
        }

        var equalityColumns = ExtractEqualityColumns(childPlan.Node.Where);
        bool indexMatched = NestedListIndexMatcher.Matches(
            childPlan.Access.Metadata,
            relation.ChildColumns,
            equalityColumns,
            childPlan.Order);

        if (indexMatched)
        {
            return (TreeListStrategy.CorrelatedIndexed, "IndexMatched");
        }

        if (!SupportsTreeCompile(context.Dialect))
        {
            return (TreeListStrategy.CorrelatedUnindexed, "DialectNotSupported");
        }

        var clearUniqueKey = GetClearUniqueKeyColumns(parentPlan);
        if (clearUniqueKey == null)
        {
            return (TreeListStrategy.CorrelatedUnindexed, "ParentOrderNotTotal");
        }

        if (childPlan.InnerColumns.Any(CollidesWithReservedName))
        {
            return (TreeListStrategy.CorrelatedUnindexed, "ReservedNameCollision");
        }

        long parentLimit = parentPlan.Node.ExecutionLimit ?? parentPlan.Node.Limit;
        long childLimit = childPlan.Node.ExecutionLimit ?? childPlan.Node.Limit;
        if (parentLimit * childLimit > context.Options.MaxSetBasedRows)
        {
            return (TreeListStrategy.CorrelatedUnindexed, "RowBudgetExceeded");
        }

        return (TreeListStrategy.SetBased, null);
    }

    private static HashSet<string> ExtractEqualityColumns(TreeFilter? filter)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filter is null) return result;

        if (filter is TreeComparison { Operator: TreeFilterOperator.Eq, Value: not null } comp)
        {
            result.Add(comp.Column);
        }
        else if (filter is TreeAndFilter andFilter)
        {
            foreach (var item in andFilter.Items)
            {
                if (item is TreeComparison { Operator: TreeFilterOperator.Eq, Value: not null } c)
                {
                    result.Add(c.Column);
                }
            }
        }

        return result;
    }

    /// <summary>The primary key, else the first unique index, whose columns are all effectively Clear (see <see cref="ResolveClearKey"/>).</summary>
    private static List<string>? GetClearUniqueKeyColumns(NodePlan plan)
    {
        var meta = plan.Access.Metadata;
        var decision = plan.Access.Decision;

        if (ResolveClearKey(meta, decision, meta.PrimaryKeyColumns) is { } primaryKey)
        {
            return primaryKey;
        }

        foreach (var idx in meta.Indexes)
        {
            if (idx.IsUnique && ResolveClearKey(meta, decision, idx.Columns) is { } uniqueKey)
            {
                return uniqueKey;
            }
        }

        return null;
    }

    /// <summary>
    /// #836: the key columns in catalog spelling (the SQL projects and orders by that name), with the access checked on that
    /// name. An empty key, or a column that is unknown, not a safe name, or not effectively Clear, disqualifies the key (null).
    /// </summary>
    private static List<string>? ResolveClearKey(TableMetadata meta, TableAccessDecision decision, IReadOnlyList<string> columns)
    {
        if (columns.Count == 0)
        {
            return null;
        }

        var resolved = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var name = meta.GetColumn(column)?.ColumnName;
            if (name is null || !SqlIdentifierGuard.IsValidIdentifier(name) ||
                decision.GetEffectiveColumnAccess(name, meta) != ColumnAccessLevel.Clear)
            {
                return null;
            }
            resolved.Add(name);
        }
        return resolved;
    }

    private static bool CollidesWithReservedName(string column)
    {
        if (string.Equals(column, "autheris_rn", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "autheris_row", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "autheris_json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(column, "autheris_page", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (column.StartsWith("autheris_k", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(column.AsSpan(10), out _))
        {
            return true;
        }

        return false;
    }

    private static string BuildSetBasedRoot(NodePlan plan, CompileContext context)
    {
        var setBasedChildren = plan.Children
            .Where(c => c.Relation.IsList && plan.ListStrategies.TryGetValue(c.Relation, out var s) && s.Strategy == TreeListStrategy.SetBased)
            .ToList();

        var successfulCtes = new List<(TreeRelationNode Relation, NodePlan Child, string CteName, string CteSql)>();

        foreach (var (relation, child) in setBasedChildren)
        {
            var childPath = new[] { relation.ResponseKey };
            var snapshot = context.TakeSnapshot();
            try
            {
                if (context.Options.FailSetBasedFor != null &&
                    string.Equals(context.Options.FailSetBasedFor, relation.ResponseKey, StringComparison.Ordinal))
                {
                    if (string.Equals(context.Options.FailSetBasedFor, "cancelling_rel", StringComparison.Ordinal))
                    {
                        throw new OperationCanceledException();
                    }
                    throw new InvalidOperationException($"Fault injection for {relation.ResponseKey}");
                }

                var aliasIndex = child.Alias.StartsWith("t", StringComparison.OrdinalIgnoreCase) ? child.Alias[1..] : child.Alias;
                var cteName = Q(context, $"autheris_k{aliasIndex}");
                var cteSql = BuildSetBasedChildCte(child, context, plan, relation, cteName);
                successfulCtes.Add((relation, child, cteName, cteSql));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Restore(snapshot);
                plan.ListStrategies[relation] = (TreeListStrategy.CorrelatedUnindexed, "CompileFailure");
                for (int i = 0; i < context.Lists.Count; i++)
                {
                    if (context.Lists[i].Path.SequenceEqual(childPath))
                    {
                        context.Lists[i] = new TreeListPlan(childPath, TreeListStrategy.CorrelatedUnindexed, "CompileFailure", child.Node.ExecutionLimit ?? child.Node.Limit);
                        break;
                    }
                }
            }
        }

        if (successfulCtes.Count == 0)
        {
            return BuildCorrelatedRoot(plan, context);
        }

        var uniqueCols = GetClearUniqueKeyColumns(plan)!;
        var rootTieBreaks = uniqueCols
            .Where(c => !plan.Order.Any(o => string.Equals(o.Column, c, StringComparison.OrdinalIgnoreCase)))
            .Select(c => (Column: c, Descending: false))
            .ToList();
        var rootOrderWithTieBreaks = plan.Order.Concat(rootTieBreaks).ToList();

        var pageCteSql = BuildPageCte(plan, context, rootOrderWithTieBreaks);

        var rootAlias = Q(context, plan.Alias);
        var successfulRelations = successfulCtes.ToDictionary(c => c.Relation, c => c.CteName);

        var pairs = new List<(string Key, string Expression)>(plan.Outputs.Count + plan.Children.Count);
        foreach (var output in plan.Outputs)
        {
            pairs.Add((output.Key, output.LiteralExpression ?? ColumnExpression(plan, context, output.SourceColumn!)));
        }
        foreach (var (relation, child) in plan.Children)
        {
            if (successfulRelations.TryGetValue(relation, out var cteName))
            {
                string setBasedExpr = context.Dialect switch
                {
                    DatabaseDialect.SqlServer =>
                        $"JSON_QUERY(ISNULL({cteName}.{Q(context, "autheris_json")}, N'[]'))",
                    DatabaseDialect.PostgreSql =>
                        $"coalesce({cteName}.{Q(context, "autheris_json")}, '[]'::jsonb)",
                    DatabaseDialect.Databricks =>
                        $"coalesce({cteName}.{Q(context, "autheris_json")}, array())",
                    _ =>
                        $"json(coalesce({cteName}.{Q(context, "autheris_json")}, '[]'))"
                };
                pairs.Add((relation.ResponseKey, setBasedExpr));
            }
            else
            {
                pairs.Add((relation.ResponseKey, BuildChild(child, context, plan, relation)));
            }
        }

        var fromBuilder = new StringBuilder();
        fromBuilder.Append(Q(context, "autheris_page")).Append(" AS ").Append(rootAlias);
        foreach (var (relation, child, cteName, _) in successfulCtes)
        {
            fromBuilder.Append(" LEFT JOIN ").Append(cteName).Append(" ON ");
            var joins = new List<string>();
            for (int j = 0; j < relation.ChildColumns.Count; j++)
            {
                var parentCol = plan.Access.Metadata.GetColumn(relation.ParentColumns[j])!.ColumnName;
                joins.Add($"{cteName}.{Q(context, $"autheris_k{j}")} = {rootAlias}.{Q(context, parentCol)}");
            }
            fromBuilder.Append(string.Join(" AND ", joins));
        }

        var outerOrder = string.Join(", ", rootOrderWithTieBreaks.Select(o => $"{rootAlias}.{Q(context, o.Column)}{(o.Descending ? " DESC" : string.Empty)}"));

        string rootQuery;
        switch (context.Dialect)
        {
            case DatabaseDialect.SqlServer:
                var selectList = string.Join(", ", pairs.Select(p => $"{p.Expression} AS {Q(context, p.Key)}"));
                rootQuery = $"SELECT ISNULL((SELECT {selectList} FROM {fromBuilder}" +
                    (outerOrder.Length > 0 ? $" ORDER BY {outerOrder}" : string.Empty) +
                    " FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')";
                break;

            case DatabaseDialect.PostgreSql:
                var pgObj = BuildPostgresObject(pairs, context);
                rootQuery = $"SELECT coalesce(jsonb_agg({pgObj}{(outerOrder.Length > 0 ? " ORDER BY " + outerOrder : string.Empty)}), '[]'::jsonb)::text FROM {fromBuilder}";
                break;

            case DatabaseDialect.Databricks:
                var dbxObj = BuildDatabricksStruct(pairs, context.Dialect);
                rootQuery = BuildDatabricksProjectedRoot(fromBuilder.ToString(), rootAlias, dbxObj, rootOrderWithTieBreaks, context);
                break;

            default:
                throw new NotSupportedException($"Set-based tree SQL compiler does not support dialect '{context.Dialect}'.");
        }

        var allCtes = new List<string> { pageCteSql };
        foreach (var (_, _, _, cteSql) in successfulCtes)
        {
            allCtes.Add(cteSql);
        }

        return $"WITH {string.Join(",\n     ", allCtes)}\n{rootQuery}";
    }

    private static string BuildPageCte(
        NodePlan plan,
        CompileContext context,
        List<(string Column, bool Descending)> rootOrderWithTieBreaks)
    {
        var target = Q(context, TargetAlias);
        var metadata = plan.Access.Metadata;
        var node = plan.Node;
        var limit = node.ExecutionLimit ?? node.Limit;

        var sb = new StringBuilder("SELECT ");
        // #836: the page and the outer JSON are ordered by the tie-break key too, so the page carries it even when it is not
        // selected. It is not returned: the outer projection is built from plan.Outputs only.
        var projected = new List<string>(plan.InnerColumns.Count + rootOrderWithTieBreaks.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in plan.InnerColumns.Concat(rootOrderWithTieBreaks.Select(o => o.Column)))
        {
            if (seen.Add(column))
            {
                projected.Add(column);
            }
        }
        sb.Append(string.Join(", ", projected.Select(c => $"{target}.{Q(context, c)} AS {Q(context, c)}")));

        var securedSource = plan.Access.SecuredSource;
        if (securedSource != null)
        {
            sb.Append(" FROM (").Append(BindSecuredSource(context, securedSource)).Append(')');
        }
        else
        {
            sb.Append(" FROM ").Append(context.Dialect.FormatTableIdentifier(metadata.Identifier));
        }
        sb.Append(" AS ").Append(target);

        var where = BuildInnerPredicates(plan, context, parent: null, relation: null, correlate: false);
        if (limit == 0 && context.Dialect == DatabaseDialect.SqlServer)
        {
            where.Add("1 = 0");
            limit = 1;
        }
        if (where.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", where));
        }

        var orderBy = string.Join(", ", rootOrderWithTieBreaks.Select(o => $"{target}.{Q(context, o.Column)}{(o.Descending ? " DESC" : string.Empty)}"));
        var limitText = limit.ToString(CultureInfo.InvariantCulture);
        var offsetText = node.Offset.ToString(CultureInfo.InvariantCulture);

        if (context.Dialect == DatabaseDialect.SqlServer)
        {
            sb.Append(" ORDER BY ").Append(orderBy.Length > 0 ? orderBy : "(SELECT NULL)");
            sb.Append(" OFFSET ").Append(offsetText).Append(" ROWS FETCH NEXT ").Append(limitText).Append(" ROWS ONLY");
        }
        else
        {
            if (orderBy.Length > 0)
            {
                sb.Append(" ORDER BY ").Append(orderBy);
            }
            sb.Append(" LIMIT ").Append(limitText);
            if (node.Offset > 0)
            {
                sb.Append(" OFFSET ").Append(offsetText);
            }
        }

        return $"{Q(context, "autheris_page")} AS ({sb})";
    }

    private static string BuildSetBasedChildCte(
        NodePlan child,
        CompileContext context,
        NodePlan parent,
        TreeRelationNode relation,
        string cteName)
    {
        var childTarget = Q(context, TargetAlias);
        var childAlias = Q(context, child.Alias);
        var xAlias = Q(context, "x");

        // 1. Level 1 (Ranking level)
        var childInnerCols = child.InnerColumns.Select(c => $"{childTarget}.{Q(context, c)} AS {Q(context, c)}").ToList();
        for (int j = 0; j < relation.ChildColumns.Count; j++)
        {
            var childCol = child.Access.Metadata.GetColumn(relation.ChildColumns[j])!.ColumnName;
            childInnerCols.Add($"{childTarget}.{Q(context, childCol)} AS {Q(context, $"autheris_k{j}")}");
        }

        var partitionBy = string.Join(", ", relation.ChildColumns.Select(c => $"{childTarget}.{Q(context, child.Access.Metadata.GetColumn(c)!.ColumnName)}"));

        var orderTerms = new List<string>();
        foreach (var (col, desc) in child.Order)
        {
            orderTerms.Add($"{childTarget}.{Q(context, col)}{(desc ? " DESC" : string.Empty)}");
        }

        if (ResolveClearKey(child.Access.Metadata, child.Access.Decision, child.Access.Metadata.PrimaryKeyColumns) is { } childKey)
        {
            foreach (var pkCol in childKey)
            {
                if (!child.Order.Any(o => string.Equals(o.Column, pkCol, StringComparison.OrdinalIgnoreCase)))
                {
                    orderTerms.Add($"{childTarget}.{Q(context, pkCol)}");
                }
            }
        }

        var orderBy = orderTerms.Count > 0 ? string.Join(", ", orderTerms) : (context.Dialect == DatabaseDialect.Databricks ? "1" : "(SELECT NULL)");
        childInnerCols.Add($"ROW_NUMBER() OVER (PARTITION BY {partitionBy} ORDER BY {orderBy}) AS {Q(context, "autheris_rn")}");

        var securedSource = child.Access.SecuredSource;
        string fromSource = securedSource != null
            ? $"({BindSecuredSource(context, securedSource)})"
            : context.Dialect.FormatTableIdentifier(child.Access.Metadata.Identifier);

        var level1Where = BuildInnerPredicates(child, context, parent: parent, relation: relation, correlate: false);
        var pageCteName = Q(context, "autheris_page");
        var pageAlias = Q(context, "autheris_p");
        var existsPreds = new List<string>();
        for (int j = 0; j < relation.ChildColumns.Count; j++)
        {
            var parentCol = parent.Access.Metadata.GetColumn(relation.ParentColumns[j])!.ColumnName;
            var childCol = child.Access.Metadata.GetColumn(relation.ChildColumns[j])!.ColumnName;
            existsPreds.Add($"{pageAlias}.{Q(context, parentCol)} = {childTarget}.{Q(context, childCol)}");
        }
        level1Where.Add($"EXISTS (SELECT 1 FROM {pageCteName} AS {pageAlias} WHERE {string.Join(" AND ", existsPreds)})");
        string whereSql = level1Where.Count > 0 ? " WHERE " + string.Join(" AND ", level1Where) : string.Empty;

        string level1Sql = $"SELECT {string.Join(", ", childInnerCols)} FROM {fromSource} AS {childTarget}{whereSql}";

        // 2. Level 2 (Row JSON projection level)
        var level2Cols = new List<string>();
        for (int j = 0; j < relation.ChildColumns.Count; j++)
        {
            level2Cols.Add($"{childAlias}.{Q(context, $"autheris_k{j}")} AS {Q(context, $"autheris_k{j}")}");
        }
        level2Cols.Add($"{childAlias}.{Q(context, "autheris_rn")} AS {Q(context, "autheris_rn")}");

        string rowJsonExpr = context.Dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"CAST((SELECT {SqlServerSelectList(child, context)} FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER) AS nvarchar(max))",
            DatabaseDialect.PostgreSql =>
                PostgresObject(child, context),
            DatabaseDialect.Databricks =>
                DatabricksObject(child, context),
            _ => throw new NotSupportedException($"Set-based tree SQL compiler does not support dialect '{context.Dialect}'.")
        };
        level2Cols.Add($"{rowJsonExpr} AS {Q(context, "autheris_row")}");

        int childLimit = child.Node.ExecutionLimit ?? child.Node.Limit;
        string level2Sql = $"SELECT {string.Join(", ", level2Cols)} FROM ({level1Sql}) AS {childAlias} WHERE {childAlias}.{Q(context, "autheris_rn")} <= {childLimit}";

        // 3. Level 3 (Aggregation level)
        var level3Cols = new List<string>();
        for (int j = 0; j < relation.ChildColumns.Count; j++)
        {
            level3Cols.Add($"{xAlias}.{Q(context, $"autheris_k{j}")} AS {Q(context, $"autheris_k{j}")}");
        }

        var dbxRn = Q(context, "__rn");
        var dbxVal = Q(context, "__v");
        string aggExpr = context.Dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"N'[' + STRING_AGG({xAlias}.{Q(context, "autheris_row")}, N',') WITHIN GROUP (ORDER BY {xAlias}.{Q(context, "autheris_rn")}) + N']'",
            DatabaseDialect.PostgreSql =>
                $"jsonb_agg({xAlias}.{Q(context, "autheris_row")} ORDER BY {xAlias}.{Q(context, "autheris_rn")})",
            DatabaseDialect.Databricks =>
                $"transform(array_sort(collect_list(named_struct('__rn', {xAlias}.{Q(context, "autheris_rn")}, '__v', {xAlias}.{Q(context, "autheris_row")})), (l, r) -> CASE WHEN l.{dbxRn} < r.{dbxRn} THEN -1 WHEN l.{dbxRn} > r.{dbxRn} THEN 1 ELSE 0 END), e -> e.{dbxVal})",
            _ => throw new NotSupportedException($"Set-based tree SQL compiler does not support dialect '{context.Dialect}'.")
        };
        level3Cols.Add($"{aggExpr} AS {Q(context, "autheris_json")}");

        string groupBy = string.Join(", ", Enumerable.Range(0, relation.ChildColumns.Count).Select(j => $"{xAlias}.{Q(context, $"autheris_k{j}")}"));
        string level3Sql = $"SELECT {string.Join(", ", level3Cols)} FROM ({level2Sql}) AS {xAlias} GROUP BY {groupBy}";

        return $"{cteName} AS ({level3Sql})";
    }

    /// <summary>
    /// SQLA-06 follow-up: binds the parameters of a secured source. Each engine marker gets a fresh <c>@gqN</c> name from the shared counter and
    /// the shared parameter budget; the remap is a tokenising one (C4) on the post-compile text only.
    /// </summary>
    private static string BindSecuredSource(CompileContext context, TreeSecuredSource source)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in source.Parameters)
        {
            if (map.ContainsKey(parameter.Marker))
            {
                throw TreeSecuredSourceMarkers.Denied();
            }

            map[parameter.Marker] = context.AddParameter(parameter.Value);
        }

        return TreeSecuredSourceMarkers.Remap(source.Sql, source.Dialect, map);
    }

    /// <summary>SQL2-11: replaces @name parameter tokens outside string literals and quoted identifiers.</summary>
    internal static string RenameParameters(string sql, IReadOnlyDictionary<string, string> renames)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c is '\'' or '"' or '[' or '`')
            {
                char close = c == '[' ? ']' : c;
                int end = i + 1;
                while (end < sql.Length)
                {
                    if (sql[end] == close)
                    {
                        if (close != ']' && end + 1 < sql.Length && sql[end + 1] == close)
                        {
                            end += 2;
                            continue;
                        }
                        break;
                    }
                    end++;
                }
                end = Math.Min(end + 1, sql.Length);
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (c == '@')
            {
                int start = i + 1;
                int end = start;
                while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end++;
                }

                var name = sql[start..end];
                if (name.Length > 0 && renames.TryGetValue(name, out var replacement))
                {
                    sb.Append(replacement);
                    i = end;
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// G2/G4 (docs/plans/rls-subquery-in-strategy.md): compiles a GraphQL selection tree into ONE statement whose single
/// result value is the finished JSON array (SQL Server <c>FOR JSON PATH</c>, PostgreSQL <c>jsonb_agg</c>, SQLite
/// <c>json_group_array</c>).
/// <para>Every table level reads from its own derived table
/// <c>(SELECT … FROM table AS autheris_target WHERE tenant AND (row filter) AND join AND where ORDER BY … LIMIT …)</c>,
/// so row filters bind to the reserved alias exactly as in WebSQL and OData, nested limits apply per parent row, and the
/// outer levels only see governed rows. Masks are SQL literals; HMAC columns are read raw and pseudonymized by the
/// gateway afterwards. Filter, sort and join columns must be effectively Clear (SEC-01). All values are parameters.</para>
/// </summary>
public static partial class TreeSqlCompiler
{
    public const int MaxDepth = 5;
    public const int MaxLimit = 10_000;
    public const int MaxInListSize = 1_000;
    public const int MaxParameters = 2_000;
    public const int MaxFilterDepth = 15;

    /// <summary>Key/value pairs per JSON object function call (PostgreSQL allows 100 arguments, SQLite 127).</summary>
    private const int ObjectChunkSize = 50;

    private static readonly string TargetAlias = TrinoSqlEngine.RowFilterAliases.Target;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex SafeNameRegex();

    public static CompiledTreeQuery Compile(
        TreeQueryNode root,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        DatabaseDialect dialect,
        bool maskingDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(access);

        if (dialect is not (DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Sqlite))
        {
            throw new NotSupportedException($"Dialect '{dialect}' does not support single-statement JSON queries.");
        }

        if (Depth(root) > MaxDepth)
        {
            throw new GatewayInvalidQueryException($"The query nests relations deeper than {MaxDepth} levels.");
        }

        var context = new CompileContext(dialect, access, maskingDisabled);
        var plan = Prepare(root, context, path: []);
        var sql = BuildRoot(plan, context);
        return new CompiledTreeQuery(sql, context.Parameters, context.HmacColumns);
    }

    private static int Depth(TreeQueryNode node) =>
        1 + (node.Relations.Count == 0 ? 0 : node.Relations.Max(r => Depth(r.Child)));

    // ---------------------------------------------------------------- planning

    private sealed class CompileContext(DatabaseDialect dialect, IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access, bool maskingDisabled)
    {
        public DatabaseDialect Dialect { get; } = dialect;
        public IReadOnlyDictionary<TableIdentifier, TreeTableAccess> Access { get; } = access;
        public bool MaskingDisabled { get; } = maskingDisabled;
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);
        public List<TreeHmacColumn> HmacColumns { get; } = [];
        private int _aliasCounter;
        private int _parameterCounter;

        public string NextAlias() => "t" + (_aliasCounter++).ToString(CultureInfo.InvariantCulture);

        public string AddParameter(object? value)
        {
            if (Parameters.Count >= MaxParameters)
            {
                throw new GatewayInvalidQueryException($"The query uses more than {MaxParameters} values.");
            }
            string name;
            do
            {
                name = "@gq" + (_parameterCounter++).ToString(CultureInfo.InvariantCulture);
            }
            while (Parameters.ContainsKey(name));
            Parameters[name] = value ?? DBNull.Value;
            return name;
        }

        /// <summary>
        /// SQL2-11: binds the parameters of one table's row filter. Row filters of different tables may use the same
        /// parameter name with different values (each consent names its own parameters); such a parameter gets a name
        /// unique to this query and the filter SQL is rewritten accordingly, instead of rejecting the whole query.
        /// </summary>
        public string BindRowFilter(string filterSql, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return filterSql;
            }

            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in parameters)
            {
                var bound = value ?? DBNull.Value;
                if (Parameters.TryGetValue(name, out var existing) && !Equals(existing, bound))
                {
                    string unique;
                    do
                    {
                        unique = "@rf" + (_parameterCounter++).ToString(CultureInfo.InvariantCulture) + "_" + name.TrimStart('@');
                    }
                    while (Parameters.ContainsKey(unique));

                    renames[name.TrimStart('@')] = unique;
                    Parameters[unique] = bound;
                    continue;
                }

                Parameters[name] = bound;
            }

            return renames.Count == 0 ? filterSql : RenameParameters(filterSql, renames);
        }
    }

    /// <summary>SQL2-11: replaces @name parameter tokens outside string literals and quoted identifiers.</summary>
    internal static string RenameParameters(string sql, IReadOnlyDictionary<string, string> renames)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 16);
        int i = 0;
        while (i < sql.Length)
        {
            char c = sql[i];
            if (c is '\'' or '"' or '[')
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

    private sealed record OutputColumn(string Key, string? SourceColumn, string? LiteralExpression);

    private sealed class NodePlan
    {
        public required TreeQueryNode Node { get; init; }
        public required TreeTableAccess Access { get; init; }
        public required string Alias { get; init; }
        public required List<OutputColumn> Outputs { get; init; }
        public required List<string> InnerColumns { get; init; }
        public required List<(string Column, bool Descending)> Order { get; init; }
        public required List<(TreeRelationNode Relation, NodePlan Child)> Children { get; init; }
    }

    private static NodePlan Prepare(TreeQueryNode node, CompileContext context, IReadOnlyList<string> path)
    {
        if (!context.Access.TryGetValue(node.Table, out var tableAccess) || !tableAccess.Decision.IsAllowed)
        {
            throw new GatewayForbiddenException("Access denied.");
        }

        if (node.Limit < 1 || node.Limit > MaxLimit)
        {
            throw new GatewayInvalidQueryException($"The page size must be between 1 and {MaxLimit}.");
        }
        if (node.Offset < 0)
        {
            throw new GatewayInvalidQueryException("The offset must not be negative.");
        }
        if (node.Columns.Count == 0 && node.Relations.Count == 0)
        {
            throw new GatewayInvalidQueryException($"The selection on '{node.Table.TableName}' is empty.");
        }

        var metadata = tableAccess.Metadata;
        var inner = new List<string>();
        void Need(string column)
        {
            if (!inner.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                inner.Add(column);
            }
        }

        // Selected columns: catalog spelling, deduplicated, Deny == unknown.
        var outputs = new List<OutputColumn>();
        foreach (var requested in node.Columns)
        {
            var column = ResolveReadableColumn(metadata, tableAccess.Decision, requested);
            if (outputs.Any(o => string.Equals(o.Key, column, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var level = tableAccess.Decision.GetEffectiveColumnAccess(column, metadata);
            if (level == ColumnAccessLevel.Mask && !context.MaskingDisabled)
            {
                var rule = metadata.ColumnMaskingRules.TryGetValue(column, out var r) ? r : new MaskingRule { RuleType = "REDACT" };
                if (IsHmacRule(rule))
                {
                    context.HmacColumns.Add(new TreeHmacColumn(path, column, rule));
                    outputs.Add(new OutputColumn(column, column, null));
                    Need(column);
                }
                else
                {
                    var colMeta = metadata.GetColumn(column);
                var isString = colMeta == null || IsStringType(colMeta.DataType);
                outputs.Add(new OutputColumn(column, null, MaskLiteral(rule, context.Dialect, isString)));
                }
            }
            else
            {
                outputs.Add(new OutputColumn(column, column, null));
                Need(column);
            }
        }

        // Order: explicit, otherwise the Clear primary key (stable paging).
        var order = new List<(string Column, bool Descending)>();
        foreach (var term in node.OrderBy)
        {
            var column = ResolveClearColumn(metadata, tableAccess.Decision, term.Column);
            order.Add((column, term.Descending));
            Need(column);
        }
        if (order.Count == 0)
        {
            foreach (var pk in metadata.PrimaryKeyColumns)
            {
                var column = metadata.GetColumn(pk)?.ColumnName;
                if (column == null || tableAccess.Decision.GetEffectiveColumnAccess(column, metadata) != ColumnAccessLevel.Clear)
                {
                    order.Clear();
                    break;
                }
                order.Add((column, false));
                Need(column);
            }
        }

        if (node.Where != null)
        {
            ValidateFilterColumns(node.Where, metadata, tableAccess.Decision, depth: 0);
        }

        var children = new List<(TreeRelationNode Relation, NodePlan Child)>();
        foreach (var relation in node.Relations)
        {
            if (!SafeNameRegex().IsMatch(relation.ResponseKey))
            {
                throw new GatewayInvalidQueryException("Invalid relation name.");
            }
            if (outputs.Any(o => string.Equals(o.Key, relation.ResponseKey, StringComparison.OrdinalIgnoreCase)) ||
                children.Any(c => string.Equals(c.Relation.ResponseKey, relation.ResponseKey, StringComparison.OrdinalIgnoreCase)))
            {
                throw new GatewayInvalidQueryException($"The response name '{relation.ResponseKey}' is used twice on '{node.Table.TableName}'.");
            }
            if (relation.ParentColumns.Count == 0 || relation.ParentColumns.Count != relation.ChildColumns.Count)
            {
                throw new GatewayInvalidQueryException($"The relation '{relation.ResponseKey}' has no valid join keys.");
            }
            foreach (var parentColumn in relation.ParentColumns)
            {
                Need(ResolveClearColumn(metadata, tableAccess.Decision, parentColumn));
            }

            var childPath = path.Append(relation.ResponseKey).ToArray();
            var childPlan = Prepare(relation.Child, context, childPath);
            foreach (var childColumn in relation.ChildColumns)
            {
                ResolveClearColumn(childPlan.Access.Metadata, childPlan.Access.Decision, childColumn);
            }
            children.Add((relation, childPlan));
        }

        if (inner.Count == 0)
        {
            // Only masked literals and/or relations selected: the derived table still needs one column.
            var anyColumn = metadata.Columns.Select(c => c.ColumnName)
                .FirstOrDefault(c => tableAccess.Decision.GetEffectiveColumnAccess(c, metadata) == ColumnAccessLevel.Clear);
            if (anyColumn == null)
            {
                throw new GatewayInvalidQueryException($"No readable column on '{node.Table.TableName}'.");
            }
            Need(anyColumn);
        }

        return new NodePlan
        {
            Node = node,
            Access = tableAccess,
            Alias = context.NextAlias(),
            Outputs = outputs,
            InnerColumns = inner,
            Order = order,
            Children = children
        };
    }

    private static string ResolveReadableColumn(TableMetadata metadata, TableAccessDecision decision, string requested)
    {
        var column = metadata.GetColumn(requested?.Trim() ?? string.Empty)?.ColumnName;
        if (column == null || !SafeNameRegex().IsMatch(column) || decision.GetEffectiveColumnAccess(column, metadata) == ColumnAccessLevel.Deny)
        {
            throw new GatewayInvalidQueryException($"The property '{Echo(requested)}' does not exist or is not accessible.");
        }
        return column;
    }

    /// <summary>SEC-01: filter, sort and join columns must be effectively Clear (no inference on masked/denied values).</summary>
    private static string ResolveClearColumn(TableMetadata metadata, TableAccessDecision decision, string requested)
    {
        var column = metadata.GetColumn(requested?.Trim() ?? string.Empty)?.ColumnName;
        if (column == null || !SafeNameRegex().IsMatch(column) || decision.GetEffectiveColumnAccess(column, metadata) != ColumnAccessLevel.Clear)
        {
            throw new GatewayInvalidQueryException($"The property '{Echo(requested)}' cannot be used for filtering, sorting or joining.");
        }
        return column;
    }

    private static void ValidateFilterColumns(TreeFilter filter, TableMetadata metadata, TableAccessDecision decision, int depth)
    {
        if (depth > MaxFilterDepth)
        {
            throw new GatewayInvalidQueryException($"The filter is nested deeper than {MaxFilterDepth} levels.");
        }
        switch (filter)
        {
            case TreeAndFilter and:
                foreach (var item in and.Items) ValidateFilterColumns(item, metadata, decision, depth + 1);
                break;
            case TreeOrFilter or:
                foreach (var item in or.Items) ValidateFilterColumns(item, metadata, decision, depth + 1);
                break;
            case TreeNotFilter not:
                ValidateFilterColumns(not.Item, metadata, decision, depth + 1);
                break;
            case TreeComparison comparison:
                ResolveClearColumn(metadata, decision, comparison.Column);
                break;
            default:
                throw new GatewayInvalidQueryException("Unsupported filter.");
        }
    }

    private static string Echo(string? name) =>
        name != null && SafeNameRegex().IsMatch(name.Trim()) ? name.Trim() : "(invalid name)";

    private static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;

    private static bool IsStringType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return true;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains("interval") || dt.Contains("point") || dt.Contains("polygon") ||
            dt.Contains("geometry") || dt.Contains("geography") || dt.Contains("line") ||
            dt.Contains("json") || dt.Contains("xml") || dt.Contains("uuid") || dt.Contains("guid"))
        {
            return true;
        }

        var baseType = dt.Split('(', '[', ' ')[0].Trim();
        if (baseType.StartsWith("bit") || baseType.StartsWith("bool")) return false;
        if (baseType is "bigint" or "int8" or "long" or "int" or "integer" or "int4" or "smallint" or "int2" or "tinyint") return false;
        if (baseType is "decimal" or "numeric" or "money" or "smallmoney" or "float" or "double" or "real" or "float4" or "float8") return false;

        return true;
    }

    private static string MaskLiteral(MaskingRule rule, DatabaseDialect dialect, bool isString)
    {
        if (!isString || string.Equals(rule.RuleType, "NULLIFY", StringComparison.OrdinalIgnoreCase))
        {
            return "NULL";
        }
        var text = !string.IsNullOrWhiteSpace(rule.Replacement) ? rule.Replacement : "***";
        var prefix = dialect == DatabaseDialect.SqlServer ? "N" : string.Empty;
        return $"{prefix}'{dialect.EscapeSqlLiteral(text)}'";
    }

    // ---------------------------------------------------------------- SQL

    private static string Q(CompileContext context, string identifier) => context.Dialect.QuoteIdentifier(identifier);

    private static string BuildInner(NodePlan plan, CompileContext context, NodePlan? parent, TreeRelationNode? relation)
    {
        var dialect = context.Dialect;
        var target = Q(context, TargetAlias);
        var metadata = plan.Access.Metadata;
        var node = plan.Node;
        var isRoot = parent == null;
        var limit = relation is { IsList: false } ? 1 : node.Limit;

        var sb = new StringBuilder("SELECT ");
        if (!isRoot && dialect == DatabaseDialect.SqlServer)
        {
            sb.Append("TOP (").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(") ");
        }
        sb.Append(string.Join(", ", plan.InnerColumns.Select(c => $"{target}.{Q(context, c)} AS {Q(context, c)}")));
        sb.Append(" FROM ").Append(dialect.FormatTableIdentifier(metadata.Identifier));
        sb.Append(" AS ").Append(target);

        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.Access.TenantColumn))
        {
            var tenantColumn = metadata.GetColumn(plan.Access.TenantColumn)?.ColumnName
                ?? throw new GatewayForbiddenException("Access denied.");
            where.Add($"{target}.{Q(context, tenantColumn)} = {context.AddParameter(plan.Access.TenantValue)}");
        }

        var rowFilter = plan.Access.Decision.CombinedRowFilterSql;
        if (!string.IsNullOrWhiteSpace(rowFilter))
        {
            SqlSecurityValidator.ValidateRowFilter(plan.Access.Decision);
            where.Add($"({context.BindRowFilter(rowFilter, plan.Access.Decision.RowFilterParameters)})");
        }

        if (parent != null && relation != null)
        {
            for (var i = 0; i < relation.ChildColumns.Count; i++)
            {
                var childColumn = metadata.GetColumn(relation.ChildColumns[i])!.ColumnName;
                var parentColumn = parent.Access.Metadata.GetColumn(relation.ParentColumns[i])!.ColumnName;
                where.Add($"{target}.{Q(context, childColumn)} = {Q(context, parent.Alias)}.{Q(context, parentColumn)}");
            }
        }

        if (node.Where != null)
        {
            where.Add(BuildFilter(node.Where, metadata, context, target));
        }

        if (where.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", where));
        }

        var orderBy = string.Join(", ", plan.Order.Select(o => $"{target}.{Q(context, o.Column)}{(o.Descending ? " DESC" : string.Empty)}"));
        var limitText = limit.ToString(CultureInfo.InvariantCulture);
        var offsetText = node.Offset.ToString(CultureInfo.InvariantCulture);

        if (dialect == DatabaseDialect.SqlServer)
        {
            if (isRoot)
            {
                sb.Append(" ORDER BY ").Append(orderBy.Length > 0 ? orderBy : "(SELECT NULL)");
                sb.Append(" OFFSET ").Append(offsetText).Append(" ROWS FETCH NEXT ").Append(limitText).Append(" ROWS ONLY");
            }
            else if (orderBy.Length > 0)
            {
                sb.Append(" ORDER BY ").Append(orderBy);
            }
        }
        else
        {
            if (orderBy.Length > 0)
            {
                sb.Append(" ORDER BY ").Append(orderBy);
            }
            sb.Append(" LIMIT ").Append(limitText);
            if (isRoot && node.Offset > 0)
            {
                sb.Append(" OFFSET ").Append(offsetText);
            }
        }

        return sb.ToString();
    }

    private static string OuterOrderBy(NodePlan plan, CompileContext context) =>
        string.Join(", ", plan.Order.Select(o => $"{Q(context, plan.Alias)}.{Q(context, o.Column)}{(o.Descending ? " DESC" : string.Empty)}"));

    private static string ColumnExpression(NodePlan plan, CompileContext context, string column)
    {
        var quoted = $"{Q(context, plan.Alias)}.{Q(context, column)}";
        var dataType = plan.Access.Metadata.GetColumn(column)?.DataType?.Trim().ToLowerInvariant();
        if (dataType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring" or "multipolygon" or "multipoint")
        {
            return context.Dialect switch
            {
                DatabaseDialect.SqlServer => $"{quoted}.STAsText()",
                DatabaseDialect.PostgreSql => $"ST_AsGeoJSON({quoted})",
                DatabaseDialect.Sqlite => $"AsGeoJSON({quoted})",
                _ => quoted
            };
        }
        if (dataType is "hierarchyid" && context.Dialect == DatabaseDialect.SqlServer)
        {
            return $"{quoted}.ToString()";
        }
        if (dataType is "bytea" && context.Dialect == DatabaseDialect.PostgreSql)
        {
            return $"encode({quoted}, 'base64')";
        }
        if (dataType is "blob" && context.Dialect == DatabaseDialect.Sqlite)
        {
            return $"hex({quoted})";
        }
        return quoted;
    }

    private static List<(string Key, string Expression)> ObjectPairs(NodePlan plan, CompileContext context)
    {
        var pairs = new List<(string Key, string Expression)>(plan.Outputs.Count + plan.Children.Count);
        foreach (var output in plan.Outputs)
        {
            pairs.Add((output.Key, output.LiteralExpression ?? ColumnExpression(plan, context, output.SourceColumn!)));
        }
        foreach (var (relation, child) in plan.Children)
        {
            pairs.Add((relation.ResponseKey, BuildChild(child, context, plan, relation)));
        }
        return pairs;
    }

    private static string BuildRoot(NodePlan plan, CompileContext context)
    {
        var inner = BuildInner(plan, context, parent: null, relation: null);
        var alias = Q(context, plan.Alias);
        var order = OuterOrderBy(plan, context);

        return context.Dialect switch
        {
            DatabaseDialect.SqlServer =>
                $"SELECT ISNULL((SELECT {SqlServerSelectList(plan, context)} FROM ({inner}) AS {alias}" +
                (order.Length > 0 ? $" ORDER BY {order}" : string.Empty) +
                " FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]')",
            DatabaseDialect.PostgreSql =>
                $"SELECT coalesce(jsonb_agg({PostgresObject(plan, context)}{(order.Length > 0 ? " ORDER BY " + order : string.Empty)}), '[]'::jsonb)::text FROM ({inner}) AS {alias}",
            _ =>
                $"SELECT json_group_array(json({SqliteObject(plan, context)}){(order.Length > 0 ? " ORDER BY " + order : string.Empty)}) FROM ({inner}) AS {alias}"
        };
    }

    private static string BuildChild(NodePlan plan, CompileContext context, NodePlan parent, TreeRelationNode relation)
    {
        var inner = BuildInner(plan, context, parent, relation);
        var alias = Q(context, plan.Alias);
        var order = OuterOrderBy(plan, context);

        switch (context.Dialect)
        {
            case DatabaseDialect.SqlServer:
                if (!relation.IsList)
                {
                    return $"JSON_QUERY((SELECT {SqlServerSelectList(plan, context)} FROM ({inner}) AS {alias} FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER))";
                }
                return $"JSON_QUERY(ISNULL((SELECT {SqlServerSelectList(plan, context)} FROM ({inner}) AS {alias}" +
                       (order.Length > 0 ? $" ORDER BY {order}" : string.Empty) +
                       " FOR JSON PATH, INCLUDE_NULL_VALUES), N'[]'))";

            case DatabaseDialect.PostgreSql:
                return relation.IsList
                    ? $"(SELECT coalesce(jsonb_agg({PostgresObject(plan, context)}{(order.Length > 0 ? " ORDER BY " + order : string.Empty)}), '[]'::jsonb) FROM ({inner}) AS {alias})"
                    : $"(SELECT {PostgresObject(plan, context)} FROM ({inner}) AS {alias})";

            default:
                // SQLite: values of scalar subqueries lose the JSON subtype; json() restores it.
                return relation.IsList
                    ? $"json((SELECT json_group_array(json({SqliteObject(plan, context)}){(order.Length > 0 ? " ORDER BY " + order : string.Empty)}) FROM ({inner}) AS {alias}))"
                    : $"json((SELECT {SqliteObject(plan, context)} FROM ({inner}) AS {alias}))";
        }
    }

    private static string SqlServerSelectList(NodePlan plan, CompileContext context) =>
        string.Join(", ", ObjectPairs(plan, context).Select(p => $"{p.Expression} AS {Q(context, p.Key)}"));

    private static string PostgresObject(NodePlan plan, CompileContext context)
    {
        var chunks = ObjectPairs(plan, context)
            .Chunk(ObjectChunkSize)
            .Select(chunk => "jsonb_build_object(" + string.Join(", ", chunk.Select(p => $"'{p.Key}', {p.Expression}")) + ")")
            .ToList();
        return chunks.Count == 0 ? "'{}'::jsonb" : string.Join(" || ", chunks);
    }

    private static string SqliteObject(NodePlan plan, CompileContext context)
    {
        var chunks = ObjectPairs(plan, context).Chunk(ObjectChunkSize).ToList();
        if (chunks.Count == 0)
        {
            return "json_object()";
        }

        // json_set instead of json_patch: a merge patch would drop keys whose value is null.
        var expression = "json_object(" + string.Join(", ", chunks[0].Select(p => $"'{p.Key}', {p.Expression}")) + ")";
        foreach (var chunk in chunks.Skip(1))
        {
            expression = $"json_set({expression}, " + string.Join(", ", chunk.Select(p => $"'$.\"{p.Key}\"', {p.Expression}")) + ")";
        }
        return expression;
    }

    // ---------------------------------------------------------------- filters

    private static string BuildFilter(TreeFilter filter, TableMetadata metadata, CompileContext context, string target)
    {
        switch (filter)
        {
            case TreeAndFilter and:
                return and.Items.Count == 0
                    ? "1 = 1"
                    : "(" + string.Join(" AND ", and.Items.Select(i => BuildFilter(i, metadata, context, target))) + ")";
            case TreeOrFilter or:
                return or.Items.Count == 0
                    ? "1 = 0"
                    : "(" + string.Join(" OR ", or.Items.Select(i => BuildFilter(i, metadata, context, target))) + ")";
            case TreeNotFilter not:
                return "NOT (" + BuildFilter(not.Item, metadata, context, target) + ")";
            case TreeComparison comparison:
                return BuildComparison(comparison, metadata, context, target);
            default:
                throw new GatewayInvalidQueryException("Unsupported filter.");
        }
    }

    private static string BuildComparison(TreeComparison comparison, TableMetadata metadata, CompileContext context, string target)
    {
        var column = $"{target}.{Q(context, metadata.GetColumn(comparison.Column)!.ColumnName)}";
        var value = comparison.Value;

        switch (comparison.Operator)
        {
            case TreeFilterOperator.IsNull:
                return value is true ? $"{column} IS NULL" : $"{column} IS NOT NULL";
            case TreeFilterOperator.Eq:
                return value == null ? $"{column} IS NULL" : $"{column} = {context.AddParameter(value)}";
            case TreeFilterOperator.Neq:
                return value == null ? $"{column} IS NOT NULL" : $"{column} <> {context.AddParameter(value)}";
            case TreeFilterOperator.Gt:
                return $"{column} > {context.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Gte:
                return $"{column} >= {context.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Lt:
                return $"{column} < {context.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.Lte:
                return $"{column} <= {context.AddParameter(RequireValue(comparison))}";
            case TreeFilterOperator.In:
            case TreeFilterOperator.NotIn:
            {
                var items = ToList(comparison);
                if (items.Count == 0)
                {
                    return comparison.Operator == TreeFilterOperator.In ? "1 = 0" : "1 = 1";
                }
                var names = string.Join(", ", items.Select(context.AddParameter));
                return comparison.Operator == TreeFilterOperator.In ? $"{column} IN ({names})" : $"{column} NOT IN ({names})";
            }
            case TreeFilterOperator.Contains:
                return $"{column} LIKE {context.AddParameter("%" + EscapeLike(RequireText(comparison)) + "%")} ESCAPE '\\'";
            case TreeFilterOperator.StartsWith:
                return $"{column} LIKE {context.AddParameter(EscapeLike(RequireText(comparison)) + "%")} ESCAPE '\\'";
            case TreeFilterOperator.EndsWith:
                return $"{column} LIKE {context.AddParameter("%" + EscapeLike(RequireText(comparison)))} ESCAPE '\\'";
            default:
                throw new GatewayInvalidQueryException("Unsupported filter operator.");
        }
    }

    private static object RequireValue(TreeComparison comparison) =>
        comparison.Value ?? throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' on '{comparison.Column}' needs a value.");

    private static string RequireText(TreeComparison comparison) =>
        comparison.Value as string ?? throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' on '{comparison.Column}' needs a text value.");

    private static List<object?> ToList(TreeComparison comparison)
    {
        if (comparison.Value is string || comparison.Value is not IEnumerable enumerable)
        {
            throw new GatewayInvalidQueryException($"The operator '{comparison.Operator}' on '{comparison.Column}' needs a list.");
        }
        var items = enumerable.Cast<object?>().Where(v => v != null).Distinct().ToList();
        if (items.Count > MaxInListSize)
        {
            throw new GatewayInvalidQueryException($"A list filter may contain at most {MaxInListSize} values.");
        }
        return items;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal)
             .Replace("[", "\\[", StringComparison.Ordinal);
}

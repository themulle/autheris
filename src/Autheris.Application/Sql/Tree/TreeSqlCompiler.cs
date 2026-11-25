using System.Collections;
using System.Globalization;
using System.Text;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Application.Sql.Tree;

/// <summary>
/// G2/G4 (docs/plans/rls-subquery-in-strategy.md): compiles a GraphQL selection tree into ONE statement whose single
/// result value is the finished JSON array (SQL Server <c>FOR JSON PATH</c>, PostgreSQL <c>jsonb_agg</c>).
/// <para>Every table level reads from its own derived table
/// <c>(SELECT … FROM table AS autheris_target WHERE tenant AND (row filter) AND join AND where ORDER BY … LIMIT …)</c>,
/// so row filters bind to the reserved alias exactly as in WebSQL and OData, nested limits apply per parent row, and the
/// outer levels only see governed rows. Masks are SQL literals; HMAC columns are read raw and pseudonymized by the
/// gateway afterwards. Filter, sort and join columns must be effectively Clear. All values are parameters.</para>
/// </summary>
public static partial class TreeSqlCompiler
{
    public const int MaxDepth = 5;
    public const int MaxLimit = 10_001;
    public const int MaxInListSize = 1_000;
    public const int MaxParameters = 2_000;
    public const int MaxFilterDepth = 15;

    /// <summary>Key/value pairs per JSON object function call (PostgreSQL allows 100 arguments).</summary>
    private const int ObjectChunkSize = 50;

    private static readonly string TargetAlias = TrinoSqlEngine.RowFilterAliases.Target;

    /// <summary>
    /// 03 / G-6: the dialects whose single-statement JSON aggregation the tree compiler implements (the constant
    /// stand-in for the dialect provider capability <c>SupportsTreeCompile</c>). Tables of other dialects remain readable
    /// as flat tables through the governed compile path but offer no nested relation fields.
    /// </summary>
    public static bool SupportsTreeCompile(DatabaseDialect dialect) =>
        dialect is DatabaseDialect.SqlServer or DatabaseDialect.PostgreSql or DatabaseDialect.Databricks;

    public static CompiledTreeQuery Compile(
        TreeQueryNode root,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        DatabaseDialect dialect,
        bool maskingDisabled = false)
        => Compile(root, access, dialect, maskingDisabled, TreeCompileOptions.Default);

    internal static CompiledTreeQuery Compile(
        TreeQueryNode root,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        DatabaseDialect dialect,
        bool maskingDisabled,
        TreeCompileOptions? options)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(access);

        if (!SupportsTreeCompile(dialect))
        {
            throw new NotSupportedException($"Dialect '{dialect}' does not support single-statement JSON queries.");
        }

        if (Depth(root) > MaxDepth)
        {
            throw new GatewayInvalidQueryException($"The query nests relations deeper than {MaxDepth} levels.");
        }

        var context = new CompileContext(dialect, access, maskingDisabled, options);
        var plan = Prepare(root, context, path: []);
        PlanListStrategies(plan, context, isRoot: true, path: []);
        var sql = BuildRoot(plan, context);
        return new CompiledTreeQuery(sql, context.Parameters, context.HmacColumns, context.Lists);
    }

    /// <summary>
    /// Runs the validation phase of <see cref="Compile"/> (column, filter and required-filter-column checks) without emitting SQL and
    /// without consulting any secured source, so policy-independent client errors surface before row-policy support checks.
    /// </summary>
    public static void Validate(
        TreeQueryNode root,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        DatabaseDialect dialect,
        bool maskingDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(access);

        if (!SupportsTreeCompile(dialect))
        {
            throw new NotSupportedException($"Dialect '{dialect}' does not support single-statement JSON queries.");
        }

        if (Depth(root) > MaxDepth)
        {
            throw new GatewayInvalidQueryException($"The query nests relations deeper than {MaxDepth} levels.");
        }

        _ = Prepare(root, new CompileContext(dialect, access, maskingDisabled), path: []);
    }

    /// <summary>
    /// Lists the selected columns that <see cref="Compile"/> masks for the caller (same decision, same column
    /// resolution), so that transports can report them (GraphQL <c>extensions.maskedFields</c>). Columns that are unknown
    /// or denied are skipped here; <see cref="Compile"/> rejects them.
    /// </summary>
    public static IReadOnlyList<TreeMaskedColumn> CollectMaskedColumns(
        TreeQueryNode root,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        bool maskingDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(access);

        var result = new List<TreeMaskedColumn>();
        if (maskingDisabled)
        {
            return result;
        }

        CollectMasked(root, access, [], result);
        return result;
    }

    private static void CollectMasked(
        TreeQueryNode node,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        IReadOnlyList<string> path,
        List<TreeMaskedColumn> result)
    {
        if (!access.TryGetValue(node.Table, out var tableAccess))
        {
            return;
        }

        foreach (var requested in node.Columns)
        {
            var column = tableAccess.Metadata.GetColumn(requested?.Trim() ?? string.Empty)?.ColumnName;
            if (column != null &&
                tableAccess.Decision.GetEffectiveColumnAccess(column, tableAccess.Metadata) == ColumnAccessLevel.Mask &&
                !result.Any(m => string.Equals(m.Column, column, StringComparison.Ordinal) && m.Path.SequenceEqual(path, StringComparer.Ordinal)))
            {
                result.Add(new TreeMaskedColumn(path, column));
            }
        }

        foreach (var relation in node.Relations)
        {
            CollectMasked(relation.Child, access, path.Append(relation.ResponseKey).ToArray(), result);
        }
    }

    private static int Depth(TreeQueryNode node) =>
        1 + (node.Relations.Count == 0 ? 0 : node.Relations.Max(r => Depth(r.Child)));

    // ---------------------------------------------------------------- planning

    private sealed class CompileContext(
        DatabaseDialect dialect,
        IReadOnlyDictionary<TableIdentifier, TreeTableAccess> access,
        bool maskingDisabled,
        TreeCompileOptions? options = null)
    {
        public DatabaseDialect Dialect { get; } = dialect;
        public IReadOnlyDictionary<TableIdentifier, TreeTableAccess> Access { get; } = access;
        public bool MaskingDisabled { get; } = maskingDisabled;
        public TreeCompileOptions Options { get; } = options ?? TreeCompileOptions.Default;
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);
        public List<TreeHmacColumn> HmacColumns { get; } = [];
        public List<TreeListPlan> Lists { get; } = [];
        private int _aliasCounter;
        private int _parameterCounter;

        public sealed record ContextSnapshot(
            int AliasCounter,
            int ParameterCounter,
            Dictionary<string, object?> Parameters,
            int HmacCount,
            int ListCount);

        public ContextSnapshot TakeSnapshot() =>
            new(_aliasCounter, _parameterCounter, new Dictionary<string, object?>(Parameters, StringComparer.Ordinal), HmacColumns.Count, Lists.Count);

        public void Restore(ContextSnapshot s)
        {
            _aliasCounter = s.AliasCounter;
            _parameterCounter = s.ParameterCounter;
            Parameters.Clear();
            foreach (var kvp in s.Parameters)
            {
                Parameters[kvp.Key] = kvp.Value;
            }
            if (HmacColumns.Count > s.HmacCount)
            {
                HmacColumns.RemoveRange(s.HmacCount, HmacColumns.Count - s.HmacCount);
            }
            if (Lists.Count > s.ListCount)
            {
                Lists.RemoveRange(s.ListCount, Lists.Count - s.ListCount);
            }
        }

        public string NextAlias() => "t" + (_aliasCounter++).ToString(CultureInfo.InvariantCulture);

        public string AddParameter(object? value)
        {
            if (Parameters.Count >= MaxParameters)
            {
                throw new GatewayInvalidQueryException($"The query uses more than {MaxParameters} values.");
            }
            var prefix = Dialect == DatabaseDialect.Databricks ? ":gq" : "@gq";
            string name;
            do
            {
                name = prefix + (_parameterCounter++).ToString(CultureInfo.InvariantCulture);
            }
            while (Parameters.ContainsKey(name));
            Parameters[name] = value ?? DBNull.Value;
            return name;
        }

        public string BindRowFilter(string filterSql, IReadOnlyDictionary<string, object?>? parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return filterSql;
            }

            // two keys of one decision that name the same parameter (marker prefix or case) fail closed.
            _ = Autheris.Application.Sql.Policy.RowFilterParameterNames.Checked(parameters);

            var markerPrefix = Dialect == DatabaseDialect.Databricks ? ":" : "@";
            var rfPrefix = Dialect == DatabaseDialect.Databricks ? ":rf" : "@rf";
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in parameters)
            {
                var bound = value ?? DBNull.Value;
                var raw = name.TrimStart('@', ':');
                var targetName = markerPrefix + raw;
                if (Parameters.TryGetValue(targetName, out var existing) && !Equals(existing, bound))
                {
                    string unique;
                    do
                    {
                        unique = rfPrefix + (_parameterCounter++).ToString(CultureInfo.InvariantCulture) + "_" + raw;
                    }
                    while (Parameters.ContainsKey(unique));

                    renames[raw] = unique;
                    Parameters[unique] = bound;
                    continue;
                }

                if (!string.Equals(name, targetName, StringComparison.Ordinal))
                {
                    renames[raw] = targetName;
                }

                Parameters[targetName] = bound;
            }

            return renames.Count == 0 ? filterSql : RenameParameters(filterSql, renames);
        }
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
        public Dictionary<TreeRelationNode, (TreeListStrategy Strategy, string? Reason)> ListStrategies { get; } = [];
    }

    private static NodePlan Prepare(TreeQueryNode node, CompileContext context, IReadOnlyList<string> path, IReadOnlyList<string>? joinedChildColumns = null)
    {
        if (!context.Access.TryGetValue(node.Table, out var tableAccess) || !tableAccess.Decision.IsAllowed)
        {
            throw new GatewayForbiddenException("Access denied.");
        }

        var effectiveLimit = node.ExecutionLimit ?? node.Limit;
        if (effectiveLimit < 0 || effectiveLimit > MaxLimit)
        {
            throw new GatewayInvalidQueryException($"The page size must be between 0 and {MaxLimit}.");
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
                    var maskExpr = context.Dialect == DatabaseDialect.Databricks
                        ? DatabricksMaskExpression(rule, context, isString)
                        : MaskLiteral(rule, context.Dialect, isString);
                    outputs.Add(new OutputColumn(column, null, maskExpr));
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
            // SD-2: the same default ordering as every other read path. The composite fallback sorts every clear column, so
            // nested relation lists stop after the key rules to bound the cost.
            var defaultOrdering = Autheris.Application.DataSources.Services.DefaultReadOrdering.Resolve(metadata, tableAccess.Decision);
            var useDefault = defaultOrdering.Kind != Autheris.Application.DataSources.Services.DefaultOrderingKind.None &&
                (path.Count == 0 || defaultOrdering.Kind != Autheris.Application.DataSources.Services.DefaultOrderingKind.ClearColumnComposite);
            if (useDefault)
            {
                if (!defaultOrdering.Terms.All(t => SqlIdentifierGuard.IsValidIdentifier(t.Name)))
                {
                    // Fail closed (like the table-read planner): never silently drop the ORDER BY, which would make paging unstable.
                    throw new GatewayInvalidQueryException("The table cannot be read in a stable order.");
                }

                foreach (var term in defaultOrdering.Terms)
                {
                    // Issue #390: the declared key direction; DESC is a fixed literal chosen by this flag, never catalog text.
                    order.Add((term.Name, term.Descending));
                    Need(term.Name);
                }
            }
            else if (defaultOrdering.Kind == Autheris.Application.DataSources.Services.DefaultOrderingKind.None && path.Count == 0 && node.Offset > 0)
            {
                // No clear orderable column: paging beyond the first page would be unstable (same refusal and text as the planner).
                throw new GatewayInvalidQueryException(Autheris.Application.DataSources.Services.GovernedTableReadPlanner.NoOrderableColumnMessage);
            }
        }

        if (node.Where != null)
        {
            ValidateFilterColumns(node.Where, metadata, tableAccess.Decision, depth: 0);
        }

        EnsureRequiredFilterColumnsSatisfied(metadata, tableAccess, node.Where, joinedChildColumns);

        var children = new List<(TreeRelationNode Relation, NodePlan Child)>();
        foreach (var relation in node.Relations)
        {
            if (!SqlIdentifierGuard.IsValidIdentifier(relation.ResponseKey))
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
            var childPlan = Prepare(relation.Child, context, childPath, relation.ChildColumns);
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
        if (column == null || !SqlIdentifierGuard.IsValidIdentifier(column) || decision.GetEffectiveColumnAccess(column, metadata) == ColumnAccessLevel.Deny)
        {
            throw new GatewayInvalidQueryException($"The property '{Echo(requested)}' does not exist or is not accessible.", "INVALID_QUERY");
        }
        return column;
    }

    /// <summary>filter, sort and join columns must be effectively Clear (no inference on masked/denied values).</summary>
    private static string ResolveClearColumn(TableMetadata metadata, TableAccessDecision decision, string requested)
    {
        var column = metadata.GetColumn(requested?.Trim() ?? string.Empty)?.ColumnName;
        if (column == null || !SqlIdentifierGuard.IsValidIdentifier(column))
        {
            throw new GatewayInvalidQueryException($"The property '{Echo(requested)}' cannot be used for filtering, sorting or joining.", "INVALID_QUERY");
        }
        var access = decision.GetEffectiveColumnAccess(column, metadata);
        if (access == ColumnAccessLevel.Mask)
        {
            throw new FilterOnMaskedColumnException(column);
        }
        if (access != ColumnAccessLevel.Clear)
        {
            throw new GatewayInvalidQueryException($"The property '{Echo(requested)}' cannot be used for filtering, sorting or joining.", "INVALID_QUERY");
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

    private static void EnsureRequiredFilterColumnsSatisfied(
        TableMetadata metadata,
        TreeTableAccess tableAccess,
        TreeFilter? where,
        IReadOnlyList<string>? joinedChildColumns = null)
    {
        if (metadata.RequiredFilterColumns.Length == 0 && metadata.RequiredFilterColumnGroups.Length == 0)
        {
            return;
        }

        bool IsSatisfied(string reqCol)
        {
            // 1. Tenant column
            if (tableAccess.TenantColumn != null &&
                string.Equals(tableAccess.TenantColumn, reqCol, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Active RLS
            if (tableAccess.Decision.RowPolicy is not null)
            {
                try
                {
                    if (DecisionRowFilter.TryResolveTypedPredicate(tableAccess.Decision, out var rlsPredicate) &&
                        rlsPredicate.ReferencedColumns.Contains(reqCol.ToLowerInvariant()))
                    {
                        return true;
                    }
                }
                catch (GatewaySecurityException)
                {
                    // Drifted or unsupported row policy satisfies nothing.
                }
            }

            // 3. Relation join keys (Issue #221)
            if (joinedChildColumns != null &&
                joinedChildColumns.Contains(reqCol, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // 4. Query filter
            return IsColumnConstrained(where, reqCol);
        }

        if (metadata.RequiredFilterColumnGroups.Length > 0)
        {
            bool anyGroupSatisfied = false;
            foreach (var group in metadata.RequiredFilterColumnGroups)
            {
                if (group.Length == 0) continue;
                if (group.All(IsSatisfied))
                {
                    anyGroupSatisfied = true;
                    break;
                }
            }

            if (!anyGroupSatisfied)
            {
                throw new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException(
                    TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(
                        TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing));
            }
        }
        else
        {
            foreach (var reqCol in metadata.RequiredFilterColumns)
            {
                if (IsSatisfied(reqCol))
                {
                    continue;
                }

                throw new TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException(
                    TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FixedMessage(
                        TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.RequiredFilterColumnMissing));
            }
        }
    }

    private static bool IsColumnConstrained(TreeFilter? filter, string column) =>
        TreeFilterClauseBuilder.ExtractConstrainedColumns(filter).Contains(column);

    private static string Echo(string? name) =>
        name != null && SqlIdentifierGuard.IsValidIdentifier(name.Trim()) ? name.Trim() : "(invalid name)";

    private static bool IsHmacRule(MaskingRule rule) => rule.IsHmac;

    private static bool IsStringType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return true;
        var dt = dataType.Trim().ToLowerInvariant();
        if (dt.Contains("interval", StringComparison.Ordinal) || dt.Contains("point", StringComparison.Ordinal) || dt.Contains("polygon", StringComparison.Ordinal) ||
            dt.Contains("geometry", StringComparison.Ordinal) || dt.Contains("geography", StringComparison.Ordinal) || dt.Contains("line", StringComparison.Ordinal) ||
            dt.Contains("json", StringComparison.Ordinal) || dt.Contains("xml", StringComparison.Ordinal) || dt.Contains("uuid", StringComparison.Ordinal) || dt.Contains("guid", StringComparison.Ordinal))
        {
            return true;
        }

        var baseType = dt.Split('(', '[', ' ')[0].Trim();
        if (baseType.StartsWith("bit", StringComparison.Ordinal) || baseType.StartsWith("bool", StringComparison.Ordinal)) return false;
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

    /// <summary>
    /// SQLA-06: true when the row policy cannot be spliced as text. A typed predicate without subqueries is spliced; a predicate with a subquery,
    /// an untyped text filter and any unknown <see cref="IRowPolicy"/> need the engine pipeline (tenant wrapper, coverage verifier) and are denied here.
    /// </summary>
    internal static bool RowPolicyRequiresEnginePipeline(TableAccessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.HasRowFilter && !DecisionRowFilter.IsInlineEligiblePolicyPredicate(decision);
    }

    private static List<string> BuildInnerPredicates(
        NodePlan plan,
        CompileContext context,
        NodePlan? parent,
        TreeRelationNode? relation,
        bool correlate = true)
    {
        var target = Q(context, TargetAlias);
        var metadata = plan.Access.Metadata;
        var securedSource = plan.Access.SecuredSource;
        var where = new List<string>();

        if (!string.IsNullOrWhiteSpace(plan.Access.TenantColumn))
        {
            var tenantColumn = metadata.GetColumn(plan.Access.TenantColumn)?.ColumnName
                ?? throw new GatewayForbiddenException("Access denied.");
            var tenantParam = context.AddParameter(plan.Access.TenantValue);
            where.Add(context.Dialect == DatabaseDialect.Databricks
                ? $"CAST({target}.{Q(context, tenantColumn)} AS BINARY) = CAST({tenantParam} AS BINARY)"
                : $"{target}.{Q(context, tenantColumn)} = {tenantParam}");
        }

        if (plan.Access.Decision.HasRowFilter && securedSource == null)
        {
            // SQLA-06: the spliced text carries no tenant wrapper for policy subqueries (the engine pipeline adds it); deny instead of emitting it.
            if (RowPolicyRequiresEnginePipeline(plan.Access.Decision))
            {
                throw new GatewaySecurityException("The row policy of this table is not supported in tree queries.", "TREE_ROW_POLICY_UNSUPPORTED");
            }

            var rowFilter = plan.Access.Decision.CombinedRowFilterSql!;
            SqlSecurityValidator.ValidateRowFilter(plan.Access.Decision);
            where.Add($"({context.BindRowFilter(rowFilter, plan.Access.Decision.RowFilterParameters)})");
        }

        if (correlate && parent != null && relation != null)
        {
            for (var i = 0; i < relation.ChildColumns.Count; i++)
            {
                var childColumn = metadata.GetColumn(relation.ChildColumns[i])!.ColumnName;
                var parentColumn = parent.Access.Metadata.GetColumn(relation.ParentColumns[i])!.ColumnName;
                where.Add($"{target}.{Q(context, childColumn)} = {Q(context, parent.Alias)}.{Q(context, parentColumn)}");
            }
        }

        if (plan.Node.Where != null)
        {
            where.Add(BuildFilter(plan.Node.Where, metadata, context, target));
        }

        return where;
    }

    private static string BuildInner(NodePlan plan, CompileContext context, NodePlan? parent, TreeRelationNode? relation)
    {
        var dialect = context.Dialect;
        var target = Q(context, TargetAlias);
        var metadata = plan.Access.Metadata;
        var node = plan.Node;
        var isRoot = parent == null;
        var limit = relation is { IsList: false } ? 1 : (node.ExecutionLimit ?? node.Limit);

        var sb = new StringBuilder("SELECT ");
        if (!isRoot && dialect == DatabaseDialect.SqlServer)
        {
            sb.Append("TOP (").Append(limit.ToString(CultureInfo.InvariantCulture)).Append(") ");
        }
        sb.Append(string.Join(", ", plan.InnerColumns.Select(c => $"{target}.{Q(context, c)} AS {Q(context, c)}")));
        var securedSource = plan.Access.SecuredSource;
        if (securedSource != null)
        {
            // SQLA-06 follow-up: the derived table carries the engine-applied tenant and row policy; it projects every catalog column by name.
            sb.Append(" FROM (").Append(BindSecuredSource(context, securedSource)).Append(')');
        }
        else
        {
            sb.Append(" FROM ").Append(dialect.FormatTableIdentifier(metadata.Identifier));
        }

        sb.Append(" AS ").Append(target);

        var where = BuildInnerPredicates(plan, context, parent, relation, correlate: true);

        // SQL Server rejects FETCH NEXT 0 ROWS (provider error). A zero page there fetches one row behind a constant-false predicate.
        if (limit == 0 && isRoot && dialect == DatabaseDialect.SqlServer)
        {
            where.Add("1 = 0");
            limit = 1;
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

    private static string ColumnExpression(NodePlan plan, CompileContext context, string column, string? rowAlias = null)
    {
        var quoted = $"{Q(context, rowAlias ?? plan.Alias)}.{Q(context, column)}";
        var dataType = plan.Access.Metadata.GetColumn(column)?.DataType?.Trim().ToLowerInvariant();
        if (dataType is "geometry" or "geography" or "spatial" or "point" or "polygon" or "linestring" or "multipolygon" or "multipoint")
        {
            return context.Dialect switch
            {
                DatabaseDialect.SqlServer => $"{quoted}.STAsText()",
                DatabaseDialect.PostgreSql => $"ST_AsGeoJSON({quoted})",
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
        return quoted;
    }

    private static List<(string Key, string Expression)> ObjectPairs(NodePlan plan, CompileContext context, string? rowAlias = null)
    {
        var pairs = new List<(string Key, string Expression)>(plan.Outputs.Count + plan.Children.Count);
        foreach (var output in plan.Outputs)
        {
            pairs.Add((output.Key, output.LiteralExpression ?? ColumnExpression(plan, context, output.SourceColumn!, rowAlias)));
        }
        foreach (var (relation, child) in plan.Children)
        {
            pairs.Add((relation.ResponseKey, BuildChild(child, context, plan, relation)));
        }
        return pairs;
    }

    private static string BuildRoot(NodePlan plan, CompileContext context)
    {
        bool hasSetBasedChild = plan.Children.Any(c => c.Relation.IsList &&
            plan.ListStrategies.TryGetValue(c.Relation, out var s) && s.Strategy == TreeListStrategy.SetBased);

        if (hasSetBasedChild)
        {
            return BuildSetBasedRoot(plan, context);
        }

        return BuildCorrelatedRoot(plan, context);
    }

    private static string BuildCorrelatedRoot(NodePlan plan, CompileContext context)
    {
        if (context.Dialect == DatabaseDialect.Databricks)
        {
            return BuildDatabricksCorrelatedRoot(plan, context);
        }

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
            _ => throw new NotSupportedException($"Tree SQL compiler does not support dialect '{context.Dialect}'.")
        };
    }

    private static string BuildChild(NodePlan plan, CompileContext context, NodePlan parent, TreeRelationNode relation)
    {
        if (context.Dialect == DatabaseDialect.Databricks)
        {
            return BuildDatabricksChild(plan, context, parent, relation);
        }

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
                throw new NotSupportedException($"Tree SQL compiler does not support dialect '{context.Dialect}'.");
        }
    }

    private static string SqlServerSelectList(NodePlan plan, CompileContext context) =>
        string.Join(", ", ObjectPairs(plan, context).Select(p => $"{p.Expression} AS {Q(context, p.Key)}"));

    private static string PostgresObject(NodePlan plan, CompileContext context) =>
        BuildPostgresObject(ObjectPairs(plan, context), context);

    private static string BuildPostgresObject(IEnumerable<(string Key, string Expression)> pairs, CompileContext context)
    {
        var chunks = pairs
            .Chunk(ObjectChunkSize)
            .Select(chunk => "jsonb_build_object(" + string.Join(", ", chunk.Select(p => $"'{FormatPostgresJsonKey(p.Key, context.Dialect)}', {p.Expression}")) + ")")
            .ToList();
        return chunks.Count == 0 ? "'{}'::jsonb" : string.Join(" || ", chunks);
    }

    private static string FormatPostgresJsonKey(string key, DatabaseDialect dialect)
    {
        if (!SqlIdentifierGuard.IsValidIdentifier(key))
        {
            throw new GatewayInvalidQueryException("Invalid property name.");
        }

        return dialect.EscapeSqlLiteral(key);
    }

    // ---------------------------------------------------------------- filters

    private static string BuildFilter(TreeFilter filter, TableMetadata metadata, CompileContext context, string target) =>
        TreeFilterRenderer.Render(filter, new CompilerFilterTarget(metadata, context, target));

    /// <summary>Dialect rendering of the shared filter shape: qualified quoted columns, <c>@gqN</c> parameters, backslash LIKE escape.</summary>
    private sealed class CompilerFilterTarget(TableMetadata metadata, CompileContext context, string target) : ITreeFilterTarget
    {
        public string Column(string column) => $"{target}.{Q(context, metadata.GetColumn(column)!.ColumnName)}";

        public string AddParameter(object? value) => context.AddParameter(value);

        public char LikeEscape => '\\';

        public string LikeEscapeLiteral => context.Dialect == DatabaseDialect.Databricks ? "'\\\\'" : $"'{LikeEscape}'";

        public string LikeSpecials => context.Dialect == DatabaseDialect.Databricks ? "%_" : "%_[";

        public bool AlwaysEmitLikeEscape => true;
    }
}

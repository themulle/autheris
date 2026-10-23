namespace Autheris.Application.Sql.Federation.Subtrees;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;

/// <summary>
/// Issue #123 (WP-123-2, SEC-DATA123-01..03) / Issue #132 (WP-132-3, SEC-DATA132-02, SEC-DATA132-04):
/// Compiles same-source AST subtrees and co-located SQL Server subtrees strictly via governed compilation,
/// embedding universal member RLS conjunctions, isolated parameter naming, and transitive masking heritage.
/// </summary>
public static class SubtreeCompiler
{
    private static readonly Regex ParameterMarkerRegex = new(
        @"(?:@|:|\$)\b([a-zA-Z0-9_]+)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// SEC-DATA132-04: Cross-Database Policy Isolation.
    /// Asserts that table-level RLS policies (CombinedRowFilterSql) do not reference tables in other databases.
    /// Correlated cross-database RLS policies fail closed.
    /// </summary>
    public static void ValidatePolicyIsolation(
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);

        foreach (var table in tables)
        {
            var id = table.Metadata.Identifier;
            if (!decisions.TryGetValue(id, out var dec) || dec == null) continue;

            if (!string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql))
            {
                var rowFilter = dec.CombinedRowFilterSql;
                var currentDb = table.Metadata.Table.PhysicalDatabaseName ?? table.Metadata.Identifier.Domain;

                foreach (var other in tables)
                {
                    var otherDb = other.Metadata.Table.PhysicalDatabaseName ?? other.Metadata.Identifier.Domain;
                    if (string.Equals(currentDb, otherDb, StringComparison.OrdinalIgnoreCase)) continue;

                    // Correlated subquery or table reference spanning to another database
                    if (rowFilter.Contains(otherDb, StringComparison.OrdinalIgnoreCase) ||
                        rowFilter.Contains(other.Metadata.Identifier.TableName, StringComparison.OrdinalIgnoreCase) ||
                        rowFilter.Contains(other.Target.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new GatewaySecurityException(
                            $"Cross-database correlated row filter subqueries are not permitted; policy on '{id}' references external table or database '{otherDb}'.",
                            GovernedSqlErrorCodes.PolicyDenied);
                    }
                }
            }
        }
    }

    public static SubtreeExecutionPlan Compile(
        SourceSubtree subtree,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        ISqlEngine sqlEngine,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subtree);
        ArgumentNullException.ThrowIfNull(user);
        // SQLA-14: the subtree tenant predicate binds the caller's tenant; there is no "default" tenant fallback.
        if (string.IsNullOrWhiteSpace(tenantId.Value))
        {
            throw new ArgumentException("A subtree can only be compiled for a tenant.", nameof(tenantId));
        }

        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sqlEngine);

        // 1. Universal Authorization check (SEC-DATA123-02, fail-closed)
        var memberDecisions = new Dictionary<TableIdentifier, TableAccessDecision>();
        foreach (var member in subtree.MemberTables)
        {
            var dec = CrossSourcePlanner.RequireDecision(decisions, member);
            if (!dec.IsAllowed)
            {
                throw new WebSqlTableDeniedException(
                    $"Zero-Trust Violation: Access denied to table '{member.Target.FullName}'. Denied reasons: {string.Join("; ", dec.DeniedReasons)}");
            }
            memberDecisions[member.Metadata.Identifier] = dec;
        }

        // 2. Validate function allowlists for target dialect (V-4, SEC-DATA123-01)
        var dialect = subtree.Dialect;
        // GAP-013 / SEC-PG-03: federated WebSQL translates the Trino date functions like single-source WebSQL (one behaviour per capability).
        var allowlist = SqlFunctionAllowlists.WithDateTranslation(SqlFunctionAllowlists.GetDefault(dialect));
        var functionsInSubtree = CollectFunctionCalls(subtree);
        foreach (var func in functionsInSubtree)
        {
            if (!allowlist.Contains(func))
            {
                throw new WebSqlPolicyException($"Function '{func}' is not permitted in {dialect} queries.");
            }
        }

        // 3. Universal RLS Conjunction for ALL member tables (SEC-DATA123-02)
        Expression? combinedWhere = null;
        foreach (var pred in subtree.PushablePredicates)
        {
            combinedWhere = combinedWhere == null ? pred : new BinaryExpression(combinedWhere, BinaryOperator.And, pred);
        }

        foreach (var member in subtree.MemberTables)
        {
            var dec = memberDecisions[member.Metadata.Identifier];
            if (!string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql))
            {
                var (exprTree, _) = sqlEngine.ParseExpression(dec.CombinedRowFilterSql.AsMemory(), null, ct);
                var rlsExpr = new SqlAstBuilder().BuildExpression(exprTree.expression());
                combinedWhere = combinedWhere == null ? rlsExpr : new BinaryExpression(combinedWhere, BinaryOperator.And, rlsExpr);
            }
        }

        // 4. Assemble isolated QuerySpecification AST
        var querySpec = new QuerySpecification(
            Distinct: false,
            Projections: subtree.ProjectedItems,
            From: subtree.RootTableSource,
            Where: combinedWhere,
            GroupBy: null,
            Having: null);

        var selectStmt = new SelectStatement(null, querySpec, null, null);

        // 5. Emit canonical ANSI SQL for compile pass (zero manual string concatenation)
        var ansiGenerator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.Ansi);
        var canonicalSql = ansiGenerator.GenerateSql(selectStmt);

        // 6. Governed Compilation via ISqlEngine.Compile (SEC-DATA123-01)
        var entries = subtree.MemberTables.Select(m => new TableCatalogEntry(
            new TableIdentity(m.Metadata.Table.SchemaName, m.Metadata.Table.TableName, null),
            m.Metadata.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
            m.Metadata.TenantColumnName,
            0)).ToList();

        var inMemoryCatalog = new InMemoryTableCatalog(entries, "public");
        var policy = new GovernancePolicy
        {
            RowFilters = new SubtreeNoOpRowPolicyProvider(entries),
            Masks = new SubtreeNoOpMaskProvider(),
            Catalog = inMemoryCatalog,
            Tenant = new TenantBinding("__autheris_tenant", tenantId.Value, SqlParameterType.String)
        };

        var compileRequest = CompileRequestFactory.Create(
            policy,
            dialect,
            new WebSqlOptions(),
            isDml: false,
            enforcedMaxRows: 0,
            allowedFunctions: allowlist,
            translateDateFunctions: true);

        var compiled = sqlEngine.Compile(canonicalSql.AsMemory(), compileRequest, ct);

        // 7. Isolated Parameter Isolation (@p_st_<n>) (SEC-DATA123-01)
        var isolatedParams = new Dictionary<string, object?>();
        string isolatedSql = compiled.Sql;

        if (compiled.Parameters.Length > 0)
        {
            var paramMatches = ParameterMarkerRegex.Matches(compiled.Sql);
            var seenMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int paramIndex = 0;

            foreach (Match m in paramMatches)
            {
                var originalMarker = m.Value;
                if (!seenMarkers.TryGetValue(originalMarker, out var isolatedMarker))
                {
                    isolatedMarker = $"@p_{subtree.SubtreeId}_{paramIndex}";
                    seenMarkers[originalMarker] = isolatedMarker;

                    object? paramVal = paramIndex < compiled.Parameters.Length ? compiled.Parameters[paramIndex].Value : null;
                    isolatedParams[isolatedMarker] = paramVal;
                    paramIndex++;
                }
            }

            foreach (var (orig, isolated) in seenMarkers.OrderByDescending(kv => kv.Key.Length))
            {
                isolatedSql = Regex.Replace(isolatedSql, Regex.Escape(orig) + @"\b", isolated);
            }
        }

        // 8. Transitive Masking Heritage (SEC-DATA123-03)
        var maskedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectedColNames = new List<string>(subtree.ProjectedItems.Count);

        foreach (var proj in subtree.ProjectedItems)
        {
            if (proj is ColumnSelectItem colSelect)
            {
                var outName = colSelect.Alias?.Value ?? ExtractColumnName(colSelect.Expression);
                projectedColNames.Add(outName);

                var refs = new List<ColumnReference>();
                CollectColumnReferences(colSelect.Expression, refs);

                foreach (var cr in refs)
                {
                    var colName = cr.Name.Parts[^1].Value;
                    var qual = cr.Name.Parts.Count > 1 ? cr.Name.Parts[^2].Value : null;

                    foreach (var m in subtree.MemberTables)
                    {
                        if (qual == null || MatchesTable(qual, m))
                        {
                            if (m.Metadata.HasColumn(colName))
                            {
                                var dec = memberDecisions[m.Metadata.Identifier];
                                var lvl = dec.GetEffectiveColumnAccess(colName, m.Metadata);
                                if (lvl == ColumnAccessLevel.Mask ||
                                    (m.Metadata.ColumnMaskingRules != null && m.Metadata.ColumnMaskingRules.ContainsKey(colName)))
                                {
                                    maskedColumns.Add(outName);
                                }
                            }
                        }
                    }
                }
            }
            else if (proj is WildcardSelectItem)
            {
                foreach (var m in subtree.MemberTables)
                {
                    var dec = memberDecisions[m.Metadata.Identifier];
                    foreach (var c in m.Metadata.Columns)
                    {
                        projectedColNames.Add(c.ColumnName);
                        var lvl = dec.GetEffectiveColumnAccess(c.ColumnName, m.Metadata);
                        if (lvl == ColumnAccessLevel.Mask ||
                            (m.Metadata.ColumnMaskingRules != null && m.Metadata.ColumnMaskingRules.ContainsKey(c.ColumnName)))
                        {
                            maskedColumns.Add(c.ColumnName);
                        }
                    }
                }
            }
        }

        // 9. Synthesize TableMetadata
        var syntheticId = new TableIdentifier(subtree.SourceName, "public", subtree.SubtreeId);
        var syntheticMaskingRules = new Dictionary<string, MaskingRule>();
        foreach (var mc in maskedColumns)
        {
            syntheticMaskingRules[mc] = new MaskingRule { RuleType = "REDACT" };
        }

        var syntheticMetadata = new TableMetadata
        {
            Identifier = syntheticId,
            Table = new Table
            {
                TableName = subtree.SubtreeId,
                SchemaName = "public",
                DataSourceType = DataSourceType.Sql,
                SourceName = subtree.SourceName,
                IsActive = true
            },
            Columns = projectedColNames.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new TableColumn { ColumnName = c, DataType = "varchar" })
                .ToList(),
            ColumnMaskingRules = syntheticMaskingRules
        };

        return new SubtreeExecutionPlan(
            SubtreeId: subtree.SubtreeId,
            SourceName: subtree.SourceName,
            Dialect: dialect,
            MemberTables: subtree.MemberTables,
            Compiled: compiled,
            ProjectedColumns: projectedColNames,
            MaskedColumns: maskedColumns.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SyntheticMetadata: syntheticMetadata,
            Parameters: isolatedParams,
            SqlOverride: isolatedSql);
    }

    private static HashSet<string> CollectFunctionCalls(SourceSubtree subtree)
    {
        var functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void InspectNode(SqlNode node)
        {
            if (node is FunctionCallExpression func)
            {
                functions.Add(func.Name.Parts[^1].Value);
            }

            foreach (var prop in node.GetType().GetProperties())
            {
                if (typeof(SqlNode).IsAssignableFrom(prop.PropertyType))
                {
                    if (prop.GetValue(node) is SqlNode child)
                    {
                        InspectNode(child);
                    }
                }
                else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(string))
                {
                    if (prop.GetValue(node) is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            if (item is SqlNode childNode)
                            {
                                InspectNode(childNode);
                            }
                        }
                    }
                }
            }
        }

        foreach (var p in subtree.PushablePredicates) InspectNode(p);
        foreach (var item in subtree.ProjectedItems) InspectNode(item);
        InspectNode(subtree.RootTableSource);

        return functions;
    }

    private static void CollectColumnReferences(SqlNode node, List<ColumnReference> list)
    {
        if (node is ColumnReference cr)
        {
            list.Add(cr);
            return;
        }

        foreach (var prop in node.GetType().GetProperties())
        {
            if (typeof(SqlNode).IsAssignableFrom(prop.PropertyType))
            {
                if (prop.GetValue(node) is SqlNode child)
                {
                    CollectColumnReferences(child, list);
                }
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(prop.PropertyType) && prop.PropertyType != typeof(string))
            {
                if (prop.GetValue(node) is System.Collections.IEnumerable enumerable)
                {
                    foreach (var item in enumerable)
                    {
                        if (item is SqlNode childNode)
                        {
                            CollectColumnReferences(childNode, list);
                        }
                    }
                }
            }
        }
    }

    private static string ExtractColumnName(Expression expr)
    {
        if (expr is ColumnReference cr) return cr.Name.Parts[^1].Value;
        return "col";
    }

    private static bool MatchesTable(string qualifier, ResolvedSourceTable table)
    {
        return string.Equals(qualifier, table.Target.Alias, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Target.TableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Target.FullName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(qualifier, table.Metadata.Identifier.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SubtreeNoOpRowPolicyProvider : IPolicyPredicateProvider
    {
        private readonly HashSet<TableIdentity> _tables;

        public SubtreeNoOpRowPolicyProvider(IEnumerable<TableCatalogEntry> entries)
        {
            _tables = new HashSet<TableIdentity>(entries.Select(e => e.Identity));
        }

        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class SubtreeNoOpMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) =>
            throw new InvalidOperationException("No masks configured on subtree compile pass.");
    }
}

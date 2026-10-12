namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;

public sealed record CrossSourcePlan(
    string GeneratedDuckDbSql,
    IReadOnlyList<StagingTableRequest> StagingRequests,
    IReadOnlyList<string> StagingNames);

public sealed record VirtualFilterJoinSpec(
    string TargetTableQualifiedName,
    VirtualFilter Filter,
    FilterBinding Binding,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> AllowedKeyTuples);

public interface ICrossSourcePlanner
{
    CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<string, TableAccessDecision> decisions,
        CrossSourceOptions options,
        IReadOnlyList<VirtualFilterJoinSpec>? virtualFilterJoins = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? targetCustomArguments = null);
}

public sealed class CrossSourcePlanner : ICrossSourcePlanner
{
    private readonly ISqlEngine _sqlEngine;

    public CrossSourcePlanner(ISqlEngine sqlEngine)
    {
        _sqlEngine = sqlEngine ?? throw new ArgumentNullException(nameof(sqlEngine));
    }

    public CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<string, TableAccessDecision> decisions,
        CrossSourceOptions options) =>
        Plan(rawSql, metadata, tables, decisions, options, null, null);

    public CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<string, TableAccessDecision> decisions,
        CrossSourceOptions options,
        IReadOnlyList<VirtualFilterJoinSpec>? virtualFilterJoins = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? targetCustomArguments = null)
    {
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(options);

        // 1. DuckDB Function allowlist check (Phase 3)
        if (metadata.FunctionCalls is { Count: > 0 })
        {
            var allowlist = SqlFunctionAllowlists.DuckDb;
            foreach (var func in metadata.FunctionCalls)
            {
                if (!allowlist.Contains(func))
                {
                    throw new WebSqlPolicyException($"Function '{func}' is not permitted in DuckDB federated queries.");
                }
            }
        }

        // 2. Guardrails SEC-JOIN-01 and SEC-FILTER-01 on all tables (INV-4)
        foreach (var t in tables)
        {
            if (decisions.TryGetValue(t.Metadata.Identifier.ToQualifiedName(), out var decision))
            {
                GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(t.Target, t.Metadata, decision, metadata);
            }
        }

        // 3. Non-equi join / Cartesian product check (INV-12)
        if (!options.AllowNonEquiJoins && metadata.JoinCount > 0)
        {
            if (metadata.JoinColumnReferences == null || metadata.JoinColumnReferences.Count == 0)
            {
                throw new WebSqlPolicyException("Cartesian product / CROSS JOIN across multiple data sources is not permitted.");
            }
        }

        // 4. Staging names and minimal projections
        var targetToStaging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tables)
        {
            tableNameCounts[t.Target.TableName] = tableNameCounts.GetValueOrDefault(t.Target.TableName) + 1;
        }

        var stagingRequests = new List<StagingTableRequest>(tables.Count);
        var stagingNames = new List<string>(tables.Count);

        for (int i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            string stagingName = $"s{i}";
            stagingNames.Add(stagingName);

            string? alias = t.Target.Alias;
            if (string.IsNullOrWhiteSpace(alias) && tableNameCounts[t.Target.TableName] == 1)
            {
                alias = t.Target.TableName;
            }

            targetToStaging[t.Target.FullName] = stagingName;
            targetToStaging[t.Metadata.Identifier.ToQualifiedName()] = stagingName;
            targetToStaging[t.Target.TableName] = stagingName;

            if (!string.IsNullOrWhiteSpace(alias))
            {
                targetToAlias[t.Target.FullName] = alias;
                targetToAlias[t.Metadata.Identifier.ToQualifiedName()] = alias;
                targetToAlias[t.Target.TableName] = alias;
                if (!string.IsNullOrWhiteSpace(t.Target.Alias))
                {
                    targetToAlias[t.Target.Alias] = alias;
                }
            }

            // Minimal projection
            decisions.TryGetValue(t.Metadata.Identifier.ToQualifiedName(), out var dec);
            dec ??= TableAccessDecision.Allowed(t.Metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

            var projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool selectAll = metadata.ProjectedColumns.Any(p => p == "*");

            foreach (var col in t.Metadata.Columns)
            {
                if (dec.GetColumnAccess(col.ColumnName) == ColumnAccessLevel.Deny)
                    continue;

                bool referenced = selectAll ||
                    metadata.ProjectedColumns.Any(p => MatchesProjectedColumn(p, col.ColumnName, t.Target)) ||
                    (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Any(jc => GovernedSqlExecutionService.ReferencesColumn(jc.TableOrAlias, jc.ColumnName, col.ColumnName, t.Target))) ||
                    (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Any(fc => GovernedSqlExecutionService.ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, t.Target)));

                if (referenced)
                {
                    projected.Add(col.ColumnName);
                }
            }

            if (projected.Count == 0)
            {
                foreach (var col in t.Metadata.Columns)
                {
                    if (dec.GetColumnAccess(col.ColumnName) != ColumnAccessLevel.Deny)
                        projected.Add(col.ColumnName);
                }
            }

            var qualName = t.Metadata.Identifier.ToQualifiedName();
            var sessionItems = new Dictionary<string, object?>();
            if (t.Metadata.Table.DataSourceType == DataSourceType.HttpDeclarative || dec.AppliedVirtualFilters is { Count: > 0 })
            {
                sessionItems[Autheris.Application.Connectors.GovernedConnectorReader.VirtualFilterFederationHandledKey] = true;
            }

            IReadOnlyDictionary<string, object?>? customArgs = null;
            if (targetCustomArguments != null && targetCustomArguments.TryGetValue(qualName, out var ca))
            {
                customArgs = ca;
            }

            stagingRequests.Add(new StagingTableRequest(
                t.Target,
                t.Metadata,
                dec,
                stagingName,
                projected.ToList(),
                PushdownFilter: null,
                SessionItems: sessionItems.Count > 0 ? sessionItems : null,
                CustomArguments: customArgs));
        }

        // 5. AST rewrite to target s0, s1, etc.
        var (tree, _) = _sqlEngine.Parse(rawSql.AsMemory());
        var builder = new SqlAstBuilder();
        var ast = builder.BuildStatement(tree);

        var rewriter = new FederationAstRewriter(targetToStaging, targetToAlias, tables);
        var rewrittenAst = (SqlStatement)rewriter.Visit(ast);

        if (virtualFilterJoins != null && virtualFilterJoins.Count > 0)
        {
            foreach (var vf in virtualFilterJoins)
            {
                string vfStagingName = $"s{stagingRequests.Count}";
                stagingNames.Add(vfStagingName);

                var keyCols = vf.Filter.KeyColumns.Select(VirtualFilterNames.ColumnOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var vfColumns = keyCols.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList();
                var vfMeta = new TableMetadata
                {
                    Identifier = new TableIdentifier(vf.Filter.Source, "virtual_filter", vf.Filter.Name),
                    Table = new Table { DataSourceType = DataSourceType.Sql, IsActive = true },
                    Columns = vfColumns
                };

                var vfTarget = new TableAccessTarget(vf.Filter.Source, "virtual_filter", vf.Filter.Name, null, $"{vf.Filter.Source}.virtual_filter.{vf.Filter.Name}");
                var vfDecision = TableAccessDecision.Allowed(vfMeta.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

                stagingRequests.Add(new StagingTableRequest(
                    Reference: vfTarget,
                    Metadata: vfMeta,
                    Decision: vfDecision,
                    StagingName: vfStagingName,
                    Projection: keyCols,
                    PreloadedRows: vf.AllowedKeyTuples));

                string targetStaging = targetToStaging.TryGetValue(vf.TargetTableQualifiedName, out var ts) ? ts : "s0";
                string effectiveTarget = targetToAlias.TryGetValue(vf.TargetTableQualifiedName, out var ta) ? ta : targetStaging;

                var pairConditions = new List<Expression>();
                foreach (var keyCol in vf.Filter.KeyColumns)
                {
                    var filterKey = VirtualFilterNames.ColumnOf(keyCol);
                    var targetCol = vf.Binding.ColumnMap != null && vf.Binding.ColumnMap.TryGetValue(filterKey, out var mapped)
                        ? mapped
                        : filterKey;

                    pairConditions.Add(new BinaryExpression(
                        new ColumnReference(new SqlQualifiedName([new SqlIdentifier(effectiveTarget, false), new SqlIdentifier(targetCol, false)])),
                        BinaryOperator.Equal,
                        new ColumnReference(new SqlQualifiedName([new SqlIdentifier(vfStagingName, false), new SqlIdentifier(filterKey, false)]))));
                }

                Expression? overallCondition = null;
                foreach (var pair in pairConditions)
                {
                    overallCondition = overallCondition == null
                        ? pair
                        : new BinaryExpression(overallCondition, BinaryOperator.And, pair);
                }

                if (rewrittenAst is SelectStatement selectStatement && selectStatement.Body is QuerySpecification querySpec && querySpec.From != null && overallCondition != null)
                {
                    rewrittenAst = selectStatement with
                    {
                        Body = querySpec with
                        {
                            From = new JoinedTableSource(
                                querySpec.From,
                                JoinType.Inner,
                                new NamedTableSource(new SqlQualifiedName([new SqlIdentifier(vfStagingName, false)]), null),
                                new OnJoinCondition(overallCondition))
                        }
                    };
                }
            }
        }

        var generator = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.DuckDb);
        var duckSql = generator.GenerateSql(rewrittenAst);

        return new CrossSourcePlan(duckSql, stagingRequests, stagingNames);
    }

    private static bool MatchesProjectedColumn(string p, string colName, TableAccessTarget target)
    {
        var dotIdx = p.LastIndexOf('.');
        if (dotIdx >= 0)
        {
            var tableOrAlias = p[..dotIdx];
            var referencedCol = p[(dotIdx + 1)..];
            return GovernedSqlExecutionService.ReferencesColumn(tableOrAlias, referencedCol, colName, target);
        }
        return string.Equals(p, colName, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FederationAstRewriter : SqlAstRewriter
    {
        private readonly Dictionary<string, string> _targetToStaging;
        private readonly Dictionary<string, string> _targetToAlias;
        private readonly IReadOnlyList<ResolvedSourceTable> _tables;

        public FederationAstRewriter(
            Dictionary<string, string> targetToStaging,
            Dictionary<string, string> targetToAlias,
            IReadOnlyList<ResolvedSourceTable> tables)
        {
            _targetToStaging = targetToStaging;
            _targetToAlias = targetToAlias;
            _tables = tables;
        }

        public override SqlNode VisitNamedTableSource(NamedTableSource node)
        {
            var rawName = string.Join(".", node.Name.Parts.Select(p => p.Value));
            string? stagingName = null;

            if (_targetToStaging.TryGetValue(rawName, out var s))
            {
                stagingName = s;
            }
            else
            {
                var lastName = node.Name.Parts[^1].Value;
                if (_targetToStaging.TryGetValue(lastName, out var sLast))
                {
                    stagingName = sLast;
                }
            }

            stagingName ??= "s0";

            SqlIdentifier? alias = node.Alias;
            if (alias == null && _targetToAlias.TryGetValue(rawName, out var a))
            {
                alias = new SqlIdentifier(a, false);
            }
            else if (alias == null && _targetToAlias.TryGetValue(node.Name.Parts[^1].Value, out var aLast))
            {
                alias = new SqlIdentifier(aLast, false);
            }

            return new NamedTableSource(
                new SqlQualifiedName([new SqlIdentifier(stagingName, false)]),
                alias);
        }

        public override SqlNode VisitColumnReference(ColumnReference node)
        {
            if (node.Name.Parts.Count <= 1)
            {
                return node;
            }

            var colName = node.Name.Parts[^1];
            var qualifierParts = node.Name.Parts.Take(node.Name.Parts.Count - 1).Select(p => p.Value).ToList();
            var rawQualifier = string.Join(".", qualifierParts);

            string? effectiveQualifier = null;
            if (_targetToAlias.TryGetValue(rawQualifier, out var a))
            {
                effectiveQualifier = a;
            }
            else if (_targetToStaging.TryGetValue(rawQualifier, out var s))
            {
                effectiveQualifier = s;
            }
            else if (qualifierParts.Count > 0 && _targetToAlias.TryGetValue(qualifierParts[^1], out var aLast))
            {
                effectiveQualifier = aLast;
            }
            else if (qualifierParts.Count > 0 && _targetToStaging.TryGetValue(qualifierParts[^1], out var sLast))
            {
                effectiveQualifier = sLast;
            }

            if (effectiveQualifier != null)
            {
                return new ColumnReference(new SqlQualifiedName([new SqlIdentifier(effectiveQualifier, false), colName]));
            }

            return node;
        }

        public override SqlNode VisitWildcardSelectItem(WildcardSelectItem node)
        {
            if (node.Qualifier == null || node.Qualifier.Parts.Count == 0)
            {
                return node;
            }

            var rawQualifier = string.Join(".", node.Qualifier.Parts.Select(p => p.Value));
            string? effectiveQualifier = null;
            if (_targetToAlias.TryGetValue(rawQualifier, out var a))
            {
                effectiveQualifier = a;
            }
            else if (_targetToStaging.TryGetValue(rawQualifier, out var s))
            {
                effectiveQualifier = s;
            }

            if (effectiveQualifier != null)
            {
                return new WildcardSelectItem(new SqlQualifiedName([new SqlIdentifier(effectiveQualifier, false)]));
            }

            return node;
        }
    }
}

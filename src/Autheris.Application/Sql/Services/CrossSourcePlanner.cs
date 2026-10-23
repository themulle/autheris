namespace Autheris.Application.Sql.Services;

using System;
using System.Security.Claims;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Sql.Federation.Conjuncts;
using Autheris.Application.Sql.Federation.KeySet;
using Autheris.Application.Sql.Federation.Pushdown;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Autheris.Application.Sql.Federation.Optimization;
using Autheris.Application.Sql.Federation.Parameters;

public sealed record CrossSourcePlan(
    CompiledSql Compiled,
    IReadOnlyList<StagingTableRequest> StagingRequests,
    IReadOnlyList<string> StagingNames)
{
    /// <summary>The DuckDB text with <c>$n</c> markers; every value is bound through <see cref="Compiled"/>.</summary>
    public string GeneratedDuckDbSql => Compiled.Sql;
}

public interface ICrossSourcePlanner
{
    CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? virtualFilterPlans = null,
        SqlStatement? statement = null,
        FederatedParameterContract? parameters = null);
}

public sealed class CrossSourcePlanner : ICrossSourcePlanner
{
    private readonly ISqlEngine _sqlEngine;
    private readonly Autheris.Application.Sql.TimeZones.ITimeZoneConversionState? _timeZoneState;

    public CrossSourcePlanner(ISqlEngine sqlEngine, Autheris.Application.Sql.TimeZones.ITimeZoneConversionState? timeZoneState = null)
    {
        _sqlEngine = sqlEngine ?? throw new ArgumentNullException(nameof(sqlEngine));
        _timeZoneState = timeZoneState;   // null = conversion unavailable (fail closed)
    }

    /// <summary>
    /// SEC-1 (F-GOV-14): the one key of the access decision of a cross-source table: the full, source-qualified
    /// identifier (<c>domain.schema.table</c>, case-insensitive). Never the schema-qualified name, which collides for
    /// tables of different sources.
    /// </summary>
    public static TableIdentifier DecisionKey(ResolvedSourceTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.Metadata.Identifier;
    }

    /// <summary>The decision of a table; a missing decision denies (fail closed), it is never defaulted to "allowed".</summary>
    public static TableAccessDecision RequireDecision(
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        ResolvedSourceTable table)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        if (decisions.TryGetValue(DecisionKey(table), out var decision) && decision != null)
        {
            return decision;
        }

        throw new GatewaySecurityException(
            $"No access decision exists for table '{DecisionKey(table)}'; the cross-source query is denied.",
            VirtualFilterErrorCodes.EnforcementIncomplete);
    }

    /// <summary>
    /// Plans the staging requests and the DuckDB query. <paramref name="virtualFilterPlans"/> (WI-6) are attached to the
    /// staging request of their table; the user query is never rewritten for Virtual Filters. <paramref name="statement"/>
    /// is the already parsed user statement (parsed here when it is not supplied).
    /// </summary>
    public CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options,
        ClaimsPrincipal user,
        TenantId tenantId,
        IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? virtualFilterPlans = null,
        SqlStatement? statement = null,
        FederatedParameterContract? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(user);
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

        // 2. Every table has its own decision (SEC-1, fail closed); guardrails SEC-JOIN-01 and SEC-FILTER-01 (INV-4)
        var tableDecisions = new TableAccessDecision[tables.Count];
        for (int i = 0; i < tables.Count; i++)
        {
            tableDecisions[i] = RequireDecision(decisions, tables[i]);
        }

        var requiredFilterPending = new HashSet<TableIdentifier>();
        for (int i = 0; i < tables.Count; i++)
        {
            GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(tables[i].Target, tables[i].Metadata, tableDecisions[i], metadata);

            var t = tables[i];
            var dec = tableDecisions[i];
            if (!RequiredFiltersSatisfied(t, dec, clientFilterColumns: null))
            {
                // PARITY-GAP-017: a pushed column predicate satisfies the rule too; a single staged table is judged again once its pushdown
                // filter exists (below), every other table (subtree member) is rejected right after the partition. Never skipped.
                requiredFilterPending.Add(DecisionKey(t));
            }
        }

        // A Virtual Filter plan for a table that is not part of the query cannot be attached; deny instead of ignoring it.
        if (virtualFilterPlans != null)
        {
            foreach (var planned in virtualFilterPlans.Keys)
            {
                if (!tables.Any(t => DecisionKey(t) == planned))
                {
                    throw new GatewaySecurityException(
                        $"A Virtual Filter plan targets '{planned}', which is not part of the query; the query is denied.",
                        VirtualFilterErrorCodes.EnforcementIncomplete);
                }
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

        if (statement == null)
        {
            var (tree, _) = _sqlEngine.Parse(rawSql.AsMemory());
            statement = new SqlAstBuilder().BuildStatement(tree);
        }

        // 4. Staging names and minimal projections
        var targetToStaging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identifierToStaging = new Dictionary<TableIdentifier, string>();
        var identifierToAlias = new Dictionary<TableIdentifier, string>();
        var tableNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Issue #122: AST Conjunct Splitting and Single-Table Classification
        Dictionary<TableIdentifier, List<SingleTableConjunct>>? conjunctsByTable = null;
        if (options.PredicatePushdown.Enabled || options.DynamicKeySet.Enabled)
        {
            var conjuncts = SqlConjunctSplitter.Split(statement);
            var classification = SingleTableConjunctClassifier.Classify(conjuncts, tables);
            conjunctsByTable = classification.SingleTableConjuncts
                .GroupBy(c => c.Table)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // Issue #124: Dynamic Key-Set Planning & Dependency Ordering
        IReadOnlyList<KeySetJoinPlan>? keySetPlans = null;
        if (options.DynamicKeySet.Enabled && tables.Count > 1)
        {
            var decisionDict = new Dictionary<TableIdentifier, TableAccessDecision>();
            for (int idx = 0; idx < tables.Count; idx++)
            {
                decisionDict[DecisionKey(tables[idx])] = tableDecisions[idx];
            }

            keySetPlans = DynamicKeySetPlanner.Plan(
                statement,
                tables,
                decisionDict,
                options,
                conjunctsByTable);
        }

        var plannedTables = tables.ToList();
        if (keySetPlans is { Count: > 0 })
        {
            var drivingIds = new HashSet<TableIdentifier>(keySetPlans.Select(p => p.DrivingTable.ResolvedIdentifier));
            var drivenIds = new HashSet<TableIdentifier>(keySetPlans.Select(p => p.DrivenTable.ResolvedIdentifier));
            plannedTables.Sort((a, b) =>
            {
                var idA = DecisionKey(a);
                var idB = DecisionKey(b);
                if (drivingIds.Contains(idA) && drivenIds.Contains(idB)) return -1;
                if (drivenIds.Contains(idA) && drivingIds.Contains(idB)) return 1;
                return 0;
            });
        }

        foreach (var t in plannedTables)
        {
            tableNameCounts[t.Target.TableName] = tableNameCounts.GetValueOrDefault(t.Target.TableName) + 1;
        }

        // Short names (schema.table, table) are only usable when exactly one distinct table carries them (SEC-1).
        var shortNameOwners = new Dictionary<string, HashSet<TableIdentifier>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in plannedTables)
        {
            var id = DecisionKey(t);
            foreach (var name in new[] { id.ToQualifiedName(), id.TableName, t.Target.TableName })
            {
                if (!shortNameOwners.TryGetValue(name, out var owners))
                {
                    shortNameOwners[name] = owners = [];
                }

                owners.Add(id);
            }
        }

        void MapName(Dictionary<string, string> map, string name, string value, bool shortName)
        {
            if (!shortName || shortNameOwners[name].Count == 1)
            {
                map[name] = value;
            }
        }

        if (statement == null)
        {
            var (tree, _) = _sqlEngine.Parse(rawSql.AsMemory());
            statement = new SqlAstBuilder().BuildStatement(tree);
        }

        // Issue #123 (WP-123-1): AST Subtree Graph Partitioning
        var partitionResult = SubtreeGraphPartitioner.Partition(statement, tables, options.MaxTablesPerSubtree);
        var subtreePlans = new List<SubtreeExecutionPlan>();

        var singleIds = new HashSet<TableIdentifier>(partitionResult.RemainingSingleTables.Select(DecisionKey));
        if (requiredFilterPending.Any(id => !singleIds.Contains(id)))
        {
            throw RequiredFilterMissing();
        }

        foreach (var st in partitionResult.Subtrees)
        {
            var plan = SubtreeCompiler.Compile(st, decisions, options, _sqlEngine, user, tenantId);
            subtreePlans.Add(plan);
        }

        // SEC-DATA123-03: Validate that cross-source joins do not target masked subtree output columns (SEC-FED-19)
        if (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Count > 0)
        {
            foreach (var stPlan in subtreePlans)
            {
                foreach (var maskedCol in stPlan.MaskedColumns)
                {
                    if (metadata.JoinColumnReferences.Any(jc => string.Equals(jc.ColumnName, maskedCol, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new WebSqlPolicyException($"Security Policy Violation: Column '{maskedCol}' in subtree '{stPlan.SubtreeId}' is protected by masking and cannot be used in a relational JOIN predicate across data sources (SEC-FED-19).");
                    }
                }
            }
        }

        var stagingRequests = new List<StagingTableRequest>();
        var stagingNames = new List<string>();
        int stagingIndex = 0;

        // Process Subtrees first (WP-123-3)
        foreach (var stPlan in subtreePlans)
        {
            string stagingName = $"s{stagingIndex++}";
            stagingNames.Add(stagingName);

            foreach (var m in stPlan.MemberTables)
            {
                var identifier = DecisionKey(m);
                identifierToStaging[identifier] = stagingName;
                MapName(targetToStaging, m.Target.FullName, stagingName, shortName: false);
                MapName(targetToStaging, identifier.ToString(), stagingName, shortName: false);
                MapName(targetToStaging, identifier.ToQualifiedName(), stagingName, shortName: true);
                MapName(targetToStaging, m.Target.TableName, stagingName, shortName: true);

                identifierToAlias[identifier] = stagingName;
                targetToAlias[m.Target.FullName] = stagingName;
                targetToAlias[identifier.ToString()] = stagingName;
                targetToAlias[identifier.ToQualifiedName()] = stagingName;
                targetToAlias[m.Target.TableName] = stagingName;
                if (!string.IsNullOrWhiteSpace(m.Target.Alias))
                {
                    targetToAlias[m.Target.Alias] = stagingName;
                }
            }

            var rootMember = stPlan.MemberTables[0];
            var rootDecision = RequireDecision(decisions, rootMember);

            stagingRequests.Add(new StagingTableRequest(
                Reference: rootMember.Target,
                Metadata: stPlan.SyntheticMetadata,
                Decision: rootDecision,
                StagingName: stagingName,
                Projection: stPlan.ProjectedColumns,
                PushdownFilter: null,
                VirtualFilterPlan: null,
                Subtree: stPlan));
        }

        // PARITY-GAP-018: the staged projection comes from the AST (SEC-PG-21..24), never from the text of the select list.
        var statementColumns = StatementColumnReferences.Collect(statement);

        // Process remaining single tables
        foreach (var t in partitionResult.RemainingSingleTables)
        {
            string stagingName = $"s{stagingIndex++}";
            stagingNames.Add(stagingName);

            string? alias = t.Target.Alias;
            if (string.IsNullOrWhiteSpace(alias) && tableNameCounts[t.Target.TableName] == 1)
            {
                alias = t.Target.TableName;
            }

            var identifier = DecisionKey(t);
            identifierToStaging[identifier] = stagingName;
            MapName(targetToStaging, t.Target.FullName, stagingName, shortName: false);
            MapName(targetToStaging, identifier.ToString(), stagingName, shortName: false);
            MapName(targetToStaging, identifier.ToQualifiedName(), stagingName, shortName: true);
            MapName(targetToStaging, t.Target.TableName, stagingName, shortName: true);

            if (!string.IsNullOrWhiteSpace(alias))
            {
                identifierToAlias[identifier] = alias;
                MapName(targetToAlias, t.Target.FullName, alias, shortName: false);
                MapName(targetToAlias, identifier.ToString(), alias, shortName: false);
                MapName(targetToAlias, identifier.ToQualifiedName(), alias, shortName: true);
                MapName(targetToAlias, t.Target.TableName, alias, shortName: true);
                if (!string.IsNullOrWhiteSpace(t.Target.Alias))
                {
                    targetToAlias[t.Target.Alias] = alias;
                }
            }

            // Minimal projection
            var dec = RequireDecision(decisions, t);

            var projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var col in t.Metadata.Columns)
            {
                // SEC-PG-21: a Deny column is never staged; a Mask column is staged by the governed read, already masked.
                if (dec.GetEffectiveColumnAccess(col.ColumnName, t.Metadata) == ColumnAccessLevel.Deny)
                    continue;

                if (IsReferenced(statementColumns, col.ColumnName, t.Target) ||
                    (metadata.JoinColumnReferences != null && metadata.JoinColumnReferences.Any(jc => GovernedSqlExecutionService.ReferencesColumn(jc.TableOrAlias, jc.ColumnName, col.ColumnName, t.Target))) ||
                    (metadata.FilterColumnReferences != null && metadata.FilterColumnReferences.Any(fc => GovernedSqlExecutionService.ReferencesColumn(fc.TableOrAlias, fc.ColumnName, col.ColumnName, t.Target))))
                {
                    projected.Add(col.ColumnName);
                }
            }

            if (projected.Count == 0)
            {
                foreach (var col in t.Metadata.Columns)
                {
                    if (dec.GetEffectiveColumnAccess(col.ColumnName, t.Metadata) != ColumnAccessLevel.Deny)
                        projected.Add(col.ColumnName);
                }
            }

            VirtualFilterTablePlan? vfPlan = null;
            virtualFilterPlans?.TryGetValue(identifier, out vfPlan);

            TableFilterClause? pushdownFilter = null;
            PushedParameterAudit? paramAudit = null;
            if (options.PredicatePushdown.Enabled &&
                conjunctsByTable != null &&
                conjunctsByTable.TryGetValue(identifier, out var tableConjuncts))
            {
                var pushableConjuncts = new List<SingleTableConjunct>();
                foreach (var conj in tableConjuncts)
                {
                    var evalResult = PushabilityEvaluator.Evaluate(
                        conj,
                        t.Metadata,
                        dec,
                        t.Metadata.Dialect,
                        vfPlan,
                        options.PredicatePushdown,
                        parameters?.ClientValues);

                    if (evalResult.IsPushable)
                    {
                        pushableConjuncts.Add(conj);
                    }
                }

                if (pushableConjuncts.Count > 0)
                {
                    try
                    {
                        pushdownFilter = Autheris.Application.Sql.Federation.Pushdown.PushedClauseBuilder.Build(pushableConjuncts, t.Metadata, parameters?.ClientValues);
                        pushdownFilter = pushdownFilter with
                        {
                            ConstrainedColumns = Autheris.Application.Sql.Federation.Pushdown.RequiredFilterConstraint.ConstrainedColumns(
                                pushableConjuncts.Select(c => c.Predicate), t.Target, t.Metadata, tables)
                        };
                    }
                    catch (Autheris.Application.Sql.Federation.Pushdown.PushdownRejectedException)
                    {
                        // SQLA-09: an unresolvable parameter means nothing is pushed for this table; DuckDB evaluates the filter after staging.
                        pushdownFilter = null;
                    }
                }
            }
            else if (options.PredicatePushdown.Enabled && parameters != null && t.Metadata.Table.DataSourceType == DataSourceType.Sql)
            {
                pushdownFilter = Autheris.Application.Sql.Federation.Optimization.PushedClauseBuilder.BuildPushdown(statement, t.Target, t.Metadata, dec, parameters, plannedTables, out paramAudit);
            }

            if (requiredFilterPending.Contains(identifier) &&
                !RequiredFiltersSatisfied(t, dec, pushdownFilter?.ConstrainedColumns))
            {
                throw RequiredFilterMissing();
            }

            KeySetPlanInfo? keySetPlanInfo = null;
            if (keySetPlans is { Count: > 0 })
            {
                var plan = keySetPlans.FirstOrDefault(p => p.DrivenTable.ResolvedIdentifier == DecisionKey(t));
                if (plan != null && identifierToStaging.TryGetValue(plan.DrivingTable.ResolvedIdentifier, out var drivingStagingName))
                {
                    keySetPlanInfo = new KeySetPlanInfo(
                        drivingStagingName,
                        plan.DrivingTable.ResolvedIdentifier,
                        plan.DrivingKeyColumn,
                        plan.DrivenKeyColumn);
                }
            }

            stagingRequests.Add(new StagingTableRequest(
                t.Target,
                t.Metadata,
                dec,
                stagingName,
                projected.ToList(),
                PushdownFilter: pushdownFilter,
                VirtualFilterPlan: vfPlan,
                ParameterAudit: paramAudit,
                KeySetPlan: keySetPlanInfo));
        }

        // 5. AST rewrite to target s0, s1, etc. (the user query is not touched for Virtual Filters)
        // REQ-SQL-130 (BC-130-4): the governed compiler does not run for the DuckDB statement, so the time zone calls are validated here, for a supplied and
        // a built statement alike (CTEs, subqueries and set operations are traversed), before the AST is rewritten. A masked or denied column is never a timestamp.
        var timeZoneProof = TimeZoneConversion.ValidateFederated(
            statement,
            _timeZoneState?.IsAvailable(TargetSqlDialect.DuckDb) ?? false,
            (qualifier, column) => IsMaskedOrDenied(qualifier, column, tables, tableDecisions));

        var rewriter = new FederationAstRewriter(targetToStaging, targetToAlias, tables, subtreePlans);
        var rewrittenAst = (SqlStatement)rewriter.Visit(statement);

        if (parameters?.ListShapes is { IsEmpty: false } listShapes)
        {
            rewrittenAst = TrinoSqlEngine.Ast.Visitors.ClientListExpansion.Apply(rewrittenAst, listShapes);
        }

        // WP-X3: every query value is bound ($n); the staged rows are the only data DuckDB sees.
        var compiled = TimeZoneConversion.GenerateFederated(rewrittenAst, timeZoneProof);

        return new CrossSourcePlan(compiled, stagingRequests, stagingNames);
    }

    /// <summary>
    /// Whether a column reference may name a column that the decision masks or denies. A qualifier that belongs to no staged table (a CTE or derived
    /// table alias) is judged by the column name alone, the same fallback the mask guards use for columns they cannot attribute.
    /// </summary>
    private static bool IsMaskedOrDenied(
        string? qualifier,
        string column,
        IReadOnlyList<ResolvedSourceTable> tables,
        TableAccessDecision[] decisions)
    {
        bool qualifierBelongsToATable = qualifier is not null && tables.Any(t => GovernedSqlExecutionService.ReferencesColumn(qualifier, column, column, t.Target));
        for (int i = 0; i < tables.Count; i++)
        {
            foreach (var col in tables[i].Metadata.Columns)
            {
                if (!string.Equals(col.ColumnName, column, StringComparison.OrdinalIgnoreCase)) continue;
                if (decisions[i].GetEffectiveColumnAccess(col.ColumnName, tables[i].Metadata) == ColumnAccessLevel.Clear) continue;
                if (qualifier is null || !qualifierBelongsToATable || GovernedSqlExecutionService.ReferencesColumn(qualifier, column, col.ColumnName, tables[i].Target))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static TrinoSqlEngine.Ast.Emit.RequiredFilterColumnMissingException RequiredFilterMissing() => new();

    /// <summary>MISC-04: every required filter column is satisfied by the decision or by <paramref name="clientFilterColumns"/> (shared whole-identifier rule).</summary>
    private static bool RequiredFiltersSatisfied(ResolvedSourceTable t, TableAccessDecision dec, IEnumerable<string>? clientFilterColumns)
    {
        foreach (var reqCol in t.Metadata.RequiredFilterColumns)
        {
            if (!Autheris.Application.Data.Services.RequiredFilterColumnRule.IsColumnSatisfied(
                    reqCol, t.Metadata, dec, clientFilterColumns, tenantColumnCounts: true))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the statement reads <paramref name="colName"/> of <paramref name="target"/>: a qualified reference is attributed through
    /// <c>ReferencesColumn</c> only (a qualifier that names no table stages nothing); an unqualified reference counts for every table that has the
    /// column (SEC-PG-22; an ambiguous one still fails in the final query). A wildcard stages every allowed column; an unclassifiable
    /// node stages every allowed column of every table (SEC-PG-23).
    /// </summary>
    private static bool IsReferenced(StatementColumnReferences references, string colName, TableAccessTarget target)
    {
        if (references.Unclassifiable)
        {
            return true;
        }

        foreach (var wildcard in references.Wildcards)
        {
            if (wildcard is null ||
                string.Equals(wildcard, target.Alias, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(wildcard, target.TableName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(wildcard, target.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var (qualifier, column) in references.Columns)
        {
            if (GovernedSqlExecutionService.ReferencesColumn(qualifier, column, colName, target))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class FederationAstRewriter : SqlAstRewriter
    {
        private readonly Dictionary<string, string> _targetToStaging;
        private readonly Dictionary<string, string> _targetToAlias;
        private readonly IReadOnlyList<ResolvedSourceTable> _tables;
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly IReadOnlyList<SubtreeExecutionPlan>? _subtrees;

        public FederationAstRewriter(
            Dictionary<string, string> targetToStaging,
            Dictionary<string, string> targetToAlias,
            IReadOnlyList<ResolvedSourceTable> tables,
            IReadOnlyList<SubtreeExecutionPlan>? subtrees = null)
        {
            _targetToStaging = targetToStaging;
            _targetToAlias = targetToAlias;
            _tables = tables;
            _subtrees = subtrees;
        }

        public override SqlNode VisitJoinedTableSource(JoinedTableSource node)
        {
            if (_subtrees != null && _subtrees.Count > 0)
            {
                foreach (var st in _subtrees)
                {
                    if (MatchesSubtree(node, st))
                    {
                        var stagingName = _targetToStaging[st.MemberTables[0].Target.FullName];
                        return new NamedTableSource(
                            new SqlQualifiedName([new SqlIdentifier(stagingName, false)]),
                            new SqlIdentifier(stagingName, false));
                    }
                }
            }
            return base.VisitJoinedTableSource(node);
        }

        private static bool MatchesSubtree(TableSource node, SubtreeExecutionPlan plan)
        {
            var tableNamesInNode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTableNames(node, tableNamesInNode);
            var memberNames = new HashSet<string>(plan.MemberTables.Select(m => m.Target.TableName), StringComparer.OrdinalIgnoreCase);
            return tableNamesInNode.Count > 0 && tableNamesInNode.SetEquals(memberNames);
        }

        private static void CollectTableNames(TableSource source, HashSet<string> names)
        {
            if (source is NamedTableSource named)
            {
                names.Add(named.Name.Parts[^1].Value);
            }
            else if (source is JoinedTableSource joined)
            {
                CollectTableNames(joined.Left, names);
                CollectTableNames(joined.Right, names);
            }
        }

        public override SqlNode VisitCommonTableExpression(CommonTableExpression node)
        {
            // A CTE name is visible to its own body (recursive) and to everything after it.
            _cteNames.Add(node.Name.Value);
            return base.VisitCommonTableExpression(node);
        }

        public override SqlNode VisitNamedTableSource(NamedTableSource node)
        {
            if (node.Name.Parts.Count == 1 && _cteNames.Contains(node.Name.Parts[0].Value))
            {
                return node;   // reference to a CTE of the query, not to a source table
            }

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

            // ST-27: a name that is neither a resolved table nor a CTE is never guessed (no "s0" fallback).
            if (stagingName == null)
            {
                throw new WebSqlPolicyException($"Table '{rawName}' is not part of the resolved cross-source tables.");
            }

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
                return new ColumnReference(new SqlQualifiedName([new SqlIdentifier(effectiveQualifier, false), StagedSubtreeColumn(qualifierParts, rawQualifier, colName)]));
            }

            return node;
        }

        /// <summary>
        /// SQLA-17: a subtree stages a column that two members share as "&lt;alias&gt;_&lt;column&gt;"; a reference to a member's column must name that
        /// staged column. Any other column keeps its name.
        /// </summary>
        private SqlIdentifier StagedSubtreeColumn(List<string> qualifierParts, string rawQualifier, SqlIdentifier column)
        {
            if (_subtrees == null || qualifierParts.Count == 0)
            {
                return column;
            }

            foreach (var st in _subtrees)
            {
                if (st.ProjectedColumns.Contains(column.Value, StringComparer.OrdinalIgnoreCase))
                {
                    continue;   // staged under its own name: not ambiguous
                }

                foreach (var member in st.MemberTables)
                {
                    var memberQualifier = member.Target.Alias ?? member.Target.TableName;
                    bool matches = string.Equals(rawQualifier, member.Target.FullName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(qualifierParts[^1], memberQualifier, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        continue;
                    }

                    var staged = $"{memberQualifier}_{column.Value}";
                    if (st.ProjectedColumns.Contains(staged, StringComparer.OrdinalIgnoreCase))
                    {
                        return new SqlIdentifier(staged, false);
                    }
                }
            }

            return column;
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

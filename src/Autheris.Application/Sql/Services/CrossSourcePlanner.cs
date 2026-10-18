namespace Autheris.Application.Sql.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
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
        IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? virtualFilterPlans = null,
        SqlStatement? statement = null);
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

    public CrossSourcePlan Plan(
        string rawSql,
        SqlQueryMetadata metadata,
        IReadOnlyList<ResolvedSourceTable> tables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisions,
        CrossSourceOptions options) =>
        Plan(rawSql, metadata, tables, decisions, options, null, null);

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
        IReadOnlyDictionary<TableIdentifier, VirtualFilterTablePlan>? virtualFilterPlans = null,
        SqlStatement? statement = null)
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

        // 2. Every table has its own decision (SEC-1, fail closed); guardrails SEC-JOIN-01 and SEC-FILTER-01 (INV-4)
        var tableDecisions = new TableAccessDecision[tables.Count];
        for (int i = 0; i < tables.Count; i++)
        {
            tableDecisions[i] = RequireDecision(decisions, tables[i]);
        }

        for (int i = 0; i < tables.Count; i++)
        {
            GovernedSqlExecutionService.EnforceMaskedColumnGuardrails(tables[i].Target, tables[i].Metadata, tableDecisions[i], metadata);

            var t = tables[i];
            var dec = tableDecisions[i];
            if (t.Metadata.RequiredFilterColumns.Length > 0)
            {
                foreach (var reqCol in t.Metadata.RequiredFilterColumns)
                {
                    if (t.Metadata.TenantColumnName != null &&
                        string.Equals(t.Metadata.TenantColumnName, reqCol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (dec.RowPolicy is Autheris.Application.Policy.PolicyPredicateRowPolicy rls &&
                        rls.Predicate.ReferencedColumns.Contains(reqCol.ToLowerInvariant()))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(dec.CombinedRowFilterSql) &&
                        dec.CombinedRowFilterSql.Contains(reqCol, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    throw new GatewaySecurityException(
                        GovernedSqlErrorCodes.FixedMessage(GovernedSqlErrorCodes.RequiredFilterColumnMissing),
                        GovernedSqlErrorCodes.RequiredFilterColumnMissing);
                }
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

        // 4. Staging names and minimal projections
        var targetToStaging = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var targetToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var identifierToStaging = new Dictionary<TableIdentifier, string>();
        var identifierToAlias = new Dictionary<TableIdentifier, string>();
        var tableNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in tables)
        {
            tableNameCounts[t.Target.TableName] = tableNameCounts.GetValueOrDefault(t.Target.TableName) + 1;
        }

        // Short names (schema.table, table) are only usable when exactly one distinct table carries them (SEC-1).
        var shortNameOwners = new Dictionary<string, HashSet<TableIdentifier>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
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
            var dec = tableDecisions[i];

            var projected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool selectAll = metadata.ProjectedColumns.Any(p => p == "*");

            foreach (var col in t.Metadata.Columns)
            {
                if (dec.GetEffectiveColumnAccess(col.ColumnName, t.Metadata) == ColumnAccessLevel.Deny)
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
                    if (dec.GetEffectiveColumnAccess(col.ColumnName, t.Metadata) != ColumnAccessLevel.Deny)
                        projected.Add(col.ColumnName);
                }
            }

            VirtualFilterTablePlan? vfPlan = null;
            virtualFilterPlans?.TryGetValue(identifier, out vfPlan);

            stagingRequests.Add(new StagingTableRequest(
                t.Target,
                t.Metadata,
                dec,
                stagingName,
                projected.ToList(),
                PushdownFilter: null,
                VirtualFilterPlan: vfPlan));
        }

        // 5. AST rewrite to target s0, s1, etc. (the user query is not touched for Virtual Filters)
        if (statement == null)
        {
            var (tree, _) = _sqlEngine.Parse(rawSql.AsMemory());
            statement = new SqlAstBuilder().BuildStatement(tree);
        }

        // REQ-SQL-130 (BC-130-4): the governed compiler does not run for the DuckDB statement, so the time zone calls are validated here, for a supplied and
        // a built statement alike (CTEs, subqueries and set operations are traversed), before the AST is rewritten. A masked or denied column is never a timestamp.
        var timeZoneProof = TimeZoneConversion.ValidateFederated(
            statement,
            _timeZoneState?.IsAvailable(TargetSqlDialect.DuckDb) ?? false,
            (qualifier, column) => IsMaskedOrDenied(qualifier, column, tables, tableDecisions));

        var rewriter = new FederationAstRewriter(targetToStaging, targetToAlias, tables);
        var rewrittenAst = (SqlStatement)rewriter.Visit(statement);

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
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

        public FederationAstRewriter(
            Dictionary<string, string> targetToStaging,
            Dictionary<string, string> targetToAlias,
            IReadOnlyList<ResolvedSourceTable> tables)
        {
            _targetToStaging = targetToStaging;
            _targetToAlias = targetToAlias;
            _tables = tables;
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

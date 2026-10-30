namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

public sealed partial class GovernedAggregationService
{
    private async Task<TableAccessDecision> ResolveDecisionAsync(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenantId,
        TableMetadata metadata,
        AggregationQuery query,
        CancellationToken ct)
    {
        // MISC-06: a pre-evaluated decision is trusted only when the unified PDP produced it; anything else is evaluated normally.
        if (query.PreEvaluatedDecision is { EvaluatedByPdp: true } preEvaluated)
        {
            return preEvaluated;
        }

        TableAccessDecision? decision = null;

        if (_pdp != null)
        {
            var secContext = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
            decision = await _pdp.EvaluateAccessAsync(query.Table, metadata, secContext, requestedColumns: null, ct).ConfigureAwait(false);
        }
        else if (_accessResolver != null)
        {
            var resolved = await _accessResolver.ResolveTableAccessAsync(principal, query.Table, null, null, ct).ConfigureAwait(false);
            decision = resolved.Decision;
        }
        else if (_gatewayExecutionService != null)
        {
            decision = await _gatewayExecutionService.CheckTableAccessAsync(principal, metadata, recordAudit: false, ct).ConfigureAwait(false);
        }
        else if (_consentRepository != null && _resolutionService != null)
        {
            var allSids = principal.GetAllUserSids();
            var groupSids = principal.GetGroupSids();
            var roles = principal.GetUserRoles();
            var subjects = (allSids.Count > 0 ? allSids : (IEnumerable<Sid>)[userSid])
                .Concat(groupSids)
                .Distinct()
                .ToList();
            var consents = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, query.Table, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId)
                .ToList();
            var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
            decision = _resolutionService.ResolveAccess(
                userSid,
                groupSids,
                roles,
                query.Table,
                consents,
                metadata.Dialect,
                allSids.Count > 1 ? allSids : null,
                scopes.Count > 0 ? scopes : null);
        }

        // MISC-06: no consent bypass and no reason-text matching here; metric consents are resolved inside the PDP pipeline upstream.
        if (decision == null)
        {
            return TableAccessDecision.Denied(query.Table, "No policy decision point or consent resolver is configured (fail-closed).");
        }

        return decision;
    }

    /// <summary>True when the aggregation is planned as SQL (the only path that refuses a group-key type). One helper so validation and execution cannot drift.</summary>
    private bool UsesPlanner(TableMetadata metadata) =>
        _sqlExecutionService != null && metadata.Table.DataSourceType == DataSourceType.Sql;

    /// <summary>I405-SC-01: refuses the first non-groupable group-key type of a planned SQL aggregation. Runs once, after all other validation.</summary>
    private void EnsureGroupKeysGroupable(IReadOnlyList<string> groupByColumns, TableMetadata metadata)
    {
        if (!UsesPlanner(metadata))
        {
            return;
        }

        foreach (var column in groupByColumns)
        {
            var colMeta = metadata.GetColumn(column);
            if (colMeta != null)
            {
                GroupKeyTypeFamilies.EnsureGroupable(colMeta.DataType, metadata.Dialect);
            }
        }
    }

    /// <summary>
    /// The deny details of a refused request. Fixed key set and order (I405-SC-07); a refused group-key type adds only the closed family
    /// literal and a fixed reason, never a type text or column name (I405-SC-02).
    /// </summary>
    private static Dictionary<string, object?> BuildValidationDenyDetails(Exception ex, AggregationQuery query)
    {
        var details = new Dictionary<string, object?>
        {
            ["is_allowed"] = false,
            ["reasons"] = new[] { ex.Message },
            ["group_by"] = query.GroupBy,
            ["filter"] = query.Where?.SqlPredicate
        };

        if (ex is AggregationGroupKeyNotSupportedException refused)
        {
            details["refusal_reason"] = "GROUP_KEY_TYPE_NOT_GROUPABLE";
            details["group_key_type_family"] = GroupKeyTypeFamilies.ToAuditLiteral(refused.Family);
        }

        return details;
    }

    private static List<string> ValidateAndResolveGroupBy(
        AggregationQuery query,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        var result = new List<string>();
        if (query.GroupBy is not { Count: > 0 })
        {
            return result;
        }

        foreach (var rawCol in query.GroupBy)
        {
            var colMeta = metadata.GetColumn(rawCol) ?? throw new AggregationColumnUnavailableException();

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new AggregationColumnUnavailableException();
            }

            if (!result.Contains(colMeta.ColumnName, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(colMeta.ColumnName);
            }
        }

        return result;
    }

    private static List<AggregateSelection> ValidateAndResolveAggregates(
        AggregationQuery query,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        var result = new List<AggregateSelection>();
        if (query.Aggregates is not { Count: > 0 })
        {
            result.Add(new AggregateSelection(AggregateFunction.Count, null, "count"));
            return result;
        }

        foreach (var agg in query.Aggregates)
        {
            if (string.IsNullOrWhiteSpace(agg.Column) || agg.Column == "*")
            {
                if (agg.Function != AggregateFunction.Count)
                {
                    throw new ArgumentException($"Aggregate function '{agg.Function}' requires a target column.", nameof(query));
                }

                result.Add(new AggregateSelection(AggregateFunction.Count, null, agg.EffectiveAlias));
                continue;
            }

            var colMeta = metadata.GetColumn(agg.Column) ?? throw new AggregationColumnUnavailableException();

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new AggregationColumnUnavailableException();
            }

            if (agg.Function is AggregateFunction.Sum or AggregateFunction.Avg && !IsNumericType(colMeta.DataType))
            {
                throw new ArgumentException(
                    $"Aggregate function '{agg.Function}' requires a numeric column, but '{colMeta.ColumnName}' has type '{colMeta.DataType}'.",
                    nameof(query));
            }

            result.Add(new AggregateSelection(agg.Function, colMeta.ColumnName, agg.EffectiveAlias));
        }

        return result;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"@[A-Za-z_][A-Za-z0-9_]*", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex ParameterPlaceholderRegex();

    private static void ValidateWhereClause(
        TableFilterClause? where,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        if (where == null)
        {
            return;
        }

        // WP-0 (SEC-112-01): the referenced-column set is a union, never a replacement. The text of the predicate is always scanned as well
        // (defence in depth), so a clause that arrives with a partial set (for example a metric filter combined with a request predicate)
        // is checked against every identifier of its text. Placeholders of bound parameters (@gq1) are not identifiers and are removed first; an @name without a bound parameter is still checked.
        // A clause that carries both a column set and bound parameters is generated text: there only identifiers that resolve to a table
        // column are added, so SQL keywords and function names in the generated text are not mistaken for unknown columns.
        var referenced = new HashSet<string>(where.ReferencedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        bool hadExplicitSet = referenced.Count > 0;
        if (!string.IsNullOrWhiteSpace(where.SqlPredicate))
        {
            // Only placeholders that name a bound parameter are removed; any other @name keeps its identifier and is checked.
            var parameters = where.Parameters;
            var text = parameters.Count == 0
                ? where.SqlPredicate
                : ParameterPlaceholderRegex().Replace(
                    where.SqlPredicate,
                    m => parameters.ContainsKey(m.Value[1..]) || parameters.ContainsKey(m.Value) ? " " : m.Value);
            foreach (var col in DataApiFilterTranslator.ExtractReferencedColumns(text, metadata))
            {
                if (hadExplicitSet && where.Parameters.Count > 0 && metadata.GetColumn(col) == null)
                {
                    continue;
                }

                referenced.Add(col);
            }
        }

        foreach (var col in referenced)
        {
            var colMeta = metadata.GetColumn(col) ?? throw new AggregationColumnUnavailableException();

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(
                    colMeta.ColumnName,
                    $"Filtering on masked column '{colMeta.ColumnName}' in an aggregation query is forbidden.");
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new AggregationColumnUnavailableException();
            }
        }
    }

    private static void ValidateOrderByClause(
        IReadOnlyList<TableOrderBy>? orderBy,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        if (orderBy is not { Count: > 0 })
        {
            return;
        }

        foreach (var item in orderBy)
        {
            if (groupByColumns.Contains(item.Column, StringComparer.OrdinalIgnoreCase) ||
                aggregates.Any(a => string.Equals(a.EffectiveAlias, item.Column, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(item.Column, "count", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Column, GovernedTableReadPlanner.GroupCountColumnAlias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colMeta = metadata.GetColumn(item.Column);
            if (colMeta == null)
            {
                throw new AggregationColumnUnavailableException();
            }

            var orderAccess = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (orderAccess == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            if (orderAccess != ColumnAccessLevel.Clear)
            {
                throw new AggregationColumnUnavailableException();
            }

            throw new ArgumentException(
                $"Aggregate ORDER BY column '{item.Column}' must be one of the GROUP BY columns or aggregate aliases.",
                nameof(orderBy));
        }
    }

    /// <summary>Maximum number of rows the in-memory aggregation branches read; reaching it fails closed (<c>AGGREGATION_ROW_CAP_EXCEEDED</c>).</summary>
    private const int InMemoryAggregationRowCap = 100_000;

    /// <summary>The buckets read from the source and whether the source itself cut groups off (the planner returned more than the group limit).</summary>
    private readonly record struct BucketRead(List<ComputedBucket> Buckets, bool SourceTruncated = false);

    private async Task<BucketRead> ExecuteAndBuildBucketsAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableMetadata metadata,
        TableAccessDecision decision,
        AggregationQuery query,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> resolvedAggregates,
        CancellationToken ct)
    {
        // WP-2 (F-1): a SQL table is aggregated in the database by the governed planner, so no row cap applies. The in-memory branches
        // below serve the other source types and deployments without a SQL execution service.
        bool usePlanner = UsesPlanner(metadata);
        var executor = usePlanner ? null : _dataSourceExecutors?.FirstOrDefault(e => e.SupportedType == metadata.Table.DataSourceType);
        if (executor != null)
        {
            var authorizedColumns = metadata.Columns
                .Where(c => decision.GetColumnAccess(c.ColumnName) != ColumnAccessLevel.Deny)
                .Select(c => c.ColumnName)
                .ToList();

            var execContext = new DataSourceExecutionContext(
                SourceName: metadata.Table.SourceName,
                Metadata: metadata,
                Principal: principal,
                AccessDecision: decision,
                Arguments: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["limit"] = InMemoryAggregationRowCap,
                    ["offset"] = 0
                },
                RequestedFields: authorizedColumns,
                RequestHeaders: null,
                Limit: InMemoryAggregationRowCap,
                Offset: 0,
                Tenant: tenantId);

            var rawRows = await executor.ExecuteAsync(execContext, ct).ConfigureAwait(false);

            // WP-0 (F-1a): a read that fills the page cannot be told apart from a truncated read, and an aggregate over a truncated prefix is wrong.
            if (rawRows.Count >= InMemoryAggregationRowCap)
            {
                throw new AggregationRowCapExceededException();
            }

            bool rlsAlreadyPushedDown = execContext.Items.TryGetValue("RlsPushdownExecuted", out var pushed) && pushed is true;
            var rlsRows = rlsAlreadyPushedDown
                ? rawRows.ToList()
                : InMemoryRowFilterEvaluator.FilterRows(rawRows.ToList(), decision, metadata);
            var filteredRows = FilterRowsByWhere(rlsRows, query.Where, metadata);
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata, query.TimeBuckets));
        }

        if (_gatewayExecutionService != null && _sqlExecutionService == null)
        {
            var (rows, _) = await _gatewayExecutionService.ExecuteTableQueryAsync(
                principal,
                query.Table,
                first: InMemoryAggregationRowCap,
                after: 0,
                ct: ct).ConfigureAwait(false);
            if (rows.Count >= InMemoryAggregationRowCap)
            {
                throw new AggregationRowCapExceededException();
            }

            var filteredRows = FilterRowsByWhere(rows.ToList(), query.Where, metadata);
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata, query.TimeBuckets));
        }

        if (_sqlExecutionService != null)
        {
            int sqlLimit = Math.Min(
                _analyticsOptions.Aggregation.MaxGroups > 0 ? _analyticsOptions.Aggregation.MaxGroups : AggregationOptions.DefaultMaxGroups,
                AggregationOptions.UpperBoundMaxGroups);

            TableFilterClause? trinoFilter = EnsureTrinoCapableFilter(query.Where, metadata);
            PlannedTableAggregate planned;
            try
            {
                planned = _planner.PlanAggregate(
                    new TableAggregateIntent(
                        query.Table,
                        groupByColumns,
                        resolvedAggregates,
                        trinoFilter,
                        query.OrderBy,
                        Limit: sqlLimit + 1,
                        Transport: query.Transport ?? SqlTransports.Rest,
                        TimeBuckets: query.TimeBuckets),
                    metadata,
                    decision);
            }
            catch (AggregationGroupKeyNotSupportedException ex)
            {
                // I405-SC-03: the planner's defense-in-depth refusal is audited exactly once (validation refusals never reach this point).
                await RecordAuditAsync(tenantId, principal.GetUserSid()!.Value, query.Table, allowed: false, BuildValidationDenyDetails(ex, query), ct).ConfigureAwait(false);
                throw;
            }

            var sqlRequest = new GovernedSqlQueryRequest(
                planned.Sql,
                Parameters: null,
                Transport: query.Transport ?? SqlTransports.Rest,
                PlannedTableRead: true)
            {
                GatewayParameters = planned.Parameters.Count > 0 ? planned.Parameters : null,
                // I398-SC-04: (max) text group keys are read under a fixed byte budget.
                ResultByteBudget = metadata.Dialect == DatabaseDialect.Sqlite
                    ? Autheris.Application.Sql.ResultByteBudget.GroupKeysWholeValue
                    : Autheris.Application.Sql.ResultByteBudget.GroupKeys,
            };

            var sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(sqlRequest, principal, tenantId, ct).ConfigureAwait(false);
            if (sqlResult.Rows.Count > 0 && sqlResult.Rows[0].ContainsKey(planned.CountColumnAlias))
            {
                // WP-2 (F-2): the planner was asked for one group more than the limit; a surplus group means the SQL cut groups off.
                bool sourceTruncated = sqlResult.Rows.Count > sqlLimit;
                var plannedRows = sourceTruncated ? sqlResult.Rows.Take(sqlLimit).ToList() : sqlResult.Rows;
                return new BucketRead(
                    BuildBucketsFromPlannedSqlResult(plannedRows, groupByColumns, resolvedAggregates, planned.CountColumnAlias, query.TimeBuckets),
                    sourceTruncated);
            }

            var rlsRows = InMemoryRowFilterEvaluator.FilterRows(sqlResult.Rows.ToList(), decision, metadata);
            var filteredRows = FilterRowsByWhere(rlsRows, query.Where, metadata);
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata, query.TimeBuckets));
        }

        throw new GatewayNotImplementedException("No SQL execution service or data source executor is configured to execute aggregation queries.");
    }

    private static TableFilterClause? EnsureTrinoCapableFilter(TableFilterClause? where, TableMetadata metadata)
    {
        if (where == null)
        {
            return null;
        }

        if (where.TrinoSqlFactory != null)
        {
            return where;
        }

        return where with
        {
            TrinoSqlFactory = _ => where.SqlPredicate
        };
    }

    private static List<IReadOnlyDictionary<string, object?>> FilterRowsByWhere(
        List<IReadOnlyDictionary<string, object?>> rows,
        TableFilterClause? where,
        TableMetadata metadata)
    {
        if (where == null || string.IsNullOrWhiteSpace(where.SqlPredicate) || rows.Count == 0)
        {
            return rows;
        }

        return InMemoryRowFilterEvaluator.FilterRows(rows, where.SqlPredicate, metadata, where.Parameters);
    }
}

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
        if (query.PreEvaluatedDecision != null)
        {
            return query.PreEvaluatedDecision;
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

        if (decision == null)
        {
            if (query.BypassTableConsent)
            {
                return BuildMetricScopedFallbackDecision(metadata);
            }

            return TableAccessDecision.Denied(query.Table, "No policy decision point or consent resolver is configured (fail-closed).");
        }

        if (!decision.IsAllowed && query.BypassTableConsent)
        {
            bool hasExplicitDeny = decision.DeniedReasons.Any(r =>
                r.Contains("Explicit Deny", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Casbin", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("ReBAC", StringComparison.OrdinalIgnoreCase) ||
                r.Contains("Contract", StringComparison.OrdinalIgnoreCase));

            if (!hasExplicitDeny)
            {
                return BuildMetricScopedFallbackDecision(metadata, decision);
            }
        }

        return decision;
    }

    private static TableAccessDecision BuildMetricScopedFallbackDecision(TableMetadata metadata, TableAccessDecision? existing = null)
    {
        var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in metadata.Columns)
        {
            if (existing != null && existing.ColumnAccess.TryGetValue(col.ColumnName, out var existingLevel))
            {
                colAccess[col.ColumnName] = existingLevel;
            }
            else if (col.IsSensitive || metadata.ColumnMaskingRules.ContainsKey(col.ColumnName))
            {
                colAccess[col.ColumnName] = ColumnAccessLevel.Mask;
            }
            else
            {
                colAccess[col.ColumnName] = ColumnAccessLevel.Clear;
            }
        }

        return TableAccessDecision.Allowed(
            metadata.Identifier,
            colAccess,
            rowFilterSql: existing?.CombinedRowFilterSql,
            hasUnconstrainedColumnAllow: true,
            rowFilterParameters: existing?.RowFilterParameters) with
        {
            RowPolicy = existing?.RowPolicy,
            MandatoryRowPredicateSql = existing?.MandatoryRowPredicateSql,
            MandatoryRowPolicy = existing?.MandatoryRowPolicy,
            AppliedVirtualFilters = existing?.AppliedVirtualFilters,
            // REQ-GOV-143 (SEC-GOV143-07): the rebuilt decision keeps the restriction of the original; without an original every
            // catalog-sensitive column is Mask above, so there is nothing to carry.
            ScopeClearance = existing?.ScopeClearance
        };
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
        bool usePlanner = _sqlExecutionService != null && metadata.Table.DataSourceType == DataSourceType.Sql;
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
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata));
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
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata));
        }

        if (_sqlExecutionService != null)
        {
            int sqlLimit = Math.Min(
                _analyticsOptions.Aggregation.MaxGroups > 0 ? _analyticsOptions.Aggregation.MaxGroups : AggregationOptions.DefaultMaxGroups,
                AggregationOptions.UpperBoundMaxGroups);

            TableFilterClause? trinoFilter = EnsureTrinoCapableFilter(query.Where, metadata);
            var planned = _planner.PlanAggregate(
                new TableAggregateIntent(
                    query.Table,
                    groupByColumns,
                    resolvedAggregates,
                    trinoFilter,
                    query.OrderBy,
                    Limit: sqlLimit + 1,
                    Transport: query.Transport ?? SqlTransports.Rest),
                metadata,
                decision);

            var sqlRequest = new GovernedSqlQueryRequest(
                planned.Sql,
                planned.Parameters.Count > 0 ? planned.Parameters : null,
                Transport: query.Transport ?? SqlTransports.Rest,
                PlannedTableRead: true);

            var sqlResult = await _sqlExecutionService.ExecuteQueryBufferedAsync(sqlRequest, principal, tenantId, ct).ConfigureAwait(false);
            if (sqlResult.Rows.Count > 0 && sqlResult.Rows[0].ContainsKey(planned.CountColumnAlias))
            {
                // WP-2 (F-2): the planner was asked for one group more than the limit; a surplus group means the SQL cut groups off.
                bool sourceTruncated = sqlResult.Rows.Count > sqlLimit;
                var plannedRows = sourceTruncated ? sqlResult.Rows.Take(sqlLimit).ToList() : sqlResult.Rows;
                return new BucketRead(
                    BuildBucketsFromPlannedSqlResult(plannedRows, groupByColumns, resolvedAggregates, planned.CountColumnAlias),
                    sourceTruncated);
            }

            var rlsRows = InMemoryRowFilterEvaluator.FilterRows(sqlResult.Rows.ToList(), decision, metadata);
            var filteredRows = FilterRowsByWhere(rlsRows, query.Where, metadata);
            return new BucketRead(BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata));
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

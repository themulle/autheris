namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Data;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Governed analytical aggregation service (#28, #37).
/// Enforces caller RLS row filters, rejects masked columns in <c>GROUP BY</c>, aggregate arguments, and <c>WHERE</c> filters
/// with <see cref="MaskedAggregationException"/> (<c>AGGREGATION_ON_MASKED_COLUMN</c>), and enforces k-anonymity minimum group size
/// thresholds with differencing-safe grand totals.
/// </summary>
public sealed class GovernedAggregationService : IGovernedAggregationService
{
    public const string OtherBucketLabel = "OTHER";

    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IUnifiedPolicyDecisionPoint? _pdp;
    private readonly ITableAccessResolver? _accessResolver;
    private readonly IGatewayExecutionService? _gatewayExecutionService;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IGovernedSqlExecutionService? _sqlExecutionService;
    private readonly IEnumerable<IDataSourceExecutor>? _dataSourceExecutors;
    private readonly IAuditWriter? _auditWriter;
    private readonly IGovernedTableReadPlanner _planner;
    private readonly AnalyticsOptions _analyticsOptions;
    private readonly bool _hasExplicitAnalyticsOptions;

    [ActivatorUtilitiesConstructor]
    public GovernedAggregationService(
        ITableMetadataRepository metadataRepository,
        IAuditLogRepository auditLogRepository,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IGatewayExecutionService? gatewayExecutionService = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IGovernedSqlExecutionService? sqlExecutionService = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IAuditWriter? auditWriter = null,
        IOptions<AnalyticsOptions>? analyticsOptions = null,
        IGovernedTableReadPlanner? planner = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp;
        _accessResolver = accessResolver ?? (gatewayExecutionService as ITableAccessResolver);
        _gatewayExecutionService = gatewayExecutionService;
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _sqlExecutionService = sqlExecutionService;
        _dataSourceExecutors = dataSourceExecutors;
        _auditWriter = auditWriter ?? new AuditWriter(auditLogRepository, NullLogger<AuditWriter>.Instance);
        _planner = planner ?? new GovernedTableReadPlanner();
        _hasExplicitAnalyticsOptions = analyticsOptions?.Value != null;
        _analyticsOptions = analyticsOptions?.Value ?? new AnalyticsOptions();
    }

    public GovernedAggregationService(
        ITableMetadataRepository metadataRepository,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IGatewayExecutionService? gatewayExecutionService = null,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IGovernedSqlExecutionService? sqlExecutionService = null,
        IEnumerable<IDataSourceExecutor>? dataSourceExecutors = null,
        IAuditWriter? auditWriter = null,
        IOptions<AnalyticsOptions>? analyticsOptions = null,
        IGovernedTableReadPlanner? planner = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _pdp = pdp;
        _accessResolver = accessResolver ?? (gatewayExecutionService as ITableAccessResolver);
        _gatewayExecutionService = gatewayExecutionService;
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _sqlExecutionService = sqlExecutionService;
        _dataSourceExecutors = dataSourceExecutors;
        _auditWriter = auditWriter ?? (metadataRepository is IAuditLogRepository repoAudit ? new AuditWriter(repoAudit, NullLogger<AuditWriter>.Instance) : null);
        _planner = planner ?? new GovernedTableReadPlanner();
        _hasExplicitAnalyticsOptions = analyticsOptions?.Value != null;
        _analyticsOptions = analyticsOptions?.Value ?? new AnalyticsOptions();
    }

    public async Task<AggregationResult> ExecuteAggregationAsync(
        ClaimsPrincipal principal,
        AggregationQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new GatewayUnauthorizedException("Authentication is required to execute governed aggregation queries.");
        }

        var userSid = principal.GetUserSid()
            ?? throw new GatewayUnauthorizedException("No valid user SID in the authentication token.");
        var tenantId = principal.GetTenantId();

        AggregationCostCalculator.ValidateBounds(query, _analyticsOptions.Aggregation);

        var metadata = await _metadataRepository.GetTableMetadataAsync(query.Table, ct).ConfigureAwait(false)
            ?? VirtualSystemTables.Get(query.Table);
        if (metadata == null || !metadata.Table.IsActive)
        {
            throw new TableNotFoundException(query.Table);
        }

        GovernedQueryStages.EnsureCanonicalClusterAdminClaim(principal, query.Table);

        var decision = await ResolveDecisionAsync(principal, userSid, tenantId, metadata, query, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            await RecordAuditAsync(tenantId, userSid, query.Table, allowed: false, new Dictionary<string, object?>
            {
                ["is_allowed"] = false,
                ["reasons"] = decision.DeniedReasons
            }, ct).ConfigureAwait(false);

            throw new GatewayForbiddenException(
                $"Access to table '{query.Table.ToQualifiedName()}' denied: {string.Join("; ", decision.DeniedReasons)}");
        }

        IReadOnlyList<string> groupByColumns;
        IReadOnlyList<AggregateSelection> resolvedAggregates;
        try
        {
            groupByColumns = ValidateAndResolveGroupBy(query, metadata, decision);
            resolvedAggregates = ValidateAndResolveAggregates(query, metadata, decision);
            ValidateWhereClause(query.Where, metadata, decision);
            ValidateOrderByClause(query.OrderBy, groupByColumns, resolvedAggregates, metadata, decision);
        }
        catch (Exception ex)
        {
            await RecordAuditAsync(tenantId, userSid, query.Table, allowed: false, new Dictionary<string, object?>
            {
                ["is_allowed"] = false,
                ["reasons"] = new[] { ex.Message },
                ["group_by"] = query.GroupBy,
                ["filter"] = query.Where?.SqlPredicate
            }, ct).ConfigureAwait(false);
            throw;
        }

        int maxGroups = _analyticsOptions.Aggregation.MaxGroups > 0
            ? _analyticsOptions.Aggregation.MaxGroups
            : AggregationOptions.DefaultMaxGroups;
        int requestedLimit = query.Limit.HasValue
            ? Math.Min(query.Limit.Value, maxGroups)
            : maxGroups;

        var rawBuckets = await ExecuteAndBuildBucketsAsync(
            principal,
            tenantId,
            metadata,
            decision,
            query,
            groupByColumns,
            resolvedAggregates,
            ct).ConfigureAwait(false);

        int effectiveMinGroupSize = ResolveEffectiveMinGroupSize(query, metadata);
        SmallGroupStrategy effectiveStrategy = ResolveEffectiveSmallGroupStrategy(query, metadata);

        var survivingBuckets = new List<ComputedBucket>(rawBuckets.Count);
        var smallBuckets = new List<ComputedBucket>();

        if (effectiveMinGroupSize > 1)
        {
            foreach (var bucket in rawBuckets)
            {
                if (bucket.Count >= effectiveMinGroupSize)
                {
                    survivingBuckets.Add(bucket);
                }
                else if (bucket.Count > 0 || groupByColumns.Count > 0)
                {
                    smallBuckets.Add(bucket);
                }
            }
        }
        else
        {
            survivingBuckets.AddRange(rawBuckets);
        }

        bool suppressed = false;
        int suppressedGroupsCount = 0;
        long suppressedRowsCount = 0;
        bool mergedIntoOther = false;
        ComputedBucket? mergedOtherBucket = null;

        if (effectiveMinGroupSize > 1 && smallBuckets.Count > 0)
        {
            if (effectiveStrategy == SmallGroupStrategy.MergeIntoOther && groupByColumns.Count > 0)
            {
                long combinedSmallCount = smallBuckets.Sum(b => b.Count);
                if (combinedSmallCount >= effectiveMinGroupSize)
                {
                    var otherKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var gCol in groupByColumns)
                    {
                        otherKey[gCol] = OtherBucketLabel;
                    }

                    var combinedRows = smallBuckets.SelectMany(b => b.Rows).ToList();
                    mergedOtherBucket = combinedRows.Count > 0
                        ? ComputeBucketFromRows(otherKey, combinedRows, resolvedAggregates, metadata, isMergedOther: true)
                        : MergePreAggregatedBuckets(otherKey, smallBuckets, resolvedAggregates, isMergedOther: true);

                    mergedIntoOther = true;
                }
                else
                {
                    // Even the combined OTHER bucket falls below MinGroupSize; suppress it to prevent differencing attacks.
                    suppressed = true;
                    suppressedGroupsCount = smallBuckets.Count;
                    suppressedRowsCount = combinedSmallCount;
                }
            }
            else
            {
                suppressed = true;
                suppressedGroupsCount = smallBuckets.Count;
                suppressedRowsCount = smallBuckets.Sum(b => b.Count);
            }
        }

        var orderedBuckets = OrderBuckets(survivingBuckets, query.OrderBy, groupByColumns, resolvedAggregates);
        if (mergedOtherBucket != null)
        {
            orderedBuckets.Add(mergedOtherBucket);
        }

        bool truncated = orderedBuckets.Count > requestedLimit;
        if (truncated)
        {
            orderedBuckets = orderedBuckets.Take(requestedLimit).ToList();
        }

        var visibleGroupRows = orderedBuckets.Select(b => b.ToGroupRow()).ToList();

        // Differencing protection (#37): Totals are computed strictly over the surviving visible groups.
        var totalsRow = ComputeDifferencingSafeTotals(orderedBuckets, resolvedAggregates, metadata);

        int exposedSuppressedGroups = _analyticsOptions.Privacy.ExposeSuppressedGroupsCount ? suppressedGroupsCount : 0;
        var resultMetadata = new AggregationMetadata(
            MinimumGroupSize: effectiveMinGroupSize,
            Strategy: effectiveStrategy,
            Suppressed: suppressed,
            SuppressedGroupsCount: exposedSuppressedGroups,
            SuppressedRowsCount: suppressedRowsCount,
            MergedIntoOther: mergedIntoOther,
            Truncated: truncated);

        await RecordAuditAsync(tenantId, userSid, query.Table, allowed: true, new Dictionary<string, object?>
        {
            ["is_allowed"] = true,
            ["group_by"] = groupByColumns,
            ["aggregates"] = resolvedAggregates.Select(a => a.EffectiveAlias).ToArray(),
            ["filter"] = query.Where?.SqlPredicate,
            ["minimum_group_size"] = effectiveMinGroupSize,
            ["strategy"] = effectiveStrategy.ToString(),
            ["suppressed"] = suppressed,
            ["suppressed_groups_count"] = suppressedGroupsCount,
            ["suppressed_rows_count"] = suppressedRowsCount,
            ["merged_into_other"] = mergedIntoOther,
            ["returned_groups_count"] = visibleGroupRows.Count
        }, ct).ConfigureAwait(false);

        return new AggregationResult(
            Table: query.Table,
            Groups: visibleGroupRows,
            Totals: totalsRow,
            Metadata: resultMetadata);
    }

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
            AppliedVirtualFilters = existing?.AppliedVirtualFilters
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
            var colMeta = metadata.GetColumn(rawCol)
                ?? throw new ArgumentException($"Column '{rawCol}' does not exist on table '{query.Table.ToQualifiedName()}'.", nameof(query));

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new GatewayForbiddenException($"Access to column '{colMeta.ColumnName}' on table '{query.Table.ToQualifiedName()}' is denied.");
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

            var colMeta = metadata.GetColumn(agg.Column)
                ?? throw new ArgumentException($"Column '{agg.Column}' does not exist on table '{query.Table.ToQualifiedName()}'.", nameof(query));

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new GatewayForbiddenException($"Access to column '{colMeta.ColumnName}' on table '{query.Table.ToQualifiedName()}' is denied.");
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

    private static void ValidateWhereClause(
        TableFilterClause? where,
        TableMetadata metadata,
        TableAccessDecision decision)
    {
        if (where == null)
        {
            return;
        }

        var referenced = new HashSet<string>(where.ReferencedColumns ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (referenced.Count == 0 && !string.IsNullOrWhiteSpace(where.SqlPredicate))
        {
            foreach (var col in DataApiFilterTranslator.ExtractReferencedColumns(where.SqlPredicate, metadata))
            {
                referenced.Add(col);
            }
        }

        foreach (var col in referenced)
        {
            var colMeta = metadata.GetColumn(col)
                ?? throw new ArgumentException($"Filter column '{col}' does not exist on table '{metadata.Identifier.ToQualifiedName()}'.", nameof(where));

            var access = decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata);
            if (access == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(
                    colMeta.ColumnName,
                    $"Filtering on masked column '{colMeta.ColumnName}' in an aggregation query is forbidden.");
            }

            if (access != ColumnAccessLevel.Clear)
            {
                throw new GatewayForbiddenException($"Filtering on denied column '{colMeta.ColumnName}' is forbidden.");
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
            if (colMeta != null && decision.GetEffectiveColumnAccess(colMeta.ColumnName, metadata) == ColumnAccessLevel.Mask)
            {
                throw new MaskedAggregationException(colMeta.ColumnName);
            }

            throw new ArgumentException(
                $"Aggregate ORDER BY column '{item.Column}' must be one of the GROUP BY columns or aggregate aliases.",
                nameof(orderBy));
        }
    }

    private async Task<List<ComputedBucket>> ExecuteAndBuildBucketsAsync(
        ClaimsPrincipal principal,
        TenantId tenantId,
        TableMetadata metadata,
        TableAccessDecision decision,
        AggregationQuery query,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> resolvedAggregates,
        CancellationToken ct)
    {
        // 1. If an in-memory/data-source executor or GatewayExecutionService is provided without a dedicated SQL aggregate engine,
        // fetch RLS-filtered rows and aggregate in memory.
        var executor = _dataSourceExecutors?.FirstOrDefault(e => e.SupportedType == metadata.Table.DataSourceType);
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
                    ["limit"] = 100_000,
                    ["offset"] = 0
                },
                RequestedFields: authorizedColumns,
                RequestHeaders: null,
                Limit: 100_000,
                Offset: 0,
                Tenant: tenantId);

            var rawRows = await executor.ExecuteAsync(execContext, ct).ConfigureAwait(false);
            bool rlsAlreadyPushedDown = execContext.Items.TryGetValue("RlsPushdownExecuted", out var pushed) && pushed is true;
            var rlsRows = rlsAlreadyPushedDown
                ? rawRows.ToList()
                : InMemoryRowFilterEvaluator.FilterRows(rawRows.ToList(), decision, metadata);
            var filteredRows = FilterRowsByWhere(rlsRows, query.Where, metadata);
            return BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata);
        }

        if (_gatewayExecutionService != null && _sqlExecutionService == null)
        {
            var (rows, _) = await _gatewayExecutionService.ExecuteTableQueryAsync(
                principal,
                query.Table,
                first: 100_000,
                after: 0,
                ct: ct).ConfigureAwait(false);
            var filteredRows = FilterRowsByWhere(rows.ToList(), query.Where, metadata);
            return BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata);
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
                    Limit: sqlLimit,
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
                return BuildBucketsFromPlannedSqlResult(sqlResult.Rows, groupByColumns, resolvedAggregates, planned.CountColumnAlias);
            }

            // Fallback when a test double returns raw table rows from IGovernedSqlExecutionService
            var rlsRows = InMemoryRowFilterEvaluator.FilterRows(sqlResult.Rows.ToList(), decision, metadata);
            var filteredRows = FilterRowsByWhere(rlsRows, query.Where, metadata);
            return BuildBucketsFromRows(filteredRows, groupByColumns, resolvedAggregates, metadata);
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

    private static List<ComputedBucket> BuildBucketsFromRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata)
    {
        if (groupByColumns.Count == 0)
        {
            if (rows.Count == 0)
            {
                return [];
            }

            var emptyKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return [ComputeBucketFromRows(emptyKey, rows, aggregates, metadata, isMergedOther: false)];
        }

        var groups = new Dictionary<string, (Dictionary<string, object?> Key, List<IReadOnlyDictionary<string, object?>> Rows)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var keyDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var compositeParts = new string[groupByColumns.Count];
            for (int i = 0; i < groupByColumns.Count; i++)
            {
                var col = groupByColumns[i];
                var val = GetRowValue(row, col);
                keyDict[col] = val;
                compositeParts[i] = val is null or DBNull
                    ? "\u0000NULL\u0000"
                    : Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            var compositeKey = string.Join("\u001F", compositeParts);
            if (!groups.TryGetValue(compositeKey, out var entry))
            {
                entry = (keyDict, new List<IReadOnlyDictionary<string, object?>>());
                groups[compositeKey] = entry;
            }

            entry.Rows.Add(row);
        }

        var buckets = new List<ComputedBucket>(groups.Count);
        foreach (var (_, (keyDict, bucketRows)) in groups)
        {
            buckets.Add(ComputeBucketFromRows(keyDict, bucketRows, aggregates, metadata, isMergedOther: false));
        }

        return buckets;
    }

    private static ComputedBucket ComputeBucketFromRows(
        IReadOnlyDictionary<string, object?> groupKey,
        List<IReadOnlyDictionary<string, object?>> bucketRows,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata,
        bool isMergedOther)
    {
        long count = bucketRows.Count;
        var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in aggregates)
        {
            var alias = agg.EffectiveAlias;
            if (agg.Column == null)
            {
                aggValues[alias] = count;
                continue;
            }

            var col = agg.Column;
            var nonNullValues = bucketRows
                .Select(r => GetRowValue(r, col))
                .Where(v => v is not null and not DBNull)
                .ToList();

            switch (agg.Function)
            {
                case AggregateFunction.Count:
                    aggValues[alias] = (long)nonNullValues.Count;
                    break;

                case AggregateFunction.CountDistinct:
                    var distinct = new HashSet<string>(
                        nonNullValues.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty),
                        StringComparer.Ordinal);
                    aggValues[alias] = (long)distinct.Count;
                    break;

                case AggregateFunction.Sum:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = 0m;
                        sums[col] = 0m;
                    }
                    else
                    {
                        decimal sum = nonNullValues.Sum(ToDecimal);
                        object sumBoxed = PreferIntegralOrDecimal(sum, metadata.GetColumn(col)?.DataType);
                        aggValues[alias] = sumBoxed;
                        sums[col] = sumBoxed;
                    }
                    break;

                case AggregateFunction.Avg:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        avgs[col] = null;
                    }
                    else
                    {
                        decimal avg = nonNullValues.Average(ToDecimal);
                        aggValues[alias] = avg;
                        avgs[col] = avg;
                    }
                    break;

                case AggregateFunction.Min:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        mins[col] = null;
                    }
                    else
                    {
                        object? minVal = ComputeMinOrMax(nonNullValues, isMax: false);
                        aggValues[alias] = minVal;
                        mins[col] = minVal;
                    }
                    break;

                case AggregateFunction.Max:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        maxs[col] = null;
                    }
                    else
                    {
                        object? maxVal = ComputeMinOrMax(nonNullValues, isMax: true);
                        aggValues[alias] = maxVal;
                        maxs[col] = maxVal;
                    }
                    break;
            }
        }

        return new ComputedBucket(groupKey, count, aggValues, sums, avgs, mins, maxs, bucketRows, isMergedOther);
    }

    private static List<ComputedBucket> BuildBucketsFromPlannedSqlResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqlRows,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates,
        string countColumnAlias)
    {
        var buckets = new List<ComputedBucket>(sqlRows.Count);
        foreach (var row in sqlRows)
        {
            var groupKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var gCol in groupByColumns)
            {
                groupKey[gCol] = GetRowValue(row, gCol);
            }

            long count = Convert.ToInt64(GetRowValue(row, countColumnAlias) ?? 0L, CultureInfo.InvariantCulture);
            var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var agg in aggregates)
            {
                var val = GetRowValue(row, agg.EffectiveAlias);
                aggValues[agg.EffectiveAlias] = val;
                if (agg.Column != null)
                {
                    switch (agg.Function)
                    {
                        case AggregateFunction.Sum:
                            sums[agg.Column] = val;
                            break;
                        case AggregateFunction.Avg:
                            avgs[agg.Column] = val;
                            break;
                        case AggregateFunction.Min:
                            mins[agg.Column] = val;
                            break;
                        case AggregateFunction.Max:
                            maxs[agg.Column] = val;
                            break;
                    }
                }
            }

            buckets.Add(new ComputedBucket(groupKey, count, aggValues, sums, avgs, mins, maxs, [], IsMergedOther: false));
        }

        return buckets;
    }

    private static ComputedBucket MergePreAggregatedBuckets(
        IReadOnlyDictionary<string, object?> groupKey,
        IReadOnlyList<ComputedBucket> buckets,
        IReadOnlyList<AggregateSelection> aggregates,
        bool isMergedOther)
    {
        long totalCount = buckets.Sum(b => b.Count);
        var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in aggregates)
        {
            var alias = agg.EffectiveAlias;
            if (agg.Column == null)
            {
                aggValues[alias] = totalCount;
                continue;
            }

            var col = agg.Column;
            var nonNullVals = buckets
                .Select(b => b.Aggregates.TryGetValue(alias, out var v) ? v : null)
                .Where(v => v is not null and not DBNull)
                .ToList();

            switch (agg.Function)
            {
                case AggregateFunction.Count:
                case AggregateFunction.CountDistinct:
                    aggValues[alias] = nonNullVals.Sum(v => Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    break;

                case AggregateFunction.Sum:
                    decimal sum = nonNullVals.Sum(ToDecimal);
                    aggValues[alias] = sum;
                    sums[col] = sum;
                    break;

                case AggregateFunction.Avg:
                    if (totalCount == 0 || nonNullVals.Count == 0)
                    {
                        aggValues[alias] = null;
                        avgs[col] = null;
                    }
                    else
                    {
                        decimal weightedSum = 0m;
                        long weightCount = 0;
                        foreach (var b in buckets)
                        {
                            if (b.Aggregates.TryGetValue(alias, out var v) && v is not null and not DBNull && b.Count > 0)
                            {
                                weightedSum += ToDecimal(v) * b.Count;
                                weightCount += b.Count;
                            }
                        }
                        decimal avg = weightCount > 0 ? weightedSum / weightCount : 0m;
                        aggValues[alias] = avg;
                        avgs[col] = avg;
                    }
                    break;

                case AggregateFunction.Min:
                    var minVal = nonNullVals.Count > 0 ? ComputeMinOrMax(nonNullVals!, isMax: false) : null;
                    aggValues[alias] = minVal;
                    mins[col] = minVal;
                    break;

                case AggregateFunction.Max:
                    var maxVal = nonNullVals.Count > 0 ? ComputeMinOrMax(nonNullVals!, isMax: true) : null;
                    aggValues[alias] = maxVal;
                    maxs[col] = maxVal;
                    break;
            }
        }

        return new ComputedBucket(groupKey, totalCount, aggValues, sums, avgs, mins, maxs, [], isMergedOther);
    }

    private static AggregationGroupRow ComputeDifferencingSafeTotals(
        IReadOnlyList<ComputedBucket> survivingBuckets,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata)
    {
        var emptyKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (survivingBuckets.Count == 0)
        {
            var zeroAggs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var zeroSums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var agg in aggregates)
            {
                if (agg.Function is AggregateFunction.Count or AggregateFunction.CountDistinct)
                {
                    zeroAggs[agg.EffectiveAlias] = 0L;
                }
                else if (agg.Function == AggregateFunction.Sum)
                {
                    zeroAggs[agg.EffectiveAlias] = 0m;
                    if (agg.Column != null)
                    {
                        zeroSums[agg.Column] = 0m;
                    }
                }
                else
                {
                    zeroAggs[agg.EffectiveAlias] = null;
                }
            }

            return new AggregationGroupRow(emptyKey, 0L, zeroAggs)
            {
                Sum = zeroSums
            };
        }

        var allSurvivingRows = survivingBuckets.SelectMany(b => b.Rows).ToList();
        if (allSurvivingRows.Count > 0)
        {
            return ComputeBucketFromRows(emptyKey, allSurvivingRows, aggregates, metadata, isMergedOther: false).ToGroupRow();
        }

        return MergePreAggregatedBuckets(emptyKey, survivingBuckets, aggregates, isMergedOther: false).ToGroupRow();
    }

    private int ResolveEffectiveMinGroupSize(AggregationQuery query, TableMetadata metadata)
    {
        int resolved = _hasExplicitAnalyticsOptions
            ? _analyticsOptions.Privacy.DefaultMinGroupSize
            : 1;

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.TableMinimumGroupSizes.TryGetValue(candidateKey, out var tableMin))
            {
                resolved = tableMin;
                break;
            }
        }

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.Privacy.Datasets.TryGetValue(candidateKey, out var dsOverride) && dsOverride.MinGroupSize.HasValue)
            {
                resolved = dsOverride.MinGroupSize.Value;
                break;
            }
        }

        if (metadata.EffectiveMinGroupSize.HasValue)
        {
            resolved = _hasExplicitAnalyticsOptions
                ? Math.Max(resolved, metadata.EffectiveMinGroupSize.Value)
                : metadata.EffectiveMinGroupSize.Value;
        }

        if (query.MinGroupSizeOverride.HasValue)
        {
            resolved = metadata.EffectiveMinGroupSize.HasValue
                ? Math.Max(resolved, query.MinGroupSizeOverride.Value)
                : query.MinGroupSizeOverride.Value;
        }

        return Math.Max(1, resolved);
    }

    private SmallGroupStrategy ResolveEffectiveSmallGroupStrategy(AggregationQuery query, TableMetadata metadata)
    {
        if (query.SmallGroupStrategyOverride.HasValue)
        {
            return query.SmallGroupStrategyOverride.Value;
        }

        if (metadata.EffectiveSmallGroupStrategy.HasValue)
        {
            return metadata.EffectiveSmallGroupStrategy.Value;
        }

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.Privacy.Datasets.TryGetValue(candidateKey, out var dsOverride) && dsOverride.SuppressionMode.HasValue)
            {
                return dsOverride.SuppressionMode.Value;
            }
        }

        return _analyticsOptions.Privacy.SuppressionMode;
    }

    private static IEnumerable<string> CandidateTableKeys(TableIdentifier table)
    {
        yield return table.ToString();
        yield return table.ToQualifiedName();
        if (!string.IsNullOrWhiteSpace(table.Schema))
        {
            yield return $"{table.Schema}.{table.TableName}";
        }
        yield return table.TableName;
    }

    private static List<ComputedBucket> OrderBuckets(
        List<ComputedBucket> buckets,
        IReadOnlyList<TableOrderBy>? orderBy,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates)
    {
        if (buckets.Count <= 1)
        {
            return buckets.ToList();
        }

        IOrderedEnumerable<ComputedBucket>? ordered = null;
        if (orderBy is { Count: > 0 })
        {
            foreach (var item in orderBy)
            {
                Func<ComputedBucket, object?> selector = b =>
                {
                    if (b.GroupKey.TryGetValue(item.Column, out var gk))
                    {
                        return NormalizeComparable(gk);
                    }
                    if (b.Aggregates.TryGetValue(item.Column, out var av))
                    {
                        return NormalizeComparable(av);
                    }
                    if (string.Equals(item.Column, "count", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Column, GovernedTableReadPlanner.GroupCountColumnAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        return b.Count;
                    }
                    return null;
                };

                ordered = ordered == null
                    ? (item.Descending ? buckets.OrderByDescending(selector, ComparableObjectComparer.Instance) : buckets.OrderBy(selector, ComparableObjectComparer.Instance))
                    : (item.Descending ? ordered.ThenByDescending(selector, ComparableObjectComparer.Instance) : ordered.ThenBy(selector, ComparableObjectComparer.Instance));
            }
        }
        else if (groupByColumns.Count > 0)
        {
            foreach (var col in groupByColumns)
            {
                Func<ComputedBucket, object?> selector = b => b.GroupKey.TryGetValue(col, out var gk) ? NormalizeComparable(gk) : null;
                ordered = ordered == null
                    ? buckets.OrderBy(selector, ComparableObjectComparer.Instance)
                    : ordered.ThenBy(selector, ComparableObjectComparer.Instance);
            }
        }

        return ordered?.ToList() ?? buckets.ToList();
    }

    private Task RecordAuditAsync(
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        bool allowed,
        object details,
        CancellationToken ct)
    {
        if (_auditWriter == null)
        {
            return Task.CompletedTask;
        }

        return GovernedQueryStages.RecordTableReadAsync(
            _auditWriter,
            tenantId,
            userSid,
            table,
            allowed,
            details,
            ct,
            AuditTier.Required,
            AuditEventTypes.GovernedAggregation);
    }

    private static object? GetRowValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var exact))
        {
            return exact is DBNull ? null : exact;
        }

        foreach (var (k, v) in row)
        {
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase))
            {
                return v is DBNull ? null : v;
            }
        }

        return null;
    }

    private static decimal ToDecimal(object? value) =>
        value switch
        {
            null or DBNull => 0m,
            decimal d => d,
            int i => i,
            long l => l,
            double db => (decimal)db,
            float f => (decimal)f,
            short s => s,
            byte b => b,
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };

    private static object PreferIntegralOrDecimal(decimal value, string? dataType)
    {
        if (!string.IsNullOrWhiteSpace(dataType))
        {
            var dt = dataType.Trim().ToLowerInvariant();
            if ((dt.Contains("int", StringComparison.Ordinal) || dt == "serial") && decimal.Truncate(value) == value)
            {
                return value;
            }
        }

        return value;
    }

    private static object? ComputeMinOrMax(List<object?> values, bool isMax)
    {
        object? best = null;
        foreach (var v in values)
        {
            if (v is null or DBNull)
            {
                continue;
            }

            if (best == null)
            {
                best = v;
                continue;
            }

            int cmp = ComparableObjectComparer.Instance.Compare(NormalizeComparable(v), NormalizeComparable(best));
            if ((isMax && cmp > 0) || (!isMax && cmp < 0))
            {
                best = v;
            }
        }

        return best;
    }

    private static object? NormalizeComparable(object? value) =>
        value switch
        {
            null or DBNull => null,
            int or long or short or byte or decimal or double or float => ToDecimal(value),
            DateTimeOffset dto => dto.UtcTicks,
            DateTime dt => dt.ToUniversalTime().Ticks,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

    private static bool IsNumericType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true;
        }

        var dt = dataType.Trim().ToLowerInvariant();
        return dt.Contains("int", StringComparison.Ordinal) ||
               dt.Contains("numeric", StringComparison.Ordinal) ||
               dt.Contains("decimal", StringComparison.Ordinal) ||
               dt.Contains("number", StringComparison.Ordinal) ||
               dt.Contains("float", StringComparison.Ordinal) ||
               dt.Contains("double", StringComparison.Ordinal) ||
               dt.Contains("real", StringComparison.Ordinal) ||
               dt.Contains("money", StringComparison.Ordinal) ||
               dt.Contains("serial", StringComparison.Ordinal) ||
               dt.Contains("hugeint", StringComparison.Ordinal);
    }

    private sealed record ComputedBucket(
        IReadOnlyDictionary<string, object?> GroupKey,
        long Count,
        IReadOnlyDictionary<string, object?> Aggregates,
        IReadOnlyDictionary<string, object?> Sum,
        IReadOnlyDictionary<string, object?> Avg,
        IReadOnlyDictionary<string, object?> Min,
        IReadOnlyDictionary<string, object?> Max,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        bool IsMergedOther)
    {
        public AggregationGroupRow ToGroupRow() =>
            new(GroupKey, Count, Aggregates, IsMergedOther)
            {
                Sum = Sum,
                Avg = Avg,
                Min = Min,
                Max = Max
            };
    }

    private sealed class ComparableObjectComparer : IComparer<object?>
    {
        public static readonly ComparableObjectComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is decimal dx && y is decimal dy) return dx.CompareTo(dy);
            if (x is long lx && y is long ly) return lx.CompareTo(ly);
            if (x is IComparable cx && x.GetType() == y.GetType()) return cx.CompareTo(y);
            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }
}

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
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Executes governed dbt Semantic Layer metric queries (#38).
/// Enforces table or metric-scoped consent (<c>metric:&lt;name&gt;</c>), caller RLS row filters on the underlying semantic model relation,
/// masked column rejection (<c>AGGREGATION_ON_MASKED_COLUMN</c>), k-anonymity minimum group size thresholds, and audit logging.
/// </summary>
public sealed class SemanticMetricQueryService : ISemanticMetricQueryService
{
    private static readonly HashSet<string> StandardTimeGranularities = new(StringComparer.OrdinalIgnoreCase)
    {
        "hour", "day", "week", "month", "quarter", "year"
    };

    private readonly ISemanticCatalogService _catalogService;
    private readonly IGovernedAggregationService _aggregationService;
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentResolutionService? _resolutionService;
    private readonly IUnifiedPolicyDecisionPoint? _pdp;
    private readonly ITableAccessResolver? _accessResolver;
    private readonly IAuditWriter? _auditWriter;

    [ActivatorUtilitiesConstructor]
    public SemanticMetricQueryService(
        ISemanticCatalogService catalogService,
        IGovernedAggregationService aggregationService,
        ITableMetadataRepository metadataRepository,
        IAuditLogRepository auditLogRepository,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IAuditWriter? auditWriter = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _aggregationService = aggregationService ?? throw new ArgumentNullException(nameof(aggregationService));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _pdp = pdp;
        _accessResolver = accessResolver;
        _auditWriter = auditWriter ?? new AuditWriter(auditLogRepository, NullLogger<AuditWriter>.Instance);
    }

    public SemanticMetricQueryService(
        ISemanticCatalogService catalogService,
        IGovernedAggregationService aggregationService,
        ITableMetadataRepository metadataRepository,
        IConsentRepository? consentRepository = null,
        IConsentResolutionService? resolutionService = null,
        IUnifiedPolicyDecisionPoint? pdp = null,
        ITableAccessResolver? accessResolver = null,
        IAuditWriter? auditWriter = null)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _aggregationService = aggregationService ?? throw new ArgumentNullException(nameof(aggregationService));
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _consentRepository = consentRepository ?? (metadataRepository as IConsentRepository);
        _resolutionService = resolutionService;
        _pdp = pdp;
        _accessResolver = accessResolver;
        _auditWriter = auditWriter ?? (metadataRepository is IAuditLogRepository repoAudit ? new AuditWriter(repoAudit, NullLogger<AuditWriter>.Instance) : null);
    }

    public async Task<MetricQueryResult> QueryMetricsAsync(
        ClaimsPrincipal principal,
        MetricQueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (principal == null || principal.Identity?.IsAuthenticated != true)
        {
            throw new GatewayUnauthorizedException("Authentication is required to query semantic metrics.");
        }

        if (request.Metrics is not { Count: > 0 })
        {
            throw new ArgumentException("At least one metric name must be specified.", nameof(request));
        }

        var userSid = principal.GetUserSid()
            ?? throw new GatewayUnauthorizedException("No valid user SID in the authentication token.");
        var tenantId = principal.GetTenantId();

        var resolvedMetrics = new Dictionary<string, DbtMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in request.Metrics)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                throw new ArgumentException("Metric name must not be empty.", nameof(request));
            }

            var metric = await _catalogService.GetMetricAsync(rawName.Trim(), ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Metric '{rawName}' was not found in the semantic catalog.");
            resolvedMetrics[metric.Name] = metric;
        }

        var allModels = await _catalogService.ListSemanticModelsAsync(ct).ConfigureAwait(false);
        DbtSemanticModel? targetModel = null;
        var requiredMeasures = new Dictionary<string, DbtMeasure>(StringComparer.OrdinalIgnoreCase);
        int? metricOrMeasureMinGroupSize = null;

        foreach (var metric in resolvedMetrics.Values)
        {
            ValidateTimeGranularity(metric, request.TimeGranularity);
            ValidateRequestedDimensions(metric, request.GroupBy, allModels);

            if (metric.MinGroupSize.HasValue)
            {
                metricOrMeasureMinGroupSize = metricOrMeasureMinGroupSize.HasValue
                    ? Math.Max(metricOrMeasureMinGroupSize.Value, metric.MinGroupSize.Value)
                    : metric.MinGroupSize.Value;
            }

            var (model, measures) = ResolveModelAndMeasures(metric, allModels);
            if (targetModel == null)
            {
                targetModel = model;
            }
            else if (!string.Equals(targetModel.Name, model.Name, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(targetModel.NodeRelation, model.NodeRelation, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Metrics '{ string.Join(", ", resolvedMetrics.Keys) }' span incompatible semantic models ('{targetModel.Name}' and '{model.Name}').",
                    nameof(request));
            }

            foreach (var m in measures)
            {
                requiredMeasures[m.Name] = m;
                if (m.MinGroupSize.HasValue)
                {
                    metricOrMeasureMinGroupSize = metricOrMeasureMinGroupSize.HasValue
                        ? Math.Max(metricOrMeasureMinGroupSize.Value, m.MinGroupSize.Value)
                        : m.MinGroupSize.Value;
                }
            }
        }

        if (targetModel == null)
        {
            throw new InvalidOperationException("No semantic model could be resolved for the requested metrics.");
        }

        var tableMetadata = await ResolveTableMetadataAsync(targetModel, ct).ConfigureAwait(false);
        var tableId = tableMetadata.Identifier;

        // Evaluate table consent OR metric-scoped consent (metric:<metricName>)
        var (decision, hasMetricConsent) = await ResolveMetricAndTableDecisionAsync(
            principal,
            userSid,
            tenantId,
            tableMetadata,
            resolvedMetrics.Values.ToList(),
            ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            await RecordMetricAuditAsync(
                tenantId,
                userSid,
                tableId,
                allowed: false,
                resolvedMetrics.Keys.ToList(),
                _catalogService.ManifestVersion,
                new Dictionary<string, object?>
                {
                    ["is_allowed"] = false,
                    ["metrics"] = resolvedMetrics.Keys.ToArray(),
                    ["reasons"] = decision.DeniedReasons
                },
                ct).ConfigureAwait(false);

            throw new GatewayForbiddenException(
                $"Access to semantic metric(s) '{string.Join(", ", resolvedMetrics.Keys)}' on table '{tableId.ToQualifiedName()}' is denied: {string.Join("; ", decision.DeniedReasons)}");
        }

        // Build aggregate selections from the resolved measures
        var aggregateSelections = new List<AggregateSelection>();
        foreach (var measure in requiredMeasures.Values)
        {
            var aggFunc = ParseAggregateFunction(measure.Aggregation);
            string? targetColumn = ResolveMeasureColumn(measure, aggFunc, tableMetadata);
            aggregateSelections.Add(new AggregateSelection(aggFunc, targetColumn, measure.Name));
        }

        // Also add direct alias selections for single-measure simple/cumulative metrics
        foreach (var metric in resolvedMetrics.Values)
        {
            if (IsSimpleOrCumulative(metric.Type) && metric.Measures.Count == 1)
            {
                var mName = metric.Measures[0];
                if (requiredMeasures.TryGetValue(mName, out var measure) &&
                    !aggregateSelections.Any(a => string.Equals(a.EffectiveAlias, metric.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var aggFunc = ParseAggregateFunction(measure.Aggregation);
                    string? targetColumn = ResolveMeasureColumn(measure, aggFunc, tableMetadata);
                    aggregateSelections.Add(new AggregateSelection(aggFunc, targetColumn, metric.Name));
                }
            }
        }

        var combinedWhere = CombineMetricFiltersWithWhere(resolvedMetrics.Values, request.Where, tableMetadata);

        var aggQuery = new AggregationQuery(
            Table: tableId,
            GroupBy: request.GroupBy,
            Aggregates: aggregateSelections,
            Where: combinedWhere,
            OrderBy: request.OrderBy,
            Limit: request.Limit,
            MinGroupSizeOverride: metricOrMeasureMinGroupSize,
            SmallGroupStrategyOverride: request.SmallGroupStrategyOverride,
            Transport: "SemanticMetrics",
            BypassTableConsent: hasMetricConsent,
            PreEvaluatedDecision: decision);

        AggregationResult aggResult;
        try
        {
            aggResult = await _aggregationService.ExecuteAggregationAsync(principal, aggQuery, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RecordMetricAuditAsync(
                tenantId,
                userSid,
                tableId,
                allowed: false,
                resolvedMetrics.Keys.ToList(),
                _catalogService.ManifestVersion,
                new Dictionary<string, object?>
                {
                    ["is_allowed"] = false,
                    ["metrics"] = resolvedMetrics.Keys.ToArray(),
                    ["reasons"] = new[] { ex.Message }
                },
                ct).ConfigureAwait(false);
            throw;
        }

        // Enrich each group row and totals row with derived/ratio/simple metric values keyed by metric.Name
        var enrichedGroups = aggResult.Groups
            .Select(g => EnrichGroupWithMetricValues(g, resolvedMetrics.Values, requiredMeasures))
            .ToList();
        var enrichedTotals = aggResult.Totals != null
            ? EnrichGroupWithMetricValues(aggResult.Totals, resolvedMetrics.Values, requiredMeasures)
            : null;

        string manifestVersion = resolvedMetrics.Values
            .Select(m => m.Version)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? _catalogService.ManifestVersion
            ?? "1.0";

        await RecordMetricAuditAsync(
            tenantId,
            userSid,
            tableId,
            allowed: true,
            resolvedMetrics.Keys.ToList(),
            manifestVersion,
            new Dictionary<string, object?>
            {
                ["is_allowed"] = true,
                ["metrics"] = resolvedMetrics.Keys.ToArray(),
                ["manifest_version"] = manifestVersion,
                ["semantic_model"] = targetModel.Name,
                ["group_by"] = request.GroupBy ?? Array.Empty<string>(),
                ["time_granularity"] = request.TimeGranularity,
                ["minimum_group_size"] = aggResult.Metadata.MinimumGroupSize,
                ["suppressed"] = aggResult.Metadata.Suppressed,
                ["suppressed_groups_count"] = aggResult.Metadata.SuppressedGroupsCount,
                ["suppressed_rows_count"] = aggResult.Metadata.SuppressedRowsCount,
                ["metric_scoped_consent"] = hasMetricConsent
            },
            ct).ConfigureAwait(false);

        return new MetricQueryResult(
            Metrics: resolvedMetrics.Keys.ToList(),
            ManifestVersion: manifestVersion,
            Groups: enrichedGroups,
            Totals: enrichedTotals,
            Metadata: aggResult.Metadata,
            MetricDefinitions: resolvedMetrics);
    }

    private static void ValidateTimeGranularity(DbtMetric metric, string? timeGranularity)
    {
        if (string.IsNullOrWhiteSpace(timeGranularity))
        {
            return;
        }

        var allowed = metric.AllowedTimeGranularities is { Count: > 0 }
            ? metric.AllowedTimeGranularities
            : (IReadOnlyCollection<string>)StandardTimeGranularities;

        if (!allowed.Any(g => string.Equals(g, timeGranularity.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"Time granularity '{timeGranularity}' is not permitted for metric '{metric.Name}'. Allowed granularities: {string.Join(", ", allowed)}.",
                nameof(timeGranularity));
        }
    }

    private static void ValidateRequestedDimensions(
        DbtMetric metric,
        IReadOnlyList<string>? groupBy,
        IReadOnlyList<DbtSemanticModel> allModels)
    {
        if (groupBy is not { Count: > 0 } || metric.Dimensions.Count == 0)
        {
            return;
        }

        var modelDims = allModels
            .SelectMany(m => m.Dimensions)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dim in groupBy)
        {
            if (!metric.Dimensions.Contains(dim, StringComparer.OrdinalIgnoreCase) &&
                !modelDims.Contains(dim))
            {
                throw new ArgumentException(
                    $"Dimension '{dim}' is not declared on metric '{metric.Name}'. Declared dimensions: {string.Join(", ", metric.Dimensions)}.",
                    nameof(groupBy));
            }
        }
    }

    private static (DbtSemanticModel Model, List<DbtMeasure> Measures) ResolveModelAndMeasures(
        DbtMetric metric,
        IReadOnlyList<DbtSemanticModel> allModels)
    {
        if (allModels.Count == 0)
        {
            throw new InvalidOperationException($"Cannot resolve metric '{metric.Name}' because no semantic models are registered in the catalog.");
        }

        DbtSemanticModel? matchedModel = null;
        if (metric.Meta.TryGetValue("model", out var metaModel) || metric.Meta.TryGetValue("semantic_model", out metaModel))
        {
            matchedModel = allModels.FirstOrDefault(m => string.Equals(m.Name, metaModel, StringComparison.OrdinalIgnoreCase));
        }

        var measureNames = new List<string>(metric.Measures);
        if (measureNames.Count == 0 && !string.IsNullOrWhiteSpace(metric.TypeParams))
        {
            // Extract referenced measure identifiers from derived/ratio expression in TypeParams
            foreach (var model in allModels)
            {
                foreach (var ms in model.Measures)
                {
                    if (ContainsIdentifier(metric.TypeParams, ms.Name) && !measureNames.Contains(ms.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        measureNames.Add(ms.Name);
                    }
                }
            }
        }

        if (matchedModel == null)
        {
            matchedModel = allModels.FirstOrDefault(m =>
                measureNames.All(req => m.Measures.Any(ms => string.Equals(ms.Name, req, StringComparison.OrdinalIgnoreCase))))
                ?? allModels.FirstOrDefault(m =>
                    measureNames.Any(req => m.Measures.Any(ms => string.Equals(ms.Name, req, StringComparison.OrdinalIgnoreCase))))
                ?? allModels[0];
        }

        var resolvedMeasures = new List<DbtMeasure>();
        foreach (var mName in measureNames)
        {
            var found = matchedModel.Measures.FirstOrDefault(ms => string.Equals(ms.Name, mName, StringComparison.OrdinalIgnoreCase))
                ?? allModels.SelectMany(m => m.Measures).FirstOrDefault(ms => string.Equals(ms.Name, mName, StringComparison.OrdinalIgnoreCase));

            if (found != null)
            {
                resolvedMeasures.Add(found);
            }
            else
            {
                // Fallback synthetic sum measure when metric names a column directly
                resolvedMeasures.Add(new DbtMeasure(mName, "sum", mName, null));
            }
        }

        if (resolvedMeasures.Count == 0)
        {
            throw new ArgumentException($"Metric '{metric.Name}' does not reference any resolvable measure.");
        }

        return (matchedModel, resolvedMeasures);
    }

    private async Task<TableMetadata> ResolveTableMetadataAsync(DbtSemanticModel model, CancellationToken ct)
    {
        var candidates = new List<TableIdentifier>();
        if (!string.IsNullOrWhiteSpace(model.NodeRelation))
        {
            var cleaned = model.NodeRelation.Replace("\"", string.Empty).Replace("`", string.Empty).Trim();
            if (TableIdentifier.TryParse(cleaned, out var parsed))
            {
                candidates.Add(parsed);
            }

            var parts = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                candidates.Add(new TableIdentifier("default", parts[0], parts[1]));
            }
            else if (parts.Length == 1)
            {
                candidates.Add(new TableIdentifier("default", "public", parts[0]));
                candidates.Add(new TableIdentifier("default", "dbo", parts[0]));
            }
        }

        if (TableIdentifier.TryParse(model.Name, out var modelId))
        {
            candidates.Add(modelId);
        }
        candidates.Add(new TableIdentifier("default", "public", model.Name));

        foreach (var candidate in candidates)
        {
            var meta = await _metadataRepository.GetTableMetadataAsync(candidate, ct).ConfigureAwait(false)
                ?? VirtualSystemTables.Get(candidate);
            if (meta != null && meta.Table.IsActive)
            {
                return meta;
            }
        }

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var relLeaf = (model.NodeRelation ?? model.Name).Replace("\"", string.Empty).Split('.').LastOrDefault()?.Trim();
        var matched = allTables.FirstOrDefault(t =>
            t.Table.IsActive &&
            (string.Equals(t.Identifier.TableName, relLeaf, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.Identifier.TableName, model.Name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(t.Identifier.ToString(), model.NodeRelation?.Replace("\"", string.Empty), StringComparison.OrdinalIgnoreCase)));

        if (matched != null)
        {
            return matched;
        }

        throw new TableNotFoundException(candidates.FirstOrDefault());
    }

    private async Task<(TableAccessDecision Decision, bool HasMetricConsent)> ResolveMetricAndTableDecisionAsync(
        ClaimsPrincipal principal,
        Sid userSid,
        TenantId tenantId,
        TableMetadata tableMetadata,
        IReadOnlyList<DbtMetric> metrics,
        CancellationToken ct)
    {
        var table = tableMetadata.Identifier;
        var allSids = principal.GetAllUserSids();
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var subjects = (allSids.Count > 0 ? allSids : (IEnumerable<Sid>)[userSid])
            .Concat(groupSids)
            .Distinct()
            .ToList();

        // 1. Check standard table-level decision first
        TableAccessDecision? tableDecision = null;
        if (_pdp != null)
        {
            var secCtx = SecurityPrincipalContext.FromPrincipal(principal, tenantId);
            tableDecision = await _pdp.EvaluateAccessAsync(table, tableMetadata, secCtx, requestedColumns: null, ct).ConfigureAwait(false);
        }
        else if (_accessResolver != null)
        {
            var resolved = await _accessResolver.ResolveTableAccessAsync(principal, table, null, null, ct).ConfigureAwait(false);
            tableDecision = resolved.Decision;
        }
        else if (_consentRepository != null && _resolutionService != null)
        {
            var tableConsents = (await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, table, DateTimeOffset.UtcNow, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId)
                .ToList();
            if (tableConsents.Count > 0)
            {
                var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
                tableDecision = _resolutionService.ResolveAccess(
                    userSid,
                    groupSids,
                    roles,
                    table,
                    tableConsents,
                    tableMetadata.Dialect,
                    allSids.Count > 1 ? allSids : null,
                    scopes.Count > 0 ? scopes : null);
            }
        }

        if (tableDecision is { IsAllowed: true })
        {
            return (tableDecision, false);
        }

        // If table access was denied by an explicit DENY consent, fail closed immediately.
        if (tableDecision != null && tableDecision.DeniedReasons.Any(r => r.Contains("Explicit Deny", StringComparison.OrdinalIgnoreCase)))
        {
            return (tableDecision, false);
        }

        // 2. Check metric-scoped consents (e.g. "metric:<metricName>") for every requested metric
        if (_consentRepository != null)
        {
            var now = DateTimeOffset.UtcNow;
            var allSubjectConsents = (await _consentRepository.GetAllActiveConsentsForSubjectsAsync(subjects, roles, now, tenantId, ct).ConfigureAwait(false))
                .Where(c => c.TenantId == tenantId && c.IsActive(now))
                .ToList();

            var matchedMetricConsents = new List<Consent>();
            bool allMetricsPermitted = true;

            foreach (var metric in metrics)
            {
                var metricConsents = new List<Consent>();
                foreach (var c in allSubjectConsents)
                {
                    if (MatchesMetricIdentifier(c.TableIdentifier, metric.Name))
                    {
                        metricConsents.Add(c);
                    }
                }

                // Also probe GetActiveConsentsForSubjectsAsync for standard metric table identifiers
                foreach (var candidateId in CandidateMetricIdentifiers(table, metric.Name))
                {
                    var specific = await _consentRepository.GetActiveConsentsForSubjectsAsync(subjects, candidateId, now, tenantId, ct).ConfigureAwait(false);
                    foreach (var c in specific.Where(c => c.TenantId == tenantId && c.IsActive(now)))
                    {
                        if (metricConsents.All(existing => existing.Id != c.Id))
                        {
                            metricConsents.Add(c);
                        }
                    }
                }

                if (metricConsents.Any(c => c.Effect == ConsentEffect.Deny))
                {
                    return (TableAccessDecision.Denied(table, $"Explicit Deny consent for metric '{metric.Name}'."), false);
                }

                if (!metricConsents.Any(c => c.Effect == ConsentEffect.Allow))
                {
                    allMetricsPermitted = false;
                    break;
                }

                matchedMetricConsents.AddRange(metricConsents);
            }

            if (allMetricsPermitted && matchedMetricConsents.Count > 0)
            {
                // Project metric consents onto the base table so ConsentResolutionService computes their RLS row filters and column rules!
                var projectedConsents = matchedMetricConsents
                    .Select(c => new Consent
                    {
                        Id = c.Id,
                        TableId = tableMetadata.Table.Id,
                        TableIdentifier = table,
                        ConsentRequestId = c.ConsentRequestId,
                        Effect = c.Effect,
                        TenantId = c.TenantId,
                        GranteeType = c.GranteeType,
                        GranteeSid = c.GranteeSid,
                        RoleId = c.RoleId,
                        RoleName = c.RoleName,
                        ValidFrom = c.ValidFrom,
                        ValidTo = c.ValidTo,
                        IsRevoked = c.IsRevoked,
                        ColumnRules = c.ColumnRules,
                        RowFilters = c.RowFilters
                    })
                    .ToList();

                if (_resolutionService != null)
                {
                    var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
                    var metricDecision = _resolutionService.ResolveAccess(
                        userSid,
                        groupSids,
                        roles,
                        table,
                        projectedConsents,
                        tableMetadata.Dialect,
                        allSids.Count > 1 ? allSids : null,
                        scopes.Count > 0 ? scopes : null);

                    if (metricDecision is { IsAllowed: true })
                    {
                        return (metricDecision, true);
                    }
                }

                // Fallback when no IConsentResolutionService was provided in unit test construction
                var colAccess = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
                foreach (var col in tableMetadata.Columns)
                {
                    colAccess[col.ColumnName] = col.IsSensitive || tableMetadata.ColumnMaskingRules.ContainsKey(col.ColumnName)
                        ? ColumnAccessLevel.Mask
                        : ColumnAccessLevel.Clear;
                }

                foreach (var rule in projectedConsents.SelectMany(c => c.ColumnRules))
                {
                    colAccess[rule.ColumnName] = rule.AccessLevel;
                }

                return (TableAccessDecision.Allowed(table, colAccess, hasUnconstrainedColumnAllow: true), true);
            }
        }

        return (tableDecision ?? TableAccessDecision.Denied(table, "No active table or metric consent found."), false);
    }

    private static bool MatchesMetricIdentifier(TableIdentifier id, string metricName)
    {
        var metricToken = $"metric:{metricName}";
        if (string.Equals(id.TableName, metricToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.TableName, metricName, StringComparison.OrdinalIgnoreCase) && string.Equals(id.Domain, "metric", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.TableName, metricName, StringComparison.OrdinalIgnoreCase) && string.Equals(id.Schema, "metric", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.ToString(), metricToken, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id.ToQualifiedName(), $"metric.{metricName}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<TableIdentifier> CandidateMetricIdentifiers(TableIdentifier baseTable, string metricName)
    {
        yield return new TableIdentifier(baseTable.Domain, baseTable.Schema, $"metric:{metricName}");
        yield return new TableIdentifier("metric", "default", metricName);
        yield return new TableIdentifier("default", "metric", metricName);
        yield return new TableIdentifier("default", "default", $"metric:{metricName}");
    }

    private static AggregateFunction ParseAggregateFunction(string? agg) =>
        (agg ?? "sum").Trim().ToLowerInvariant() switch
        {
            "sum" => AggregateFunction.Sum,
            "count" => AggregateFunction.Count,
            "avg" or "average" => AggregateFunction.Avg,
            "min" => AggregateFunction.Min,
            "max" => AggregateFunction.Max,
            "count_distinct" or "countdistinct" => AggregateFunction.CountDistinct,
            _ => throw new ArgumentException($"Unsupported measure aggregation function '{agg}'.", nameof(agg))
        };

    private static string? ResolveMeasureColumn(DbtMeasure measure, AggregateFunction aggFunc, TableMetadata metadata)
    {
        if (!string.IsNullOrWhiteSpace(measure.Expr))
        {
            var expr = measure.Expr.Trim();
            if (expr is "1" or "*")
            {
                return aggFunc == AggregateFunction.Count ? null : expr;
            }

            return expr;
        }

        if (metadata.HasColumn(measure.Name))
        {
            return measure.Name;
        }

        return aggFunc == AggregateFunction.Count ? null : measure.Name;
    }

    private static TableFilterClause? CombineMetricFiltersWithWhere(
        IEnumerable<DbtMetric> metrics,
        TableFilterClause? requestWhere,
        TableMetadata metadata)
    {
        var predicates = new List<string>();
        var referencedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (requestWhere != null && !string.IsNullOrWhiteSpace(requestWhere.SqlPredicate))
        {
            predicates.Add(requestWhere.SqlPredicate);
            foreach (var c in requestWhere.ReferencedColumns)
            {
                referencedCols.Add(c);
            }
            foreach (var (k, v) in requestWhere.Parameters)
            {
                parameters[k] = v;
            }
        }

        foreach (var metric in metrics)
        {
            if (!string.IsNullOrWhiteSpace(metric.Filter))
            {
                var cleaned = CleanDbtFilterTemplate(metric.Filter);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    predicates.Add(cleaned);
                    foreach (var col in Autheris.Application.Data.Services.DataApiFilterTranslator.ExtractReferencedColumns(cleaned, metadata))
                    {
                        referencedCols.Add(col);
                    }
                }
            }
        }

        if (predicates.Count == 0)
        {
            return null;
        }

        if (predicates.Count == 1 && requestWhere != null)
        {
            return requestWhere;
        }

        string combinedSql = predicates.Count == 1
            ? predicates[0]
            : string.Join(" AND ", predicates.Select(p => $"({p})"));

        return new TableFilterClause(combinedSql, parameters, referencedCols.ToList())
        {
            TrinoSqlFactory = _ => combinedSql
        };
    }

    private static string CleanDbtFilterTemplate(string rawFilter)
    {
        // Replaces dbt Jinja Dimension('entity__col') / TimeDimension('...') with the column name
        var text = rawFilter.Trim();
        if (text.StartsWith("{{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal))
        {
            text = text[2..^2].Trim();
        }

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?:Dimension|TimeDimension|Entity)\(\s*['""](?:[A-Za-z0-9_]+__)?([A-Za-z0-9_]+)['""]\s*\)",
            "$1");

        return text;
    }

    private static AggregationGroupRow EnrichGroupWithMetricValues(
        AggregationGroupRow group,
        IEnumerable<DbtMetric> metrics,
        IReadOnlyDictionary<string, DbtMeasure> measures)
    {
        var enrichedAggs = new Dictionary<string, object?>(group.Aggregates, StringComparer.OrdinalIgnoreCase);
        foreach (var metric in metrics)
        {
            if (enrichedAggs.ContainsKey(metric.Name))
            {
                continue;
            }

            if (IsSimpleOrCumulative(metric.Type) && metric.Measures.Count >= 1)
            {
                var mName = metric.Measures[0];
                if (enrichedAggs.TryGetValue(mName, out var val))
                {
                    enrichedAggs[metric.Name] = val;
                }
            }
            else if (string.Equals(metric.Type, "ratio", StringComparison.OrdinalIgnoreCase))
            {
                enrichedAggs[metric.Name] = EvaluateRatioMetric(metric, enrichedAggs);
            }
            else if (string.Equals(metric.Type, "derived", StringComparison.OrdinalIgnoreCase))
            {
                enrichedAggs[metric.Name] = EvaluateDerivedMetric(metric, enrichedAggs, measures.Keys);
            }
        }

        return group with { Aggregates = enrichedAggs };
    }

    private static object? EvaluateRatioMetric(DbtMetric metric, IReadOnlyDictionary<string, object?> aggValues)
    {
        if (metric.Measures.Count >= 2 &&
            aggValues.TryGetValue(metric.Measures[0], out var numObj) &&
            aggValues.TryGetValue(metric.Measures[1], out var denObj) &&
            numObj is not null && denObj is not null)
        {
            decimal num = Convert.ToDecimal(numObj, CultureInfo.InvariantCulture);
            decimal den = Convert.ToDecimal(denObj, CultureInfo.InvariantCulture);
            return den == 0m ? null : Math.Round(num / den, 6);
        }

        return null;
    }

    private static object? EvaluateDerivedMetric(
        DbtMetric metric,
        IReadOnlyDictionary<string, object?> aggValues,
        IEnumerable<string> measureNames)
    {
        if (string.IsNullOrWhiteSpace(metric.TypeParams))
        {
            if (metric.Measures.Count >= 2 &&
                aggValues.TryGetValue(metric.Measures[0], out var first) &&
                aggValues.TryGetValue(metric.Measures[1], out var second) &&
                first is not null && second is not null)
            {
                return Convert.ToDecimal(first, CultureInfo.InvariantCulture) - Convert.ToDecimal(second, CultureInfo.InvariantCulture);
            }
            return null;
        }

        var expr = metric.TypeParams;
        foreach (var mName in measureNames.OrderByDescending(s => s.Length))
        {
            if (aggValues.TryGetValue(mName, out var val) && val is not null)
            {
                var decStr = Convert.ToDecimal(val, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
                expr = System.Text.RegularExpressions.Regex.Replace(
                    expr,
                    $@"\b{System.Text.RegularExpressions.Regex.Escape(mName)}\b",
                    decStr,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }

        try
        {
            using var table = new System.Data.DataTable();
            var computed = table.Compute(expr, null);
            return computed is null or DBNull ? null : Convert.ToDecimal(computed, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSimpleOrCumulative(string? type) =>
        string.IsNullOrWhiteSpace(type) ||
        string.Equals(type, "simple", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "cumulative", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "conversion", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIdentifier(string expr, string identifier) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            expr,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(identifier)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private Task RecordMetricAuditAsync(
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        bool allowed,
        IReadOnlyList<string> metrics,
        string manifestVersion,
        object details,
        CancellationToken ct)
    {
        if (_auditWriter == null)
        {
            return Task.CompletedTask;
        }

        var target = metrics.Count == 1
            ? $"metric:{metrics[0]} ({table})"
            : $"metrics:{string.Join(",", metrics)} ({table})";

        var entry = GovernedQueryStages.BuildEntry(
            AuditEventTypes.MetricQuery,
            tenantId,
            userSid,
            target,
            allowed,
            details);

        return GovernedQueryStages.WriteAsync(_auditWriter, entry, AuditTier.Required, ct);
    }
}

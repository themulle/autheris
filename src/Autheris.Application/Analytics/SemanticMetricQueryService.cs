namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Data;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Executes governed dbt Semantic Layer metric queries (#38).
/// Enforces table or metric-scoped consent (<c>metric:&lt;name&gt;</c>), caller RLS row filters on the underlying semantic model relation,
/// masked column rejection (<c>AGGREGATION_ON_MASKED_COLUMN</c>), k-anonymity minimum group size thresholds, and audit logging.
/// </summary>
public sealed partial class SemanticMetricQueryService : ISemanticMetricQueryService
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
                    $"Metrics '{string.Join(", ", resolvedMetrics.Keys)}' span incompatible semantic models ('{targetModel.Name}' and '{model.Name}').",
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
}

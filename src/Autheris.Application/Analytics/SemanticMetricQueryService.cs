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
using Autheris.Application.Dbt.Services;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Sql.Tree;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TrinoSqlEngine.Governance;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// Executes governed dbt Semantic Layer metric queries (#38).
/// Enforces table or metric-scoped consent (<c>metric:&lt;name&gt;</c>), caller RLS row filters on the underlying semantic model relation,
/// masked column rejection (<c>AGGREGATION_ON_MASKED_COLUMN</c>), k-anonymity minimum group size thresholds, and audit logging.
/// </summary>
public sealed partial class SemanticMetricQueryService : ISemanticMetricQueryService
{
    /// <summary>Fixed caller-facing message for a denied or missing backing table; carries no table, metric or reason text.</summary>
    internal const string DeniedMessage = "Access denied.";

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
    private readonly SemanticTenancy _tenancy;
    private readonly IPolicyExpressionParser _policyParser;

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
        IAuditWriter? auditWriter = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IPolicyExpressionParser? policyParser = null)
    {
        ArgumentNullException.ThrowIfNull(auditLogRepository);
        _tenancy = SemanticTenancy.FromOptions(gatewayOptions);
        _policyParser = policyParser ?? PolicyParserSetup.CreateParser();
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
        IAuditWriter? auditWriter = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IPolicyExpressionParser? policyParser = null)
    {
        _tenancy = SemanticTenancy.FromOptions(gatewayOptions);
        _policyParser = policyParser ?? PolicyParserSetup.CreateParser();
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
        if (request.TypedWhere is not null && request.Where is not null)
        {
            throw new ArgumentException("Specify either a typed filter or a text filter, not both.", nameof(request));
        }

        var tenantId = principal.GetTenantId();

        // A1-3: a caller without catalog standing (no tenant claim in Multi) gets the same answer as an unknown metric.
        var catalogTenant = _tenancy.ResolveCallerTenant(principal);

        var resolvedMetrics = new Dictionary<string, DbtMetric>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawName in request.Metrics)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                throw new ArgumentException("Metric name must not be empty.", nameof(request));
            }

            var metric = (catalogTenant is { } ct0 ? await _catalogService.GetMetricAsync(rawName.Trim(), ct0, ct).ConfigureAwait(false) : null)
                ?? throw new KeyNotFoundException($"Metric '{rawName}' was not found in the semantic catalog.");
            resolvedMetrics[metric.Name] = metric;
        }

        var catalogVersion = await _catalogService.GetManifestVersionAsync(catalogTenant!.Value, ct).ConfigureAwait(false);
        var allModels = await _catalogService.ListSemanticModelsAsync(catalogTenant.Value, ct).ConfigureAwait(false);
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

        TableMetadata tableMetadata;
        try
        {
            tableMetadata = await ResolveTableMetadataAsync(targetModel, ct).ConfigureAwait(false);
        }
        catch (TableNotFoundException notFound)
        {
            // WP-0 (W0-4): a missing backing table and a denied one are one indistinguishable error for the caller; the true reason stays in the audit.
            await RecordMetricAuditAsync(
                tenantId,
                userSid,
                notFound.Table,
                allowed: false,
                resolvedMetrics.Keys.ToList(),
                catalogVersion,
                new Dictionary<string, object?>
                {
                    ["is_allowed"] = false,
                    ["metrics"] = resolvedMetrics.Keys.ToArray(),
                    ["reasons"] = new[] { "Backing table not found." }
                },
                ct).ConfigureAwait(false);

            throw new GatewayForbiddenException(DeniedMessage);
        }

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
                catalogVersion,
                new Dictionary<string, object?>
                {
                    ["is_allowed"] = false,
                    ["metrics"] = resolvedMetrics.Keys.ToArray(),
                    ["reasons"] = decision.DeniedReasons
                },
                ct).ConfigureAwait(false);

            // WP-0 (W0-4): fixed message without metric names, table name or denial reasons (those are in the audit entry above).
            throw new GatewayForbiddenException(DeniedMessage);
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

        // SEC-112-16 (d): the stored filter template is parsed again here against the current base table, so a manifest stored before a
        // schema change (a column removed, a function disallowed) fails closed at query time instead of reaching the compile path.
        EnsureStoredTemplatesStillParse(resolvedMetrics.Values, tableMetadata);

        // A-8: the typed filter is built after the base table is known (kinds come from the resolved columns); it is a TableFilterClause
        // like the legacy text filter, so both compose through one path.
        var requestWhere = request.TypedWhere is { } typed ? BuildTypedWhere(typed, tableMetadata) : request.Where;
        var combinedWhere = CombineMetricFiltersWithWhere(resolvedMetrics.Values, requestWhere, tableMetadata);

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
                catalogVersion,
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
            ?? catalogVersion
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
            MetricDefinitions: resolvedMetrics.ToDictionary(kv => kv.Key, kv => MetricDefinitionSummary.From(kv.Value), StringComparer.OrdinalIgnoreCase))
        {
            MetricValueKinds = ComputeMetricValueKinds(resolvedMetrics.Values, aggregateSelections, tableMetadata),
            DimensionValueKinds = ComputeDimensionValueKinds(request.GroupBy, tableMetadata)
        };
    }

    private void EnsureStoredTemplatesStillParse(IEnumerable<DbtMetric> metrics, TableMetadata table)
    {
        foreach (var metric in metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.Filter))
            {
                continue;
            }

            var cleaned = CleanDbtFilterTemplate(metric.Filter);
            try
            {
                if (!SemanticManifestValidator.IsSafeTemplateText(cleaned))
                {
                    throw new PolicyParseException("The filter template is not valid.");
                }

                SemanticManifestValidator.TemplateParser.Parse(_policyParser, cleaned, table);
            }
            catch (Exception ex) when (ex is PolicyParseException or ArgumentException or InvalidOperationException)
            {
                // Same fixed error as an unavailable column: the stored template names something the base table no longer offers.
                throw new AggregationColumnUnavailableException();
            }
        }
    }

    private static TableFilterClause BuildTypedWhere(TreeFilter typed, TableMetadata table)
    {
        ColumnRef? Resolve(string name)
        {
            var column = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase));
            return column is null ? null : new ColumnRef(column.ColumnName, ColumnValueKinds.FromDataType(column.DataType));
        }

        EnsureDeclaredKindsMatch(typed, Resolve);
        return TreeFilterClauseBuilder.Build(typed, Resolve);
    }

    /// <summary>A leaf that declares a value kind must fit the resolved column kind (METRIC_FILTER_INVALID); an unknown column is left to the builder.</summary>
    private static void EnsureDeclaredKindsMatch(TreeFilter filter, Func<string, ColumnRef?> resolve)
    {
        switch (filter)
        {
            case TreeAndFilter and:
                foreach (var item in and.Items) EnsureDeclaredKindsMatch(item, resolve);
                break;
            case TreeOrFilter or:
                foreach (var item in or.Items) EnsureDeclaredKindsMatch(item, resolve);
                break;
            case TreeNotFilter not:
                EnsureDeclaredKindsMatch(not.Item, resolve);
                break;
            case TreeComparison { DeclaredKind: { } declared } leaf when resolve(leaf.Column) is { } column:
                if (!DeclaredKindFits(declared, column.Kind))
                {
                    throw new MetricFilterInvalidException();
                }

                break;
        }
    }

    private static bool DeclaredKindFits(ColumnValueKind declared, ColumnValueKind column) =>
        declared == column ||
        (declared is ColumnValueKind.Int or ColumnValueKind.Long && column is ColumnValueKind.Int or ColumnValueKind.Long or ColumnValueKind.Decimal or ColumnValueKind.Float) ||
        (declared is ColumnValueKind.Decimal or ColumnValueKind.Float && column is ColumnValueKind.Decimal or ColumnValueKind.Float);

    private static AggregateValueKind ToAggregateKind(ColumnValueKind kind) => kind switch
    {
        ColumnValueKind.Int or ColumnValueKind.Long => AggregateValueKind.Int,
        ColumnValueKind.Decimal => AggregateValueKind.Decimal,
        ColumnValueKind.Float => AggregateValueKind.Float,
        ColumnValueKind.Boolean => AggregateValueKind.Boolean,
        ColumnValueKind.DateTime => AggregateValueKind.DateTime,
        _ => AggregateValueKind.String
    };

    private static ColumnValueKind? ColumnKind(string? name, TableMetadata table)
    {
        var column = string.IsNullOrWhiteSpace(name)
            ? null
            : table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, name, StringComparison.OrdinalIgnoreCase));
        return column is null ? null : ColumnValueKinds.FromDataType(column.DataType);
    }

    private static AggregateValueKind SelectionKind(AggregateSelection selection, TableMetadata table)
    {
        var column = ColumnKind(selection.Column, table);
        return selection.Function switch
        {
            AggregateFunction.Count or AggregateFunction.CountDistinct => AggregateValueKind.Int,
            AggregateFunction.Avg => AggregateValueKind.Decimal,
            AggregateFunction.Sum => column is ColumnValueKind.Int or ColumnValueKind.Long
                ? AggregateValueKind.Int
                : column is ColumnValueKind.Float ? AggregateValueKind.Float : AggregateValueKind.Decimal,
            _ => column is { } k ? ToAggregateKind(k) : AggregateValueKind.Decimal
        };
    }

    /// <summary>Static kinds per requested metric (so a NULL value still has a kind): counts are INT, ratio and derived metrics are DECIMAL.</summary>
    private static IReadOnlyDictionary<string, AggregateValueKind> ComputeMetricValueKinds(
        IEnumerable<DbtMetric> metrics,
        IReadOnlyList<AggregateSelection> selections,
        TableMetadata table)
    {
        var result = new Dictionary<string, AggregateValueKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in metrics)
        {
            if (IsSimpleOrCumulative(metric.Type) && metric.Measures.Count >= 1)
            {
                var selection = selections.FirstOrDefault(a => string.Equals(a.EffectiveAlias, metric.Name, StringComparison.OrdinalIgnoreCase))
                    ?? selections.FirstOrDefault(a => string.Equals(a.EffectiveAlias, metric.Measures[0], StringComparison.OrdinalIgnoreCase));
                result[metric.Name] = selection is null ? AggregateValueKind.Decimal : SelectionKind(selection, table);
            }
            else
            {
                result[metric.Name] = AggregateValueKind.Decimal;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, AggregateValueKind> ComputeDimensionValueKinds(IReadOnlyList<string>? groupBy, TableMetadata table)
    {
        var result = new Dictionary<string, AggregateValueKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in groupBy ?? [])
        {
            result[name] = ColumnKind(name, table) is { } kind ? ToAggregateKind(kind) : AggregateValueKind.String;
        }

        return result;
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

    internal static (DbtSemanticModel Model, List<DbtMeasure> Measures) ResolveModelAndMeasures(
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
        var resolver = new Autheris.Application.Dbt.Services.SemanticBaseTableResolver(_metadataRepository);
        return await resolver.TryResolveAsync(model, isVisible: null, ct).ConfigureAwait(false)
            ?? throw new TableNotFoundException(Autheris.Application.Dbt.Services.SemanticBaseTableResolver.Candidates(model).FirstOrDefault());
    }
}

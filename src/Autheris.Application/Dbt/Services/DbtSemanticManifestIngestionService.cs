namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Application.Analytics;
using Autheris.Application.Policy.Lint;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>The manifest failed validation; nothing was changed. Carries no manifest content.</summary>
public sealed class SemanticManifestInvalidException : Exception
{
    public SemanticManifestInvalidException(IReadOnlyList<string> errorCodes)
        : base("The semantic manifest is invalid.")
    {
        ErrorCodes = errorCodes;
    }

    /// <summary>Fixed validation codes (for the log), never names or template text.</summary>
    public IReadOnlyList<string> ErrorCodes { get; }
}

/// <summary>The manifest was persisted and swapped in, but a virtual-table upsert failed; a re-import repairs it (M-4).</summary>
public sealed class SemanticManifestPartialException : Exception
{
    public SemanticManifestPartialException(Exception inner)
        : base("The semantic manifest was stored but its catalog tables were not fully updated.", inner)
    {
    }
}

public sealed class DbtSemanticManifestIngestionService : IDbtSemanticManifestIngestionService
{
    private readonly ISemanticCatalogService _catalogService;
    private readonly ILogger<DbtSemanticManifestIngestionService> _logger;
    private readonly ITableMetadataRepository? _tableRepository;
    private readonly Autheris.Application.Governance.Interfaces.IDatasetTagImportService? _tagImport;
    private readonly SemanticManifestValidator _validator;
    private readonly SemanticTenancy _tenancy;
    private readonly IConsentRepository? _consentRepository;
    private readonly IReadOnlyCollection<string> _sharedTables;
    private static readonly string?[] SemanticModelTags = ["dbt", "semantic-model"];

    public DbtSemanticManifestIngestionService(
        ISemanticCatalogService catalogService,
        ILogger<DbtSemanticManifestIngestionService> logger,
        ITableMetadataRepository? tableRepository = null,
        Autheris.Application.Governance.Interfaces.IDatasetTagImportService? tagImport = null,
        SemanticManifestValidator? validator = null,
        IOptions<GatewayOptions>? gatewayOptions = null,
        IConsentRepository? consentRepository = null)
    {
        _tagImport = tagImport;
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tableRepository = tableRepository;
        _validator = validator ?? new SemanticManifestValidator(PolicyParserSetup.CreateParser(), Options.Create(new SemanticOptions()));
        _tenancy = SemanticTenancy.FromOptions(gatewayOptions);
        _consentRepository = consentRepository ?? (tableRepository as IConsentRepository);
        _sharedTables = gatewayOptions?.Value.Catalog.SharedTables ?? [];
    }

    public async Task<DbtSemanticManifest> ParseAndIngestAsync(string manifestJson, TenantId tenantId, string? importedBy = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestJson);
        if (!_tenancy.TryMapKey(tenantId, out _))
        {
            // SEC-112-13: a tenant without catalog standing is refused before any parsing or side effect.
            throw new GatewayForbiddenException();
        }

        var manifest = Parse(manifestJson);
        var baseTables = await ResolveBaseTablesAsync(manifest, tenantId, cancellationToken).ConfigureAwait(false);
        var validation = _validator.Validate(manifest, baseTables);
        if (!validation.IsValid)
        {
            _logger.LogWarning("Rejected a semantic manifest import of tenant {TenantId} ({ErrorCodes}).", tenantId.Value, string.Join(',', validation.Errors));
            throw new SemanticManifestInvalidException(validation.Errors);
        }

        // Persist and swap the catalog first (atomic, all or nothing); the idempotent virtual-table upserts follow (M-4).
        await _catalogService.RegisterManifestAsync(manifest, tenantId, importedBy, cancellationToken).ConfigureAwait(false);

        try
        {
            await UpsertVirtualTablesAsync(manifest.SemanticModels, tenantId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The semantic manifest of tenant {TenantId} was stored but a virtual table upsert failed; a re-import repairs it.", tenantId.Value);
            throw new SemanticManifestPartialException(ex);
        }

        _logger.LogInformation("Successfully ingested dbt semantic manifest with {ModelCount} models and {MetricCount} metrics",
            manifest.SemanticModels.Count, manifest.Metrics.Count);

        return manifest;
    }

    private async Task<IReadOnlyDictionary<string, TableMetadata>> ResolveBaseTablesAsync(DbtSemanticManifest manifest, TenantId tenantId, CancellationToken ct)
    {
        var result = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
        if (_tableRepository is null)
        {
            return result;
        }

        var needed = new Dictionary<string, DbtSemanticModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var metric in manifest.Metrics.Where(m => !string.IsNullOrWhiteSpace(m.Filter)))
        {
            try
            {
                var model = SemanticMetricQueryService.ResolveModelAndMeasures(metric, manifest.SemanticModels).Model;
                needed[model.Name] = model;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The validator reports the unresolvable metric.
            }
        }

        if (needed.Count == 0)
        {
            return result;
        }

        var isVisible = await BuildVisibilityAsync(tenantId, ct).ConfigureAwait(false);
        var resolver = new SemanticBaseTableResolver(_tableRepository);
        foreach (var model in needed.Values)
        {
            if (await resolver.TryResolveAsync(model, isVisible, ct).ConfigureAwait(false) is { } table)
            {
                result[model.Name] = table;
            }
        }

        return result;
    }

    /// <summary>SEC-112-17: the importer's own scope. Single mode is unrestricted; Multi sees only tables the tenant holds consents on or that are shared.</summary>
    private async Task<Func<TableMetadata, bool>?> BuildVisibilityAsync(TenantId tenantId, CancellationToken ct)
    {
        if (_tenancy.Mode == TenancyMode.Single)
        {
            return null;
        }

        if (_consentRepository is null)
        {
            return static _ => false;
        }

        var consentTables = await _consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct).ConfigureAwait(false);
        var allowed = new HashSet<TableIdentifier>(consentTables);
        foreach (var shared in _sharedTables)
        {
            if (TableIdentifier.TryParse(shared, out var id))
            {
                allowed.Add(id);
            }
        }

        return t => allowed.Contains(t.Identifier);
    }

    private static DbtSemanticManifest Parse(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var root = doc.RootElement;

        var semanticModels = new List<DbtSemanticModel>();
        if (root.TryGetProperty("semantic_models", out var modelsElem) && modelsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in modelsElem.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desc = m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var nodeRel = m.TryGetProperty("node_relation", out var nr) && nr.TryGetProperty("relation_name", out var rn)
                    ? rn.GetString() ?? ""
                    : "";

                var entities = new List<DbtEntity>();
                if (m.TryGetProperty("entities", out var entitiesElem) && entitiesElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in entitiesElem.EnumerateArray())
                    {
                        var eName = e.TryGetProperty("name", out var en) ? en.GetString() ?? "" : "";
                        var eType = e.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
                        var eRole = e.TryGetProperty("role", out var er) ? er.GetString() : null;
                        entities.Add(new DbtEntity(eName, eType, eRole));
                    }
                }

                var measures = new List<DbtMeasure>();
                if (m.TryGetProperty("measures", out var measuresElem) && measuresElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ms in measuresElem.EnumerateArray())
                    {
                        var msName = ms.TryGetProperty("name", out var msn) ? msn.GetString() ?? "" : "";
                        var agg = ms.TryGetProperty("agg", out var ag) ? ag.GetString() ?? "sum" : "sum";
                        var expr = ms.TryGetProperty("expr", out var ex) ? ex.GetString() : null;
                        var msDesc = ms.TryGetProperty("description", out var msd) ? msd.GetString() : null;
                        string? msUnit = ms.TryGetProperty("unit", out var msu) && msu.ValueKind == JsonValueKind.String ? msu.GetString() : null;
                        int? msMinGroupSize = ms.TryGetProperty("min_group_size", out var msmg) && msmg.TryGetInt32(out var msmgVal) ? msmgVal : null;
                        measures.Add(new DbtMeasure(msName, agg, expr, msDesc, msUnit, msMinGroupSize));
                    }
                }

                var dimensions = new List<DbtDimension>();
                if (m.TryGetProperty("dimensions", out var dimsElem) && dimsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var dim in dimsElem.EnumerateArray())
                    {
                        var dimName = dim.TryGetProperty("name", out var dn) ? dn.GetString() ?? "" : "";
                        var dimType = dim.TryGetProperty("type", out var dt) ? dt.GetString() ?? "categorical" : "categorical";
                        var dimDesc = dim.TryGetProperty("description", out var dd) ? dd.GetString() : null;
                        var isPart = dim.TryGetProperty("is_partition", out var ip) && ip.GetBoolean();
                        string? dimTimeGranularity = null;
                        if (dim.TryGetProperty("type_params", out var dtp) && dtp.ValueKind == JsonValueKind.Object &&
                            dtp.TryGetProperty("time_granularity", out var dtg) && dtg.ValueKind == JsonValueKind.String)
                        {
                            dimTimeGranularity = dtg.GetString();
                        }
                        dimensions.Add(new DbtDimension(dimName, dimType, dimDesc, isPart, TimeGranularity: dimTimeGranularity));
                    }
                }

                semanticModels.Add(new DbtSemanticModel(name, desc, nodeRel, entities, measures, dimensions));
            }
        }

        string manifestVersion = "1.0";
        if (root.TryGetProperty("version", out var vElem) && vElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(vElem.GetString()))
        {
            manifestVersion = vElem.GetString()!;
        }
        else if (root.TryGetProperty("project_configuration", out var pcElem) && pcElem.ValueKind == JsonValueKind.Object &&
                 pcElem.TryGetProperty("version", out var pcvElem) && pcvElem.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pcvElem.GetString()))
        {
            manifestVersion = pcvElem.GetString()!;
        }

        var metrics = new List<DbtMetric>();
        if (root.TryGetProperty("metrics", out var metricsElem) && metricsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var mt in metricsElem.EnumerateArray())
            {
                var name = mt.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                var type = mt.TryGetProperty("type", out var ty) ? ty.GetString() ?? "simple" : "simple";
                var desc = mt.TryGetProperty("description", out var md) ? md.GetString() : null;
                var label = mt.TryGetProperty("label", out var ml) ? ml.GetString() : null;
                var filter = mt.TryGetProperty("filter", out var mf)
                    ? (mf.ValueKind == JsonValueKind.String ? mf.GetString() : mf.ToString())
                    : null;

                var dims = new List<string>();
                if (mt.TryGetProperty("dimensions", out var de) && de.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in de.EnumerateArray())
                    {
                        if (d.GetString() is { } ds) dims.Add(ds);
                    }
                }

                string? typeParamsExpr = null;
                var measures = new List<string>();
                if (mt.TryGetProperty("type_params", out var tp) && tp.ValueKind == JsonValueKind.Object)
                {
                    if (tp.TryGetProperty("expr", out var tpExpr) && tpExpr.ValueKind == JsonValueKind.String)
                    {
                        typeParamsExpr = tpExpr.GetString();
                    }

                    if (tp.TryGetProperty("measure", out var meas))
                    {
                        if (meas.ValueKind == JsonValueKind.String && meas.GetString() is { } msStr)
                        {
                            measures.Add(msStr);
                        }
                        else if (meas.ValueKind == JsonValueKind.Object && meas.TryGetProperty("name", out var mnProp) && mnProp.GetString() is { } mnpStr)
                        {
                            measures.Add(mnpStr);
                        }
                    }

                    if (tp.TryGetProperty("measures", out var measArr) && measArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var mItem in measArr.EnumerateArray())
                        {
                            if (mItem.ValueKind == JsonValueKind.String && mItem.GetString() is { } mStr)
                            {
                                measures.Add(mStr);
                            }
                            else if (mItem.ValueKind == JsonValueKind.Object && mItem.TryGetProperty("name", out var mName) && mName.GetString() is { } mnStr)
                            {
                                measures.Add(mnStr);
                            }
                        }
                    }

                    if (tp.TryGetProperty("numerator", out var numProp))
                    {
                        var numName = numProp.ValueKind == JsonValueKind.String
                            ? numProp.GetString()
                            : numProp.ValueKind == JsonValueKind.Object && numProp.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(numName))
                        {
                            measures.Add(numName!);
                        }
                    }

                    if (tp.TryGetProperty("denominator", out var denProp))
                    {
                        var denName = denProp.ValueKind == JsonValueKind.String
                            ? denProp.GetString()
                            : denProp.ValueKind == JsonValueKind.Object && denProp.TryGetProperty("name", out var dn) ? dn.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(denName))
                        {
                            measures.Add(denName!);
                        }
                    }
                }

                var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (mt.TryGetProperty("meta", out var metaElem) && metaElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in metaElem.EnumerateObject())
                    {
                        meta[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
                    }
                }

                int? metricMinGroupSize = null;
                if (mt.TryGetProperty("min_group_size", out var mgProp) && mgProp.TryGetInt32(out var mgVal))
                {
                    metricMinGroupSize = mgVal;
                }
                else if (meta.TryGetValue("min_group_size", out var metaMg) && int.TryParse(metaMg, out var metaMgVal))
                {
                    metricMinGroupSize = metaMgVal;
                }

                IReadOnlyList<string>? allowedGranularities = null;
                if (mt.TryGetProperty("allowed_time_granularities", out var atgProp) && atgProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var g in atgProp.EnumerateArray())
                    {
                        if (g.ValueKind == JsonValueKind.String && g.GetString() is { } gs)
                        {
                            list.Add(gs);
                        }
                    }
                    allowedGranularities = list;
                }

                var unit = mt.TryGetProperty("unit", out var uProp) && uProp.ValueKind == JsonValueKind.String
                    ? uProp.GetString()
                    : meta.TryGetValue("unit", out var metaUnit) ? metaUnit : null;

                metrics.Add(new DbtMetric(
                    name,
                    type,
                    desc,
                    label,
                    filter,
                    typeParamsExpr,
                    dims,
                    measures,
                    meta,
                    Unit: unit,
                    MinGroupSize: metricMinGroupSize,
                    AllowedTimeGranularities: allowedGranularities,
                    Version: manifestVersion));
            }
        }

        return new DbtSemanticManifest(semanticModels, metrics, manifestVersion);
    }

    private async Task UpsertVirtualTablesAsync(IReadOnlyList<DbtSemanticModel> semanticModels, TenantId tenantId, CancellationToken cancellationToken)
    {
        if (_tableRepository != null)
        {
            foreach (var model in semanticModels)
            {
                if (string.IsNullOrWhiteSpace(model.Name))
                {
                    continue;
                }

                var columns = new List<TableColumn>();
                var primaryKeys = new List<string>();
                var seenCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entity in model.Entities)
                {
                    if (string.IsNullOrWhiteSpace(entity.Name) || !seenCols.Add(entity.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = entity.Name,
                        DataType = "varchar",
                        Description = entity.Role ?? $"Entity ({entity.Type})",
                        DocumentationSource = "dbt"
                    });

                    if (string.Equals(entity.Type, "primary", StringComparison.OrdinalIgnoreCase))
                    {
                        primaryKeys.Add(entity.Name);
                    }
                }

                foreach (var dim in model.Dimensions)
                {
                    if (string.IsNullOrWhiteSpace(dim.Name) || !seenCols.Add(dim.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = dim.Name,
                        DataType = string.Equals(dim.Type, "time", StringComparison.OrdinalIgnoreCase) ? "timestamp" : "varchar",
                        Description = dim.Description,
                        DocumentationSource = "dbt"
                    });
                }

                foreach (var measure in model.Measures)
                {
                    if (string.IsNullOrWhiteSpace(measure.Name) || !seenCols.Add(measure.Name))
                    {
                        continue;
                    }

                    columns.Add(new TableColumn
                    {
                        ColumnName = measure.Name,
                        DataType = measure.Aggregation.Contains("count", StringComparison.OrdinalIgnoreCase) ? "bigint" : "decimal",
                        Description = measure.Description,
                        DocumentationSource = "dbt"
                    });
                }

                if (primaryKeys.Count == 0 && columns.Count > 0)
                {
                    primaryKeys.Add(columns[0].ColumnName);
                }

                var tableMeta = new TableMetadata
                {
                    Identifier = SemanticVirtualTables.IdentifierFor(tenantId, _tenancy.Mode, model.Name),
                    Table = new Table
                    {
                        SchemaName = SemanticVirtualTables.SchemaFor(tenantId, _tenancy.Mode),
                        TableName = model.Name,
                        DisplayName = model.Name,
                        Description = model.Description,
                        DocumentationSource = SemanticVirtualTables.SourceMarker,
                        IsActive = true
                    },
                    Columns = columns,
                    PrimaryKeyColumns = primaryKeys
                };

                await _tableRepository.UpsertTableMetadataAsync(tableMeta, cancellationToken).ConfigureAwait(false);

                // Issue #111 (A-2): the upsert ignores Table.Tags; the synthetic tags become dbt-source dataset tags (same cap and audit as dbt model tags).
                if (_tagImport is not null)
                {
                    await _tagImport.ImportDbtTagsAsync(tableMeta.Identifier, SemanticModelTags, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
